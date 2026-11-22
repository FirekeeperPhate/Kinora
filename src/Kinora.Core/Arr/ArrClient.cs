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

public sealed class ArrException(string message, Exception? inner = null, HttpStatusCode? status = null) : Exception(message, inner)
{
    /// <summary>What the server answered, when it answered.</summary>
    public HttpStatusCode? Status { get; } = status;
}

public sealed record ArrStatus(string AppName, string Version, string? InstanceName);

/// <summary>A movie of Radarr or a show of Sonarr: its id there, and its title as it has it.</summary>
public sealed record ArrTitle(int Id, string Title);

/// <summary>A quality profile.</summary>
public sealed record ArrChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>What a title is added with; a profile or folder that isn't there any more means the first one.</summary>
public sealed record ArrAddOptions(int? ProfileId, string? RootFolder, string Monitor = "all", bool Search = true);

/// <summary>The title added, or the one it had already.</summary>
public sealed record ArrAdded(ArrTitle Title, bool Already);

/// <summary>
/// Something of the calendar: an episode (<see cref="What"/> is "S02E05") or a movie coming out
/// (<see cref="What"/> is how: "Digital release"). Radarr's have no hour.
/// </summary>
public sealed record ArrRelease(DateTimeOffset When, bool HasTime, ArrKind Kind, string Title, string What, string? EpisodeTitle,
                                bool HasFile, string? TmdbId, string? ImdbId, string? TvdbId, int? Year);

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
    private string _version = "";
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
            _version = Str(status["version"]) ?? "";
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

    // ---- adding ----

    /// <summary>The quality profiles, for the settings and for adding.</summary>
    public async Task<List<ArrChoice>> GetProfilesAsync(CancellationToken ct) =>
        (await GetArrayAsync("/api/v3/qualityprofile", ct)).OfType<JsonObject>()
            .Where(p => Int(p["id"]) is not null)
            .Select(p => new ArrChoice(Int(p["id"])!.Value, Str(p["name"]) ?? ""))
            .ToList();

    /// <summary>The folders it puts what it downloads in, as it sees them ("/movies").</summary>
    public async Task<List<string>> GetRootFoldersAsync(CancellationToken ct) =>
        (await GetArrayAsync("/api/v3/rootfolder", ct)).OfType<JsonObject>()
            .Select(f => Str(f["path"]))
            .OfType<string>()
            .ToList();

    /// <summary>
    /// Adds a movie by its TMDB id, monitored, and has Radarr look for it. When it has it already,
    /// says so and changes nothing.
    /// </summary>
    public async Task<ArrAdded> AddMovieAsync(int tmdbId, ArrAddOptions options, CancellationToken ct)
    {
        Require(ArrKind.Radarr);
        if (await FindMovieAsync(tmdbId.ToString(CultureInfo.InvariantCulture), null, ct) is { } have)
            return new ArrAdded(have, Already: true);
        var (profile, folder) = await ResolveAsync(options, ct);
        JsonObject? found;
        try
        {
            using var lookup = await SendAsync(HttpMethod.Get, $"/api/v3/movie/lookup/tmdb?tmdbId={tmdbId}", null, ct);
            found = await ReadAsync(lookup, ct) as JsonObject;
        }
        catch (ArrException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError)
        {
            found = null;   // an id TMDB hasn't got: Radarr 7 answers 500
        }
        if (found is null)
            throw new ArrException("Radarr doesn't find this movie.");
        // Only what Radarr needs: the whole lookup sent back is refused by some versions
        var body = new JsonObject
        {
            ["title"] = Str(found["title"]) ?? "",
            ["year"] = Int(found["year"]),
            ["tmdbId"] = tmdbId,
            ["qualityProfileId"] = profile,
            ["rootFolderPath"] = folder,
            ["monitored"] = true,
            ["minimumAvailability"] = "released",
            ["addOptions"] = new JsonObject { ["searchForMovie"] = options.Search },
        };
        using var added = await SendAsync(HttpMethod.Post, "/api/v3/movie", body.ToJsonString(), ct);
        return new ArrAdded(ToTitle(await ReadAsync(added, ct)) ?? new ArrTitle(0, Str(found["title"]) ?? ""), Already: false);
    }

    /// <summary>
    /// Adds a show by its TVDB id (Sonarr knows shows by TVDB's), with the episodes the options
    /// say, and has Sonarr look for them. When it has it already, says so and changes nothing.
    /// </summary>
    public async Task<ArrAdded> AddSeriesAsync(int tvdbId, ArrAddOptions options, CancellationToken ct)
    {
        Require(ArrKind.Sonarr);
        if (await FindSeriesAsync(tvdbId.ToString(CultureInfo.InvariantCulture), null, null, ct) is { } have)
            return new ArrAdded(have, Already: true);
        var (profile, folder) = await ResolveAsync(options, ct);
        var found = (await GetArrayAsync($"/api/v3/series/lookup?term=tvdb%3A{tvdbId}", ct)).OfType<JsonObject>()
                    .FirstOrDefault(s => Int(s["tvdbId"]) == tvdbId)
                    ?? throw new ArrException("Sonarr doesn't find this show.");
        // What Sonarr's own page sends: the lookup, with the choices
        var body = (JsonObject)found.DeepClone();
        body.Remove("id");
        body["qualityProfileId"] = profile;
        body["rootFolderPath"] = folder;
        body["monitored"] = true;
        body["seasonFolder"] = true;
        body["addOptions"] = new JsonObject
        {
            ["monitor"] = options.Monitor,
            ["searchForMissingEpisodes"] = options.Search,
            ["searchForCutoffUnmetEpisodes"] = false,
        };
        if (_version.StartsWith("3.", StringComparison.Ordinal))
        {
            // Sonarr 3 wants a language profile too; 4 has none
            var languages = await GetArrayAsync("/api/v3/languageprofile", ct);
            body["languageProfileId"] = languages.OfType<JsonObject>().Select(l => Int(l["id"])).FirstOrDefault(i => i is not null) ?? 1;
        }
        using var added = await SendAsync(HttpMethod.Post, "/api/v3/series", body.ToJsonString(), ct);
        return new ArrAdded(ToTitle(await ReadAsync(added, ct)) ?? new ArrTitle(0, Str(found["title"]) ?? ""), Already: false);
    }

    /// <summary>The profile and folder of the options, or the first ones when those aren't there any more.</summary>
    private async Task<(int Profile, string Folder)> ResolveAsync(ArrAddOptions options, CancellationToken ct)
    {
        var profiles = await GetProfilesAsync(ct);
        var profile = profiles.FirstOrDefault(p => p.Id == options.ProfileId) ?? profiles.FirstOrDefault()
                      ?? throw new ArrException($"{Kind} has no quality profile.");
        var folders = await GetRootFoldersAsync(ct);
        var folder = folders.FirstOrDefault(f => f == options.RootFolder) ?? folders.FirstOrDefault()
                     ?? throw new ArrException($"{Kind} has no root folder: add one in its Settings → Media Management.");
        return (profile.Id, folder);
    }

    // ---- calendar ----

    /// <summary>
    /// What comes out between these days, only of what is monitored: Sonarr's episodes with the
    /// hour they air, Radarr's movies on the day they come out in cinemas, digitally or on disc.
    /// </summary>
    public async Task<List<ArrRelease>> GetCalendarAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var range = $"start={Uri.EscapeDataString(from.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))}"
                    + $"&end={Uri.EscapeDataString(to.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))}&unmonitored=false";
        var list = new List<ArrRelease>();
        if (Kind == ArrKind.Sonarr)
        {
            foreach (var e in (await GetArrayAsync($"/api/v3/calendar?{range}&includeSeries=true", ct)).OfType<JsonObject>())
            {
                if (Date(e["airDateUtc"]) is not { } when || e["series"] is not JsonObject show)
                    continue;
                var code = Int(e["seasonNumber"]) is { } s && Int(e["episodeNumber"]) is { } n ? $"S{s:00}E{n:00}" : "";
                list.Add(new ArrRelease(when.ToLocalTime(), HasTime: true, ArrKind.Sonarr, Str(show["title"]) ?? "", code, Str(e["title"]),
                                        e["hasFile"]?.GetValueKind() == JsonValueKind.True,
                                        Id(show["tmdbId"]), Str(show["imdbId"]), Id(show["tvdbId"]), Int(show["year"])));
            }
        }
        else
        {
            foreach (var m in (await GetArrayAsync($"/api/v3/calendar?{range}", ct)).OfType<JsonObject>())
                foreach (var (field, what) in new[] { ("inCinemas", "In cinemas"), ("digitalRelease", "Digital release"), ("physicalRelease", "On disc") })
                {
                    // Radarr's dates are days: midnight UTC, which isn't the day before here
                    if (Date(m[field]) is not { } day || day.UtcDateTime.Date < from.Date || day.UtcDateTime.Date > to.Date)
                        continue;
                    var date = DateTime.SpecifyKind(day.UtcDateTime.Date, DateTimeKind.Unspecified);
                    list.Add(new ArrRelease(new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)), HasTime: false,
                                            ArrKind.Radarr, Str(m["title"]) ?? "", what, null,
                                            m["hasFile"]?.GetValueKind() == JsonValueKind.True,
                                            Id(m["tmdbId"]), Str(m["imdbId"]), null, Int(m["year"])));
                }
        }
        return list.OrderBy(r => r.When).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.What, StringComparer.Ordinal).ToList();
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
        return await ReadAsync(response, ct) as JsonArray ?? throw new ArrException($"{Kind} gave an answer Kinora doesn't understand.");
    }

    private async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new ArrException($"{Kind} gave an answer Kinora doesn't understand.", e);
        }
    }

    private static ArrTitle? ToTitle(JsonNode? node) =>
        node is JsonObject o && Int(o["id"]) is { } id ? new ArrTitle(id, Str(o["title"]) ?? "") : null;

    private static DateTimeOffset? Date(JsonNode? node) =>
        Str(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? Id(JsonNode? node) => Int(node) is { } i && i > 0 ? i.ToString(CultureInfo.InvariantCulture) : null;

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
        throw new ArrException(code == 401 ? $"{Kind} refused the key." : $"{Kind} answered {code} to {method} {path.Split('?')[0]}.", null, (HttpStatusCode)code);
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
