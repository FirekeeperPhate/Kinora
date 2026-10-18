using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kinora.Core.Subtitles;

public sealed class OpenSubtitlesException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed record SubtitleResult(
    string Id,
    string FileId,
    string FileName,
    string Language,
    string? Release,
    int Downloads,
    bool HashMatch,
    bool HearingImpaired,
    bool MachineTranslated,
    bool Forced,
    double? Fps,
    string? Uploaded);

public sealed record SubtitleAccount(int RemainingDownloads, int AllowedDownloads, bool Vip);

public sealed record DownloadedSubtitle(string FileName, byte[] Content, int Remaining);

/// <summary>What to look for: by file hash first, then by ids, then by title.</summary>
public sealed record SubtitleQuery(
    string? MovieHash = null,
    string? ImdbId = null,
    string? TmdbId = null,
    string? ParentImdbId = null,
    string? ParentTmdbId = null,
    int? Season = null,
    int? Episode = null,
    string? Title = null,
    int? Year = null);

/// <summary>
/// The OpenSubtitles REST API (api.opensubtitles.com). Searching needs only the application's
/// key; downloading needs a user login, and each download counts against the account's daily
/// quota, which the answers report.
/// </summary>
public sealed class OpenSubtitlesClient : IDisposable
{
    public const string UserAgent = "Kinora v0.1";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private string? _token;
    private Uri _base = new("https://api.opensubtitles.com/api/v1/");

    public OpenSubtitlesClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Dispose() => _http.Dispose();

    public bool IsLoggedIn => _token is not null;

    public async Task<SubtitleAccount> LoginAsync(string username, string password, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "login");
        request.Content = Json(new { username, password });
        using var doc = await SendAsync(request, ct);
        var root = doc.RootElement;
        _token = Str(root, "token");
        // VIP accounts are sent to another host, which must be used from then on
        if (Str(root, "base_url") is { Length: > 0 } host)
            _base = new Uri($"https://{host.Replace("https://", "").TrimEnd('/')}/api/v1/");
        var user = root.TryGetProperty("user", out var u) ? u : default;
        return new SubtitleAccount(
            Int(user, "remaining_downloads") ?? Int(user, "allowed_downloads") ?? 0,
            Int(user, "allowed_downloads") ?? 0,
            user.ValueKind == JsonValueKind.Object && user.TryGetProperty("vip", out var vip) && vip.ValueKind == JsonValueKind.True);
    }

    public async Task<SubtitleAccount?> GetAccountAsync(CancellationToken ct)
    {
        if (_token is null)
            return null;
        using var doc = await SendAsync(Request(HttpMethod.Get, "infos/user"), ct);
        var data = doc.RootElement.GetProperty("data");
        return new SubtitleAccount(Int(data, "remaining_downloads") ?? 0, Int(data, "allowed_downloads") ?? 0,
                                   data.TryGetProperty("vip", out var vip) && vip.ValueKind == JsonValueKind.True);
    }

    public async Task<List<SubtitleResult>> SearchAsync(SubtitleQuery q, IReadOnlyList<string> languages, CancellationToken ct)
    {
        // The API wants the parameters sorted and lowercase, or it answers with a redirect
        var args = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (q.MovieHash is not null) args["moviehash"] = q.MovieHash;
        if (Imdb(q.ImdbId) is { } imdb) args["imdb_id"] = imdb;
        if (q.TmdbId is not null) args["tmdb_id"] = q.TmdbId;
        if (Imdb(q.ParentImdbId) is { } parentImdb) args["parent_imdb_id"] = parentImdb;
        if (q.ParentTmdbId is not null) args["parent_tmdb_id"] = q.ParentTmdbId;
        if (q.Season is { } s) args["season_number"] = s.ToString(CultureInfo.InvariantCulture);
        if (q.Episode is { } e) args["episode_number"] = e.ToString(CultureInfo.InvariantCulture);
        if (q.Title is not null && q.ImdbId is null && q.TmdbId is null && q.ParentImdbId is null && q.ParentTmdbId is null)
        {
            args["query"] = q.Title.ToLowerInvariant();
            if (q.Year is { } y) args["year"] = y.ToString(CultureInfo.InvariantCulture);
        }
        if (languages.Count > 0)
            args["languages"] = string.Join(',', languages.Select(l => l.ToLowerInvariant()).OrderBy(l => l, StringComparer.Ordinal));

        var url = "subtitles?" + string.Join('&', args.Select(a => a.Key + "=" + Uri.EscapeDataString(a.Value)));
        using var doc = await SendAsync(Request(HttpMethod.Get, url), ct);
        var results = new List<SubtitleResult>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var a = item.GetProperty("attributes");
            if (!a.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
                continue;
            var file = files[0];
            results.Add(new SubtitleResult(
                Id: item.TryGetProperty("id", out var id) ? id.ToString() : "",
                FileId: file.GetProperty("file_id").ToString(),
                FileName: Str(file, "file_name") ?? "",
                Language: Str(a, "language") ?? "und",
                Release: Str(a, "release"),
                Downloads: Int(a, "download_count") ?? 0,
                HashMatch: Bool(a, "moviehash_match"),
                HearingImpaired: Bool(a, "hearing_impaired"),
                MachineTranslated: Bool(a, "machine_translated") || Bool(a, "ai_translated"),
                Forced: Bool(a, "foreign_parts_only"),
                Fps: a.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number && fps.GetDouble() > 0 ? fps.GetDouble() : null,
                Uploaded: Str(a, "upload_date")));
        }
        return results;
    }

    /// <summary>Asks for a download link (this is what counts against the quota), then fetches the file.</summary>
    public async Task<DownloadedSubtitle> DownloadAsync(string fileId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "download");
        request.Content = Json(new { file_id = long.Parse(fileId, CultureInfo.InvariantCulture) });
        using var doc = await SendAsync(request, ct);
        var root = doc.RootElement;
        var link = Str(root, "link") ?? throw new OpenSubtitlesException(Str(root, "message") ?? "OpenSubtitles gave no download link.");
        var content = await _http.GetByteArrayAsync(link, ct);
        return new DownloadedSubtitle(Str(root, "file_name") ?? "subtitle.srt", content, Int(root, "remaining") ?? -1);
    }

    /// <summary>JSON with a plain "application/json" type: no charset, which some servers refuse.</summary>
    private static ByteArrayContent Json(object body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.Add("Api-Key", _apiKey);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var clone = attempt == 0 ? null : await CloneAsync(request);
                using var response = await _http.SendAsync(clone ?? request, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 + attempt), ct);
                    continue;
                }
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new OpenSubtitlesException(ErrorMessage(response.StatusCode, body), response.StatusCode);
                return JsonDocument.Parse(body);
            }
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var h in original.Headers)
            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync();
            copy.Content = new ByteArrayContent(bytes);
            foreach (var h in original.Content.Headers)
                copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return copy;
    }

    private static string ErrorMessage(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = Str(doc.RootElement, "message") ?? Str(doc.RootElement, "error")
                      ?? (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                          ? errors[0].ToString() : null);
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => "OpenSubtitles refused the login" + (message is null ? "" : $" (“{message}”)")
                                           + ": check the user name (not the e-mail) and the password.",
            HttpStatusCode.Forbidden => "OpenSubtitles refused the API key.",
            HttpStatusCode.NotAcceptable => message ?? "Today's download quota is used up.",
            _ => message is null ? $"OpenSubtitles answered {(int)status}." : $"OpenSubtitles: {message}",
        };
    }

    /// <summary>"tt0113277" → "113277": the API takes the number without prefix or leading zeros.</summary>
    internal static string? Imdb(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        var digits = id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id[2..] : id;
        digits = digits.TrimStart('0');
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
