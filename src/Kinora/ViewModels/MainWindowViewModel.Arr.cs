using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Arr;
using Kinora.Core.Online;

namespace Kinora.ViewModels;

/// <summary>Radarr and Sonarr beyond deleting: adding what isn't in the library, and what comes out next.</summary>
public partial class MainWindowViewModel
{
    /// <summary>Radarr or Sonarr is in the settings: the sidebar has "Coming up".</summary>
    public bool HasArr => _s.Arr.HasRadarr || _s.Arr.HasSonarr;

    /// <summary>
    /// A poster of a title TMDB has and the library hasn't: it opens TMDB's page, and with
    /// Radarr (a movie) or Sonarr (a show) in the settings its + button adds it there.
    /// </summary>
    public ShelfItem OnlineItem(TmdbSimilar title, bool series, string? note = null)
    {
        var url = $"https://www.themoviedb.org/{(series ? "tv" : "movie")}/{Uri.EscapeDataString(title.Id)}";
        var can = series ? _s.Arr.HasSonarr : _s.Arr.HasRadarr;
        return new ShelfItem(title, () => Core.Platform.Launcher.OpenUrl(url), note,
                             can ? (series ? "Sonarr" : "Radarr") : null,
                             can ? () => AddToArrAsync(title, series) : null);
    }

    /// <summary>After asking: the note for the poster ("Added to Radarr"), or null when the answer was no.</summary>
    private async Task<string?> AddToArrAsync(TmdbSimilar title, bool series)
    {
        var app = series ? "Sonarr" : "Radarr";
        var name = title.Year is { } y ? $"“{title.Title}” ({y})" : $"“{title.Title}”";
        var episodes = _s.Settings.SonarrMonitor switch
        {
            "firstSeason" => "the first season",
            "future" => "the episodes that come out from now on",
            _ => "all the episodes",
        };
        var message = series
            ? $"Sonarr adds {name} and looks for {episodes}, with the quality profile and the folder chosen in Settings."
            : $"Radarr adds {name} and looks for it, with the quality profile and the folder chosen in Settings.";
        if (Dialogs is null || !await Dialogs.ConfirmAsync($"Add to {app}", message, "Add"))
            return null;
        try
        {
            ArrAdded added;
            if (series)
            {
                // Sonarr knows shows by TVDB's id: TMDB tells it
                using var tmdb = _s.CreateTmdbClient() ?? throw new ArrException("Add a TMDB API key in Settings first.");
                var show = await tmdb.GetSeriesAsync(title.Id, CancellationToken.None);
                added = await _s.Arr.AddSeriesAsync(show?.TvdbId ?? "", CancellationToken.None);
            }
            else
                added = await _s.Arr.AddMovieAsync(title.Id, CancellationToken.None);
            return added.Already ? $"Already in {app}" : $"Added to {app}";
        }
        catch (Exception e) when (e is ArrException or HttpRequestException or TmdbException or TaskCanceledException)
        {
            return $"{app}: {e.Message}";
        }
    }

    // ---- coming up ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibrary))]
    public partial CalendarViewModel? Calendar { get; private set; }

    [RelayCommand]
    private void OpenCalendar()
    {
        if (!HasArr)
            return;
        Remember(new CalendarPage());
        var old = Calendar;
        Calendar = new CalendarViewModel(this, _s);
        old?.Dispose();
        CloseDetail();
        ClosePerson();
    }

    private void CloseCalendar()
    {
        var old = Calendar;
        Calendar = null;
        old?.Dispose();
    }

    /// <summary>A title of the calendar, when the library has it: by its ids.</summary>
    public CardViewModel? CardFor(ArrRelease release)
    {
        if (release.Kind == ArrKind.Radarr)
            return (release.TmdbId is { } t ? MovieByTmdbId(t) : null)
                   ?? (release.ImdbId is { } i ? _movies.FirstOrDefault(c => string.Equals(c.Movie.ImdbId, i, StringComparison.OrdinalIgnoreCase)) : null);
        return _shows.FirstOrDefault(c => release.TvdbId is { } tv && c.Series.TvdbId == tv)
               ?? (release.ImdbId is { } im ? _shows.FirstOrDefault(c => string.Equals(c.Series.ImdbId, im, StringComparison.OrdinalIgnoreCase)) : null)
               ?? (release.TmdbId is { } tm ? ShowByTmdbId(tm) : null);
    }
}
