using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kinora.Core.Arr;

public enum ArrKind
{
    Radarr,
    Sonarr,
}

public sealed class ArrException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ArrStatus(string AppName, string Version, string? InstanceName);

/// <summary>A movie of Radarr or a show of Sonarr: its id there, and its title as it has it.</summary>
public sealed record ArrTitle(int Id, string Title);

/// <summary>
/// The few calls of Radarr's and Sonarr's API (v3, the same for both) that keep them from
/// downloading again what Kinora deletes: find the title by its ids, remove it without touching
/// the files, stop monitoring an episode. Titles are found by IMDb, TMDB and TVDB ids, never by
/// path: Radarr on the NAS sees "/movies/Heat (1995)" where Kinora sees "M:\Heat (1995)".
/// </summary>
public sealed class ArrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _key;
    private string _base;
    private bool _connected;

    public ArrKind Kind { get; }

    public ArrClient(ArrKind kind, string url, string key, HttpMessageHandler? handler = null)
    {
        Kind = kind;
        _base = CleanUrl(url);
        _key = key.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kinora/0.1");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", _key);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>"nas:7878", "http://nas:8989/sonarr/", or an address copied from the browser.</summary>
    public static string CleanUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArrException("That doesn't look like the address of a server.");
        var path = uri.AbsolutePath.TrimEnd('/');
        var api = path.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        if (api >= 0)
            path = path[..api];
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    /// <summary>
    /// Who answers. The address as written first (Radarr behind a proxy under "/radarr"), then
    /// only its host and port (a page copied from the browser, "http://nas:7878/settings").
    /// </summary>
    public async Task<ArrStatus> TestAsync(CancellationToken ct)
    {
        var bases = new List<string> { _base };
        var root = new Uri(_base).GetLeftPart(UriPartial.Authority);
        if (root != _base)
            bases.Add(root);
        foreach (var b in bases)
        {
            using var response = await _http.GetAsync($"{b}/api/v3/system/status", ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ArrException($"{Kind} refused this key.");
            if (!response.IsSuccessStatusCode)
                continue;
            JsonNode? status;
            try
            {
                status = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
                continue;   // a web page, not the API
            }
            if (status is not JsonObject || status["version"] is null)
                continue;
            var app = Str(status["appName"]) ?? "";
            if (app.Length > 0 && !app.Equals(Kind.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ArrException($"That is {app}, not {Kind}.");
            _base = b;
            _connected = true;
            return new ArrStatus(app.Length > 0 ? app : Kind.ToString(), Str(status["version"]) ?? "", Str(status["instanceName"]));
        }
        throw new ArrException($"No {Kind} answers at that address.");
    }

    // ---- Radarr ----

    /// <summary>The movie with this TMDB id, or else this IMDb id; null when Radarr hasn't got it.</summary>
    public async Task<ArrTitle?> FindMovieAsync(string? tmdbId, string? imdbId, CancellationToken ct)
    {
        Require(ArrKind.Radarr);
        if (Int(tmdbId) is { } tmdb && Pick(await GetArrayAsync($"/api/v3/movie?tmdbId={tmdb}", ct), m => Int(m["tmdbId"]) == tmdb) is { } byTmdb)
            return byTmdb;
        if (string.IsNullOrWhiteSpace(imdbId))
            return null;
        return Pick(await GetArrayAsync("/api/v3/movie", ct), m => Same(Str(m["imdbId"]), imdbId));
    }

    // ---- Sonarr ----

    /// <summary>The show with this TVDB id, or else this IMDb or TMDB id; null when Sonarr hasn't got it.</summary>
    public async Task<ArrTitle?> FindSeriesAsync(string? tvdbId, string? imdbId, string? tmdbId, CancellationToken ct)
    {
        Require(ArrKind.Sonarr);
        if (Int(tvdbId) is { } tvdb && Pick(await GetArrayAsync($"/api/v3/series?tvdbId={tvdb}", ct), s => Int(s["tvdbId"]) == tvdb) is { } byTvdb)
            return byTvdb;
        var tmdb = Int(tmdbId);
        if (string.IsNullOrWhiteSpace(imdbId) && tmdb is null)
            return null;
        var all = await GetArrayAsync("/api/v3/series", ct);
        return Pick(all, s => !string.IsNullOrWhiteSpace(imdbId) && Same(Str(s["imdbId"]), imdbId))
               ?? (tmdb is null ? null : Pick(all, s => Int(s["tmdbId"]) == tmdb));
    }

    /// <summary>
    /// Stops monitoring these episodes, so Sonarr doesn't download them again. Says how many it
    /// found; the ones it was already not monitoring count too.
    /// </summary>
    public async Task<int> UnmonitorEpisodesAsync(int seriesId, int season, IReadOnlyCollection<int> numbers, CancellationToken ct)
    {
        Require(ArrKind.Sonarr);
        var ids = (await GetArrayAsync($"/api/v3/episode?seriesId={seriesId}", ct)).OfType<JsonObject>()
                  .Where(e => Int(e["seasonNumber"]) == season && Int(e["episodeNumber"]) is { } n && numbers.Contains(n))
                  .Select(e => Int(e["id"])!.Value)
                  .ToList();
        if (ids.Count == 0)
            return 0;
        var body = new JsonObject { ["episodeIds"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["monitored"] = false };
        await SendAsync(HttpMethod.Put, "/api/v3/episode/monitor", body.ToJsonString(), ct);
        return ids.Count;
    }

    // ---- both ----

    /// <summary>
    /// Removes the movie or show, leaving its files alone (Kinora has deleted them already);
    /// <paramref name="exclude"/> also keeps import lists from adding it back.
    /// </summary>
    public async Task RemoveAsync(ArrTitle title, bool exclude, CancellationToken ct)
    {
        var (what, flag) = Kind == ArrKind.Radarr ? ("movie", "addImportExclusion") : ("series", "addImportListExclusion");
        var x = exclude ? "true" : "false";
        await SendAsync(HttpMethod.Delete, $"/api/v3/{what}/{title.Id}?deleteFiles=false&{flag}={x}", null, ct);
    }

    // ---- plumbing ----

    private void Require(ArrKind kind)
    {
        if (Kind != kind)
            throw new InvalidOperationException($"This is a {Kind} client.");
    }

    private async Task<JsonArray> GetArrayAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray
                   ?? throw new ArrException($"{Kind} gave an answer Kinora doesn't understand.");
        }
        catch (JsonException e)
        {
            throw new ArrException($"{Kind} gave an answer Kinora doesn't understand.", e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        if (!_connected)
            await TestAsync(ct);
        using var request = new HttpRequestMessage(method, _base + path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;
        var code = (int)response.StatusCode;
        response.Dispose();
        throw new ArrException(code == 401 ? $"{Kind} refused the key." : $"{Kind} answered {code} to {method} {path.Split('?')[0]}.");
    }

    private static ArrTitle? Pick(JsonArray items, Func<JsonObject, bool> match)
    {
        var found = items.OfType<JsonObject>().Where(match).Take(2).ToList();
        // Two titles with one id would be Radarr's own mess: removing either could be the wrong one
        return found.Count == 1 && Int(found[0]["id"]) is { } id ? new ArrTitle(id, Str(found[0]["title"]) ?? "") : null;
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static int? Int(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0 ? i : null;
}
