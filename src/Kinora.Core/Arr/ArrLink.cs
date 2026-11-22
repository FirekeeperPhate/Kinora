using Kinora.Core.Models;
using Kinora.Core.Settings;

namespace Kinora.Core.Arr;

/// <summary>
/// What Radarr or Sonarr has of a title Kinora is about to delete: <see cref="Title"/> when it
/// has it, <see cref="Error"/> when it couldn't be asked, neither when it hasn't got it.
/// </summary>
public sealed record ArrFound(ArrKind Kind, ArrTitle? Title, string? Error = null)
{
    public bool Has => Title is not null;
}

/// <summary>
/// Radarr and Sonarr as the settings have them, for deleting: a movie or show Kinora deletes goes
/// from them too (its files are already gone: they are told to leave the files alone), an episode
/// stops being monitored. Without them, or without the title, nothing is asked.
/// </summary>
public sealed class ArrLink(AppSettings settings, HttpMessageHandler? handler = null)
{
    public bool HasRadarr => settings.HasRadarr;
    public bool HasSonarr => settings.HasSonarr;

    /// <summary>The import lists are kept from adding it back.</summary>
    public bool Exclude => settings.ArrExclude;

    private ArrClient Radarr() => new(ArrKind.Radarr, settings.RadarrUrl!, settings.RadarrKey, handler);
    private ArrClient Sonarr() => new(ArrKind.Sonarr, settings.SonarrUrl!, settings.SonarrKey, handler);

    /// <summary>Null when there is no Radarr in the settings.</summary>
    public Task<ArrFound?> FindAsync(Movie movie, CancellationToken ct) => !HasRadarr
        ? Task.FromResult<ArrFound?>(null)
        : Ask(ArrKind.Radarr, Radarr, c => c.FindMovieAsync(movie.TmdbId, movie.ImdbId, ct));

    /// <summary>Null when there is no Sonarr in the settings.</summary>
    public Task<ArrFound?> FindAsync(Series series, CancellationToken ct) => !HasSonarr
        ? Task.FromResult<ArrFound?>(null)
        : Ask(ArrKind.Sonarr, Sonarr, c => c.FindSeriesAsync(series.TvdbId, series.ImdbId, series.TmdbId, ct));

    private static async Task<ArrFound?> Ask(ArrKind kind, Func<ArrClient> make, Func<ArrClient, Task<ArrTitle?>> find)
    {
        try
        {
            using var client = make();
            return new ArrFound(kind, await find(client));
        }
        catch (Exception e) when (IsTrouble(e))
        {
            return new ArrFound(kind, null, Why(e));
        }
    }

    /// <summary>Removes what <see cref="FindAsync(Movie, CancellationToken)"/> found. Null when done, else what went wrong.</summary>
    public async Task<string?> RemoveAsync(ArrFound found, CancellationToken ct)
    {
        if (found.Title is not { } title)
            return null;
        try
        {
            using var client = found.Kind == ArrKind.Radarr ? Radarr() : Sonarr();
            await client.RemoveAsync(title, Exclude, ct);
            return null;
        }
        catch (Exception e) when (IsTrouble(e))
        {
            return Why(e);
        }
    }

    /// <summary>
    /// The episode (all of them, for a file with several) stops being monitored in Sonarr. Null
    /// when done or when there is nothing to do; else what went wrong.
    /// </summary>
    public async Task<string?> UnmonitorAsync(ArrFound show, Episode episode, CancellationToken ct)
    {
        if (show.Title is not { } title || episode.Season is not { } season || episode.Number is not { } first)
            return null;
        var last = episode.NumberEnd is { } end && end > first ? end : first;
        try
        {
            using var client = Sonarr();
            return await client.UnmonitorEpisodesAsync(title.Id, season, Enumerable.Range(first, last - first + 1).ToList(), ct) > 0
                ? null
                : $"Sonarr has no {episode.Code} in “{title.Title}”";
        }
        catch (Exception e) when (IsTrouble(e))
        {
            return Why(e);
        }
    }

    // ---- adding ----

    /// <summary>Adds the movie with this TMDB id to Radarr, which looks for it.</summary>
    public async Task<ArrAdded> AddMovieAsync(string tmdbId, CancellationToken ct)
    {
        if (!int.TryParse(tmdbId, out var id) || id <= 0)
            throw new ArrException("This movie has no TMDB id.");
        try
        {
            using var client = Radarr();
            return await client.AddMovieAsync(id, new ArrAddOptions(settings.RadarrProfileId, settings.RadarrRootFolder), ct);
        }
        catch (Exception e) when (IsTrouble(e) && e is not ArrException)
        {
            throw new ArrException(Why(e), e);
        }
    }

    /// <summary>Adds the show with this TVDB id to Sonarr, which looks for its episodes.</summary>
    public async Task<ArrAdded> AddSeriesAsync(string tvdbId, CancellationToken ct)
    {
        if (!int.TryParse(tvdbId, out var id) || id <= 0)
            throw new ArrException("TVDB doesn't know this show, and Sonarr finds shows by TVDB.");
        try
        {
            using var client = Sonarr();
            return await client.AddSeriesAsync(id, new ArrAddOptions(settings.SonarrProfileId, settings.SonarrRootFolder, settings.SonarrMonitor), ct);
        }
        catch (Exception e) when (IsTrouble(e) && e is not ArrException)
        {
            throw new ArrException(Why(e), e);
        }
    }

    /// <summary>Its quality profiles and root folders, for the settings.</summary>
    public static async Task<(List<ArrChoice> Profiles, List<string> Folders)> GetChoicesAsync(ArrClient client, CancellationToken ct) =>
        (await client.GetProfilesAsync(ct), await client.GetRootFoldersAsync(ct));

    // ---- calendar ----

    /// <summary>
    /// What Sonarr and Radarr have coming out from today for these many days, together; and what
    /// went wrong asking either (the other's are there all the same).
    /// </summary>
    public async Task<(List<ArrRelease> Releases, List<string> Problems)> CalendarAsync(DateTime today, int days, CancellationToken ct)
    {
        var from = today.Date;
        var to = from.AddDays(days).AddTicks(-1);
        var asks = new List<(ArrKind Kind, Func<ArrClient> Make)>();
        if (HasSonarr)
            asks.Add((ArrKind.Sonarr, Sonarr));
        if (HasRadarr)
            asks.Add((ArrKind.Radarr, Radarr));
        var results = await Task.WhenAll(asks.Select(async a =>
        {
            try
            {
                using var client = a.Make();
                return (List: await client.GetCalendarAsync(from, to, ct), Problem: (string?)null);
            }
            catch (Exception e) when (IsTrouble(e))
            {
                return (List: new List<ArrRelease>(), Problem: $"{a.Kind}: {Why(e)}");
            }
        }));
        var releases = results.SelectMany(r => r.List)
                              .OrderBy(r => r.When.Date).ThenBy(r => r.HasTime ? 0 : 1).ThenBy(r => r.When)
                              .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.What, StringComparer.Ordinal)
                              .ToList();
        return (releases, results.Select(r => r.Problem).OfType<string>().ToList());
    }

    private static bool IsTrouble(Exception e) =>
        e is ArrException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException;

    private static string Why(Exception e) => e switch
    {
        ArrException => e.Message,
        TaskCanceledException => "no answer in time",
        _ => e.Message,
    };
}
