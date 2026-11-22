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

    private static bool IsTrouble(Exception e) =>
        e is ArrException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException;

    private static string Why(Exception e) => e switch
    {
        ArrException => e.Message,
        TaskCanceledException => "no answer in time",
        _ => e.Message,
    };
}
