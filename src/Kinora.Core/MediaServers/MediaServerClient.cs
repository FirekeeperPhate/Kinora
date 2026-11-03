using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kinora.Core.MediaServers;

public sealed class MediaServerException(string message, Exception? inner = null) : Exception(message, inner);

public enum MediaServerKind
{
    Emby,
    Jellyfin,
}

public sealed record ServerInfo(MediaServerKind Kind, string Name, string Version);

public sealed record ServerUser(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A movie, episode or show as the server has it, with what this user has seen of it.</summary>
/// <param name="LastPlayed">Unix seconds; 0 when it never was.</param>
public sealed record ServerItem(string Id, string Type, string? Path, string Name, int? Year,
                                string? ImdbId, string? TmdbId, string? TvdbId,
                                string? SeriesId, string? SeriesName, int? Season, int? Number,
                                bool Played, long PositionMs, long RunTimeMs, long LastPlayed);

/// <summary>
/// The few calls of the Emby and Jellyfin API the sync needs: they share most of it, and where
/// Jellyfin has moved a route the old one is tried after the new.
/// </summary>
public sealed class MediaServerClient : IDisposable
{
    private const int PageSize = 500;
    private const long TicksPerMs = 10_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _key;
    private string _prefix = "";
    private MediaServerKind _kind;

    public MediaServerClient(string url, string key, HttpMessageHandler? handler = null)
    {
        _base = CleanUrl(url);
        _key = key.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kinora/0.1");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", _key);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>"nas:8096", or an address copied from the browser with the web page in it: scheme, host, port.</summary>
    public static string CleanUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new MediaServerException("That doesn't look like the address of a server.");
        var path = uri.AbsolutePath;
        var web = path.IndexOf("/web", StringComparison.OrdinalIgnoreCase);
        if (web >= 0)
            path = path[..web];
        return uri.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/');
    }

    /// <summary>Which server it is. Emby also answers under /emby, and a Jellyfin behind a proxy often under /jellyfin.</summary>
    public async Task<ServerInfo> ConnectAsync(CancellationToken ct)
    {
        foreach (var prefix in new[] { "", "/emby", "/jellyfin" })
        {
            using var response = await _http.GetAsync($"{_base}{prefix}/System/Info/Public", ct);
            if (!response.IsSuccessStatusCode)
                continue;
            JsonNode? info;
            try
            {
                info = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
                continue;   // a web page, not the API
            }
            if (info is not JsonObject || info["Version"] is null)
                continue;
            _prefix = prefix;
            var product = info["ProductName"]?.GetValue<string>() ?? "";
            _kind = product.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase) ? MediaServerKind.Jellyfin : MediaServerKind.Emby;
            if (_kind == MediaServerKind.Jellyfin)
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{_key}\"");
            return new ServerInfo(_kind, info["ServerName"]?.GetValue<string>() ?? "", info["Version"]!.GetValue<string>());
        }
        throw new MediaServerException("No Emby or Jellyfin server answers at that address.");
    }

    public async Task<List<ServerUser>> GetUsersAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, ["/Users"], null, ct);
        var users = JsonSerializer.Deserialize<List<UserDto>>(await response.Content.ReadAsStringAsync(ct), Json) ?? [];
        return users.Select(u => new ServerUser(u.Id, u.Name)).OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Every movie, episode and show of the library, a page at a time.</summary>
    public async Task<List<ServerItem>> GetItemsAsync(string user, CancellationToken ct)
    {
        const string query = "Recursive=true&IncludeItemTypes=Movie,Episode,Series&Fields=Path,ProviderIds&EnableUserData=true&EnableImages=false&SortBy=SortName,Id";
        var id = Uri.EscapeDataString(user);
        var items = new List<ServerItem>();
        while (true)
        {
            var paging = $"{query}&StartIndex={items.Count}&Limit={PageSize}";
            string[] routes = _kind == MediaServerKind.Jellyfin
                ? [$"/Items?userId={id}&{paging}", $"/Users/{id}/Items?{paging}"]
                : [$"/Users/{id}/Items?{paging}"];
            using var response = await SendAsync(HttpMethod.Get, routes, null, ct);
            var page = JsonSerializer.Deserialize<PageDto>(await response.Content.ReadAsStringAsync(ct), Json);
            if (page?.Items is not { Count: > 0 })
                return items;
            items.AddRange(page.Items.Select(ToItem));
            if (items.Count >= page.TotalRecordCount)
                return items;
        }
    }

    /// <summary>Some titles by their id on the server: what a sync of one page needs. Titles gone from the server are left out.</summary>
    public async Task<List<ServerItem>> GetItemsByIdsAsync(string user, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        const string query = "Fields=Path,ProviderIds&EnableUserData=true&EnableImages=false";
        var u = Uri.EscapeDataString(user);
        var items = new List<ServerItem>();
        foreach (var chunk in ids.Distinct().Chunk(100))   // the address stays short enough for any proxy
        {
            var list = string.Join(',', chunk.Select(Uri.EscapeDataString));
            string[] routes = _kind == MediaServerKind.Jellyfin
                ? [$"/Items?userId={u}&ids={list}&{query}", $"/Users/{u}/Items?Ids={list}&{query}"]
                : [$"/Users/{u}/Items?Ids={list}&{query}"];
            using var response = await SendAsync(HttpMethod.Get, routes, null, ct);
            var page = JsonSerializer.Deserialize<PageDto>(await response.Content.ReadAsStringAsync(ct), Json);
            items.AddRange((page?.Items ?? []).Select(ToItem));
        }
        return items;
    }

    /// <summary>
    /// Where the intro is, when the server has found it: Emby keeps it in hidden chapter markers
    /// (IntroStart, IntroEnd), Jellyfin 10.10 in media segments. Null when it doesn't know.
    /// </summary>
    public async Task<(long StartMs, long EndMs)?> GetIntroAsync(string user, string item, CancellationToken ct)
    {
        var (u, i) = (Uri.EscapeDataString(user), Uri.EscapeDataString(item));
        if (_kind == MediaServerKind.Jellyfin)
        {
            try
            {
                using var segments = await SendAsync(HttpMethod.Get, [$"/MediaSegments/{i}?includeSegmentTypes=Intro"], null, ct);
                var root = JsonNode.Parse(await segments.Content.ReadAsStringAsync(ct));
                foreach (var segment in root?["Items"]?.AsArray() ?? [])
                    if (segment?["Type"]?.GetValue<string>() is "Intro" && Ticks(segment["StartTicks"]) is { } start && Ticks(segment["EndTicks"]) is { } end && end > start)
                        return (start / TicksPerMs, end / TicksPerMs);
                return null;
            }
            catch (MediaServerException)
            {
                return null;   // older than 10.10
            }
        }

        string[] routes = [$"/Users/{u}/Items/{i}?Fields=Chapters"];
        using var response = await SendAsync(HttpMethod.Get, routes, null, ct);
        var chapters = (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["Chapters"]?.AsArray() ?? []).ToList();
        long? Marker(string type) => chapters.Where(c => c?["MarkerType"]?.GetValue<string>() == type)
                                             .Select(c => Ticks(c!["StartPositionTicks"])).FirstOrDefault();
        return Marker("IntroStart") is { } from && Marker("IntroEnd") is { } to && to > from ? (from / TicksPerMs, to / TicksPerMs) : null;
    }

    private static long? Ticks(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var ticks) ? ticks : null;

    private static ServerItem ToItem(ItemDto d)
    {
        var ids = new Dictionary<string, string>(d.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
        var data = d.UserData;
        return new ServerItem(d.Id, d.Type, d.Path, d.Name ?? "", d.ProductionYear,
                              ids.GetValueOrDefault("Imdb"), ids.GetValueOrDefault("Tmdb"), ids.GetValueOrDefault("Tvdb"),
                              d.SeriesId, d.SeriesName, d.ParentIndexNumber, d.IndexNumber,
                              data?.Played ?? false, (data?.PlaybackPositionTicks ?? 0) / TicksPerMs, (d.RunTimeTicks ?? 0) / TicksPerMs,
                              DateTimeOffset.TryParse(data?.LastPlayedDate, out var played) ? played.ToUnixTimeSeconds() : 0);
    }

    public async Task SetPlayedAsync(string user, string item, bool played, CancellationToken ct)
    {
        var (u, i) = (Uri.EscapeDataString(user), Uri.EscapeDataString(item));
        string[] routes = _kind == MediaServerKind.Jellyfin
            ? [$"/UserPlayedItems/{i}?userId={u}", $"/Users/{u}/PlayedItems/{i}"]
            : [$"/Users/{u}/PlayedItems/{i}"];
        using var response = await SendAsync(played ? HttpMethod.Post : HttpMethod.Delete, routes, null, ct);
    }

    /// <summary>
    /// The place to resume from. Emby takes the user's data for an item as a whole, so it is read
    /// first: only the position changes, favourites and play counts stay.
    /// </summary>
    public async Task SetPositionAsync(string user, string item, long positionMs, CancellationToken ct)
    {
        var (u, i) = (Uri.EscapeDataString(user), Uri.EscapeDataString(item));
        var itemRoute = $"/Users/{u}/Items/{i}";
        string[] reads = _kind == MediaServerKind.Jellyfin ? [$"/UserItems/{i}/UserData?userId={u}", itemRoute] : [itemRoute];
        string[] writes = _kind == MediaServerKind.Jellyfin ? [$"/UserItems/{i}/UserData?userId={u}", itemRoute + "/UserData"] : [itemRoute + "/UserData"];

        JsonObject current;
        using (var read = await SendAsync(HttpMethod.Get, reads, null, ct))
        {
            // The data alone, or the item with the data inside
            var root = JsonNode.Parse(await read.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
            current = root["UserData"] is JsonObject data ? data.DeepClone().AsObject() : root;
        }
        current["PlaybackPositionTicks"] = positionMs * TicksPerMs;
        using var response = await SendAsync(HttpMethod.Post, writes, current.ToJsonString(), ct);
    }

    /// <summary>The routes in order: a 404 means this version has the next one.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, IReadOnlyList<string> routes, string? json, CancellationToken ct)
    {
        for (var n = 0; ; n++)
        {
            using var request = new HttpRequestMessage(method, _base + _prefix + routes[n]);
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return response;
            var status = response.StatusCode;
            response.Dispose();
            if (status == HttpStatusCode.NotFound && n + 1 < routes.Count)
                continue;
            throw new MediaServerException(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "The server refused this API key."
                : $"The server answered {(int)status} {status}.");
        }
    }

    private sealed record UserDto(string Id, string Name);

    private sealed class PageDto
    {
        public List<ItemDto>? Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    private sealed class ItemDto
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Name { get; set; }
        public string? Path { get; set; }
        public int? ProductionYear { get; set; }
        public long? RunTimeTicks { get; set; }
        public Dictionary<string, string>? ProviderIds { get; set; }
        public string? SeriesId { get; set; }
        public string? SeriesName { get; set; }
        public int? ParentIndexNumber { get; set; }
        public int? IndexNumber { get; set; }
        public UserDataDto? UserData { get; set; }
    }

    private sealed class UserDataDto
    {
        public bool Played { get; set; }
        public long PlaybackPositionTicks { get; set; }
        public string? LastPlayedDate { get; set; }
    }
}
