using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kinora.Core.Online;

public sealed class TmdbException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>
/// The few TMDB v3 calls Kinora needs. Takes either a v3 API key (32 characters, sent as
/// api_key) or a read access token (the long one, sent as Bearer). Waits and retries when TMDB
/// says "too many requests".
/// </summary>
public sealed class TmdbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly bool _ownsClient;

    public string Language { get; }

    public TmdbClient(string key, string language, HttpMessageHandler? handler = null)
    {
        Language = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _http.Timeout = TimeSpan.FromSeconds(25);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kinora/0.1");
        key = key.Trim();
        if (key.Length > 40)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else
            _apiKey = key;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    /// <summary>True when the key works.</summary>
    public async Task<bool> TestAsync(CancellationToken ct)
    {
        try
        {
            using var _ = await GetJsonAsync("configuration", [], ct);
            return true;
        }
        catch (TmdbException e) when (e.IsUnauthorized)
        {
            return false;
        }
    }

    public async Task<List<TmdbSearchResult>> SearchMovieAsync(string query, int? year, CancellationToken ct)
    {
        var args = new List<(string, string)> { ("query", query), ("include_adult", "false"), ("language", Language) };
        if (year is { } y)
            args.Add(("year", y.ToString()));
        using var doc = await GetJsonAsync("search/movie", args, ct);
        var results = new List<TmdbSearchResult>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            results.Add(new TmdbSearchResult(
                Id: r.GetProperty("id").GetRawText(),
                Title: Str(r, "title") ?? "",
                OriginalTitle: Str(r, "original_title"),
                Year: Str(r, "release_date") is { Length: >= 4 } d && int.TryParse(d[..4], out var ry) ? ry : null,
                Overview: Str(r, "overview"),
                PosterPath: Str(r, "poster_path"),
                Popularity: r.TryGetProperty("popularity", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0));
        }
        return results;
    }

    public async Task<string?> FindByImdbAsync(string imdbId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"find/{Uri.EscapeDataString(imdbId)}", [("external_source", "imdb_id")], ct);
        var movies = doc.RootElement.GetProperty("movie_results");
        return movies.GetArrayLength() > 0 ? movies[0].GetProperty("id").GetRawText() : null;
    }

    public async Task<TmdbMovie?> GetMovieAsync(string tmdbId, CancellationToken ct)
    {
        var lang2 = Language.Split('-')[0];
        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync($"movie/{Uri.EscapeDataString(tmdbId)}",
                [("language", Language), ("append_to_response", "credits,release_dates,images"),
                 ("include_image_language", $"{lang2},en,null")], ct);
        }
        catch (TmdbException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        using (doc)
        {
            var r = doc.RootElement;
            var movie = new TmdbMovie
            {
                Id = r.GetProperty("id").GetRawText(),
                ImdbId = Str(r, "imdb_id"),
                Language = Language,
                Title = Str(r, "title") ?? "",
                OriginalTitle = Str(r, "original_title"),
                Overview = Str(r, "overview"),
                Tagline = Str(r, "tagline"),
                ReleaseDate = Str(r, "release_date"),
                Runtime = r.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number && rt.GetInt32() > 0 ? rt.GetInt32() : null,
                VoteAverage = r.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number && va.GetDouble() > 0 ? Math.Round(va.GetDouble(), 1) : null,
                Collection = r.TryGetProperty("belongs_to_collection", out var col) && col.ValueKind == JsonValueKind.Object ? Str(col, "name") : null,
                Genres = Names(r, "genres"),
                Studios = Names(r, "production_companies"),
                Countries = Names(r, "production_countries"),
                PosterPath = Str(r, "poster_path"),
                BackdropPath = Str(r, "backdrop_path"),
            };

            if (r.TryGetProperty("credits", out var credits))
            {
                foreach (var c in credits.GetProperty("cast").EnumerateArray().Take(30))
                    if (Str(c, "name") is { } name)
                        movie.Cast.Add(new TmdbCast(name, Str(c, "character")));
                foreach (var c in credits.GetProperty("crew").EnumerateArray())
                    if (Str(c, "job") == "Director" && Str(c, "name") is { } name && !movie.Directors.Contains(name))
                        movie.Directors.Add(name);
            }

            movie.Certification = Certification(r, Language);
            movie.LogoPath = Logo(r, lang2);
            return movie;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, List<(string Key, string Value)> args, CancellationToken ct)
    {
        if (_apiKey is not null)
            args.Add(("api_key", _apiKey));
        var url = args.Count == 0 ? path
            : path + "?" + string.Join("&", args.Select(a => a.Key + "=" + Uri.EscapeDataString(a.Value)));

        for (var attempt = 0; ; attempt++)
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 + attempt * 2);
                await Task.Delay(wait, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "TMDB refused the API key."
                    : $"TMDB answered {(int)response.StatusCode} {response.ReasonPhrase}.";
                throw new TmdbException(message, response.StatusCode);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static List<string> Names(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => Str(x, "name")).OfType<string>().ToList()
            : [];

    /// <summary>The rating of the metadata language's country, or the US one.</summary>
    private static string? Certification(JsonElement r, string language)
    {
        if (!r.TryGetProperty("release_dates", out var rd))
            return null;
        var country = language.Contains('-') ? language.Split('-')[1].ToUpperInvariant() : "US";
        string? Find(string iso)
        {
            foreach (var entry in rd.GetProperty("results").EnumerateArray())
            {
                if (Str(entry, "iso_3166_1") != iso)
                    continue;
                foreach (var date in entry.GetProperty("release_dates").EnumerateArray())
                    if (Str(date, "certification") is { } cert)
                        return cert;
            }
            return null;
        }
        return Find(country) ?? (country == "US" ? null : Find("US"));
    }

    /// <summary>A logo in the metadata language, else English, else one without text language.</summary>
    private static string? Logo(JsonElement r, string lang2)
    {
        if (!r.TryGetProperty("images", out var images) || !images.TryGetProperty("logos", out var logos))
            return null;
        var all = logos.EnumerateArray().ToList();
        foreach (var wanted in new[] { lang2, "en", null })
        {
            var match = all.Where(l => Str(l, "iso_639_1") == wanted)
                           .OrderByDescending(l => l.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0)
                           .Select(l => Str(l, "file_path"))
                           .FirstOrDefault(p => p is not null && !p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return null;
    }
}
