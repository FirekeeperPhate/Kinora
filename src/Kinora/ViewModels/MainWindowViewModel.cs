using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.MediaServers;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Playback;
using Kinora.Core.Platform;
using Kinora.Core.Scanning;
using Kinora.Core.Settings;
using Kinora.Core.Subtitles;
using Kinora.Services;

namespace Kinora.ViewModels;

public enum LibraryMode
{
    Movies,
    Shows,
}

public partial class MainWindowViewModel : ViewModelBase
{
    public const double Gap = 22;
    private const int RecentCount = 100;

    private readonly AppServices _s;
    private readonly List<MovieCardViewModel> _movies = [];
    private readonly List<SeriesCardViewModel> _shows = [];
    private readonly HashSet<long> _unavailable = [];
    private Dictionary<long, LibraryRoot> _roots = [];
    private List<CardViewModel> _filtered = [];
    private double _viewportWidth;
    private double _scaling = 1;
    private CancellationTokenSource? _scanCts;
    private bool _suppressFilter;
    private int _statusVersion;

    public MainWindowViewModel(AppServices services)
    {
        _s = services;
        SortOptions =
        [
            new SortOption("Title", SortOrder.Title),
            new SortOption("Year", SortOrder.Year),
            new SortOption("Date added", SortOrder.DateAdded),
            new SortOption("Rating", SortOrder.Rating),
        ];
        _suppressFilter = true;
        SelectedSort = SortOptions.First(o => o.Value == _s.Settings.Sort);
        SortDescending = _s.Settings.SortDescending;
        Mode = _s.Settings.ShowTvShows ? LibraryMode.Shows : LibraryMode.Movies;
        CardViewModel.MoveTargetsProvider = MoveTargetsFor;
        CardViewModel.ToggleSelectHandler = ToggleSelection;
        _suppressFilter = false;
    }

    public AppServices Services => _s;
    public IDialogs? Dialogs { get; set; }
    public ObservableCollection<NavItem> NavItems { get; } = [];
    public IReadOnlyList<SortOption> SortOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoviesMode), nameof(IsShowsMode), nameof(EmptyLibraryTitle), nameof(EmptyLibraryText), nameof(ShowEmptyLibrary))]
    public partial LibraryMode Mode { get; set; }

    public bool IsMoviesMode
    {
        get => Mode == LibraryMode.Movies;
        set { if (value) Mode = LibraryMode.Movies; }
    }

    public bool IsShowsMode
    {
        get => Mode == LibraryMode.Shows;
        set { if (value) Mode = LibraryMode.Shows; }
    }

    [ObservableProperty]
    public partial IReadOnlyList<MovieRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial NavItem? SelectedNav { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    [ObservableProperty]
    public partial string ViewTitle { get; private set; } = "All movies";

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus), nameof(ShowEmptyLibrary))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    public partial string ScanStatus { get; private set; } = "";

    [ObservableProperty]
    public partial double ScanProgress { get; private set; }

    [ObservableProperty]
    public partial bool ScanIndeterminate { get; private set; } = true;

    public bool ShowStatus => IsScanning || ScanStatus.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibrary))]
    public partial MovieDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibrary))]
    public partial SeriesDetailViewModel? SeriesDetail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibrary))]
    public partial PersonViewModel? Person { get; private set; }

    public bool ShowLibrary => Detail is null && SeriesDetail is null && Person is null;

    /// <summary>The built-in player, over everything else while a video plays.</summary>
    [ObservableProperty]
    public partial PlayerViewModel? Player { get; private set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; private set; }

    private LibraryKind CurrentKind => Mode == LibraryMode.Movies ? LibraryKind.Movies : LibraryKind.Series;
    private bool HasRootsOfMode => _roots.Values.Any(r => r.Kind == CurrentKind);
    private IReadOnlyList<CardViewModel> CurrentCards => Mode == LibraryMode.Movies ? _movies : _shows;

    public bool ShowEmptyLibrary => !HasRootsOfMode || (CurrentCards.Count == 0 && !IsScanning);

    public string EmptyLibraryTitle => Mode == LibraryMode.Movies ? "No movies yet" : "No TV shows yet";

    public string EmptyLibraryText => (Mode, HasRootsOfMode) switch
    {
        (LibraryMode.Movies, false) => "Add the folders where your movies are, and Kinora builds the library from them.",
        (LibraryMode.Shows, false) => "Add the folders where your TV shows are: one folder per show, with season folders inside.",
        _ => "Nothing found yet. Run a scan, or check the folders in Settings.",
    };

    partial void OnModeChanged(LibraryMode value)
    {
        ClearSelection();
        _s.Settings.ShowTvShows = value == LibraryMode.Shows;
        SearchText = "";
        ClearFilters();
        SelectedNav = null;
        RebuildNav();
        ScrollToTopRequested?.Invoke();
    }

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (newValue is { IsHeader: true })
        {
            SelectedNav = oldValue;
            return;
        }
        ApplyFilter(toTop: true);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter(toTop: true);

    partial void OnSelectedSortChanged(SortOption value)
    {
        _s.Settings.Sort = value.Value;
        ApplyFilter(toTop: true);
    }

    partial void OnSortDescendingChanged(bool value)
    {
        _s.Settings.SortDescending = value;
        ApplyFilter(toTop: true);
    }

    // ---- loading ----

    public void Initialize()
    {
        Reload();
        _ = ScanAndSyncOnStartupAsync();
        _ = CheckForUpdatesOnStartupAsync();
        _ = WarmUpPlayerAsync();
    }

    private async Task ScanAndSyncOnStartupAsync()
    {
        if (_s.Settings.ScanOnStartup && _roots.Count > 0)
            await RunScanAsync(full: false);
        if (_s.Settings is { SyncOnStartup: true, HasMediaServer: true })
            await SyncMediaServerAsync(asked: false);
        if (_s.Settings.HasTmdbKey && _shows.Count > 0
            && (_s.Settings.LastEpisodeCheck is not { } last || DateTime.UtcNow - last > TimeSpan.FromDays(7)))
            await CheckEpisodesAsync(asked: false);
    }

    /// <summary>
    /// VLC's engine is started in the background soon after Kinora opens, so the first Play
    /// doesn't wait for it (the first start after a reboot or an update can take seconds).
    /// </summary>
    private async Task WarmUpPlayerAsync()
    {
        if (!_s.Settings.UseBuiltInPlayer)
            return;
        await Task.Delay(TimeSpan.FromSeconds(2));
        await _s.Video.GetAsync(_s.Settings.SubtitleScale);
    }

    private void Reload()
    {
        _roots = _s.Database.GetRoots().ToDictionary(r => r.Id);

        var movies = _s.Database.GetMovies().Where(m => _roots.ContainsKey(m.RootId)).ToList();
        var cast = _s.Database.GetCastNames();
        var started = _s.Database.GetPlaybackPositions(PlaybackKind.Movie);
        var oldMovies = _movies.ToDictionary(c => c.Movie.Id);
        _movies.Clear();
        foreach (var movie in movies)
        {
            var names = cast.GetValueOrDefault(movie.Id, "");
            if (oldMovies.TryGetValue(movie.Id, out var card))
                card.Update(movie, names);
            else
                card = new MovieCardViewModel(this, _s.PosterLoader, movie, names);
            card.IsUnavailable = _unavailable.Contains(movie.RootId);
            card.Started = PlaybackRules.SeenFraction(started.GetValueOrDefault(movie.Id));
            card.LastPlayed = started.GetValueOrDefault(movie.Id)?.Updated ?? 0;
            _movies.Add(card);
        }

        _attention.Clear();
        foreach (var card in _movies)
            if (LibraryHealth.Problems(card.Movie) is { Count: > 0 } problems)
                _attention[card] = LibraryHealth.Describe(problems);

        var shows = _s.Database.GetSeries().Where(s => _roots.ContainsKey(s.RootId)).ToList();
        var showCast = _s.Database.GetSeriesCastNames();
        var startedEpisodes = _s.Database.GetStartedEpisodes();
        var played = _s.Database.GetSeriesPlayed();
        var oldShows = _shows.ToDictionary(c => c.Series.Id);
        _shows.Clear();
        foreach (var show in shows)
        {
            var names = showCast.GetValueOrDefault(show.Id, "");
            if (oldShows.TryGetValue(show.Id, out var card))
                card.Update(show, names);
            else
                card = new SeriesCardViewModel(this, _s.PosterLoader, show, names);
            card.IsUnavailable = _unavailable.Contains(show.RootId);
            card.SetProgress(startedEpisodes.GetValueOrDefault(show.Id), played.GetValueOrDefault(show.Id));
            _shows.Add(card);
        }
        var numbers = _s.Database.GetEpisodeNumbers();
        foreach (var card in _shows)
            if (LibraryHealth.Problems(card.Series, numbers.GetValueOrDefault(card.Series.Id) ?? []) is { Count: > 0 } problems)
                _attention[card] = LibraryHealth.Describe(problems);
        _ = RefreshGapsAsync();

        OnPropertyChanged(nameof(ShowEmptyLibrary));
        OnPropertyChanged(nameof(EmptyLibraryText));
        RebuildNav();
    }

    private void RebuildNav()
    {
        var previous = SelectedNav;
        _suppressFilter = true;
        NavItems.Clear();
        var cards = CurrentCards;
        var movies = Mode == LibraryMode.Movies;

        NavItems.Add(new NavItem(NavKind.AllMovies, movies ? "All movies" : "All shows", movies ? Icons.Film : Icons.Tv) { Count = cards.Count });
        var going = cards.Count(c => c.InProgress);
        if (going > 0)
            NavItems.Add(new NavItem(NavKind.Continue, "Continue watching", Icons.Play) { Count = going });
        NavItems.Add(new NavItem(NavKind.Unwatched, "Unwatched", Icons.Eye) { Count = cards.Count(c => !c.Watched) });
        NavItems.Add(new NavItem(NavKind.RecentlyAdded, "Recently added", Icons.Clock) { Count = Math.Min(RecentCount, cards.Count) });
        var missing = cards.Count(c => c.NeedsMatch);
        if (missing > 0)
            NavItems.Add(new NavItem(NavKind.NeedsMetadata, "Not identified", Icons.Question) { Count = missing });
        FindDuplicates();
        if (_duplicates.Count > 0)
            NavItems.Add(new NavItem(NavKind.Duplicates, "Duplicates", Icons.Copies) { Count = _duplicates.Count });
        var lacking = movies ? 0 : _shows.Count(c => _gaps.ContainsKey(c.Series.Id));
        if (lacking > 0)
            NavItems.Add(new NavItem(NavKind.MissingEpisodes, "Missing episodes", Icons.Missing) { Count = lacking });
        var attention = cards.Count(_attention.ContainsKey) + EmptyFolders.Count;
        if (attention > 0)
            NavItems.Add(new NavItem(NavKind.NeedsAttention, "Needs attention", Icons.Wrench) { Count = attention });
        RefreshFilterChoices();

        var roots = _roots.Values.Where(r => r.Kind == CurrentKind).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (roots.Count > 0)
        {
            NavItems.Add(new NavItem(NavKind.Header, "FOLDERS"));
            foreach (var root in roots)
                NavItems.Add(new NavItem(NavKind.Root, root.Name, Icons.Folder)
                {
                    RootId = root.Id,
                    MarkWatchedCommand = new RelayCommand(() => MarkFolderWatched(root.Id, true)),
                    MarkUnwatchedCommand = new RelayCommand(() => MarkFolderWatched(root.Id, false)),
                    Count = cards.Count(c => c.RootId == root.Id),
                    IsUnavailable = _unavailable.Contains(root.Id),
                });
        }

        var genres = cards.SelectMany(c => c.Genres)
                          .GroupBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                          .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                          .ToList();
        if (genres.Count > 0)
        {
            NavItems.Add(new NavItem(NavKind.Header, "GENRES"));
            foreach (var g in genres)
                NavItems.Add(new NavItem(NavKind.Genre, g.Key, Icons.Tag) { Genre = g.Key, Count = g.Count() });
        }

        SelectedNav = NavItems.FirstOrDefault(n => n.SameAs(previous)) ?? NavItems[0];
        _suppressFilter = false;
        ApplyFilter();
    }

    // ---- filtering and layout ----

    private IEnumerable<CardViewModel> InView(NavItem nav)
    {
        var cards = CurrentCards;
        return nav.Kind switch
        {
            NavKind.Continue => cards.Where(c => c.InProgress),
            NavKind.Unwatched => cards.Where(c => !c.Watched),
            NavKind.RecentlyAdded => cards.OrderByDescending(c => c.DateAdded).Take(RecentCount),
            NavKind.NeedsMetadata => cards.Where(c => c.NeedsMatch),
            NavKind.Duplicates => cards.Where(_duplicates.ContainsKey),
            NavKind.MissingEpisodes => cards.Where(c => c is SeriesCardViewModel s && _gaps.ContainsKey(s.Series.Id)),
            NavKind.NeedsAttention => cards.Where(_attention.ContainsKey),
            NavKind.Root => cards.Where(c => c.RootId == nav.RootId),
            NavKind.Genre => cards.Where(c => c.Genres.Contains(nav.Genre!, StringComparer.CurrentCultureIgnoreCase)),
            _ => cards,
        };
    }

    private void ApplyFilter(bool toTop = false)
    {
        if (_suppressFilter)
            return;
        var nav = SelectedNav ?? NavItems.FirstOrDefault();
        if (nav is null)
            return;

        var query = Core.SearchText.Normalize(SearchText);
        var filter = CurrentFilter;
        var items = InView(nav).Where(c => Core.SearchText.Matches(c.SearchKey, query))
                               .Where(c => c is MovieCardViewModel m ? filter.Allows(m.Movie) : filter.Allows(c.Year, c.Rating));

        IEnumerable<CardViewModel> sorted;
        if (nav.Kind == NavKind.RecentlyAdded)
        {
            sorted = items.OrderByDescending(c => c.DateAdded);
        }
        else if (nav.Kind == NavKind.Continue)
        {
            sorted = items.OrderByDescending(c => c.LastPlayed);
        }
        else if (nav.Kind == NavKind.Duplicates)
        {
            sorted = items.OrderBy(c => _duplicates[c]);   // each title next to its copies
        }
        else
        {
            // Each order has its natural direction (titles A→Z, the rest newest or best first);
            // the toggle reverses it.
            sorted = SelectedSort.Value switch
            {
                SortOrder.Year => SortDescending
                    ? items.OrderBy(c => c.Year ?? int.MaxValue).ThenBy(c => c.SortKey, StringComparer.Ordinal)
                    : items.OrderByDescending(c => c.Year ?? 0).ThenBy(c => c.SortKey, StringComparer.Ordinal),
                SortOrder.DateAdded => SortDescending
                    ? items.OrderBy(c => c.DateAdded)
                    : items.OrderByDescending(c => c.DateAdded),
                SortOrder.Rating => SortDescending
                    ? items.OrderBy(c => c.Rating ?? double.MaxValue).ThenBy(c => c.SortKey, StringComparer.Ordinal)
                    : items.OrderByDescending(c => c.Rating ?? 0).ThenBy(c => c.SortKey, StringComparer.Ordinal),
                _ => SortDescending
                    ? items.OrderByDescending(c => c.SortKey, StringComparer.Ordinal)
                    : items.OrderBy(c => c.SortKey, StringComparer.Ordinal),
            };
        }

        _filtered = sorted.ToList();
        // What the view is about, under each poster
        foreach (var card in CurrentCards)
            card.Note = nav.Kind switch
            {
                NavKind.Duplicates when _duplicates.ContainsKey(card) =>
                    _roots.TryGetValue(card.RootId, out var root) ? $"{card.CopyText}  ·  {root.Name}" : card.CopyText,
                NavKind.MissingEpisodes when card is SeriesCardViewModel s && _gaps.TryGetValue(s.Series.Id, out var gaps) => gaps.Note,
                NavKind.NeedsAttention => _attention.GetValueOrDefault(card),
                _ => null,
            };
        OnPropertyChanged(nameof(EmptyFolders));
        OnPropertyChanged(nameof(EmptyFoldersTitle));
        OnPropertyChanged(nameof(ShowEmptyFolders));
        ViewTitle = nav.Title;
        var (one, many) = Mode == LibraryMode.Movies ? ("movie", "movies") : ("show", "shows");
        CountText = query.Length > 0
            ? $"{Format.Count(_filtered.Count, one, many)} matching “{SearchText.Trim()}”"
            : Format.Count(_filtered.Count, one, many);
        HasNoResults = _filtered.Count == 0 && CurrentCards.Count > 0;
        LayoutRows();
        if (toTop)
            ScrollToTopRequested?.Invoke();
    }

    // ---- duplicates ----

    private readonly Dictionary<CardViewModel, int> _duplicates = [];

    /// <summary>The cards that have a copy, with the place of their group: by title, each group together.</summary>
    private void FindDuplicates()
    {
        foreach (var card in _duplicates.Keys)
            card.Note = null;
        _duplicates.Clear();
        var groups = DuplicateFinder.Groups(CurrentCards, c => c.Ids).OrderBy(g => g[0].SortKey, StringComparer.Ordinal);
        var place = 0;
        foreach (var group in groups)
        {
            foreach (var card in group)
                _duplicates[card] = place;
            place++;
        }
    }

    // ---- needs attention, missing episodes ----

    private readonly Dictionary<CardViewModel, string> _attention = [];
    private readonly Dictionary<long, List<string>> _emptyFolders = [];
    private Dictionary<long, ShowGaps> _gaps = [];
    private int _gapsVersion;

    /// <summary>The folders of this mode's library folders with no video, as the last scan found them.</summary>
    public IReadOnlyList<FolderLink> EmptyFolders =>
        _roots.Values.Where(r => r.Kind == CurrentKind && _emptyFolders.ContainsKey(r.Id))
              .SelectMany(r => _emptyFolders[r.Id].Select(rel =>
              {
                  var path = RelPath.ToFull(r.Path, rel);
                  return new FolderLink(rel, path, new RelayCommand(() => RevealPath(path)));
              }))
              .ToList();

    public bool ShowEmptyFolders => SelectedNav?.Kind == NavKind.NeedsAttention && EmptyFolders.Count > 0;

    public string EmptyFoldersTitle => Format.Count(EmptyFolders.Count, "folder without a video", "folders without a video") + " (a click opens it)";

    /// <summary>What a show lacks by TMDB, for its page; null when nothing or not known.</summary>
    public ShowGaps? GapsFor(SeriesCardViewModel card) => _gaps.GetValueOrDefault(card.Series.Id);

    /// <summary>Worked out again from what TMDB said last time, in the background: no request.</summary>
    private async Task RefreshGapsAsync()
    {
        var version = ++_gapsVersion;
        var shows = _shows.Select(c => c.Series).ToList();
        Dictionary<long, ShowGaps> gaps;
        try
        {
            gaps = await Task.Run(() =>
            {
                var checker = new EpisodeChecker(_s.Database);
                return checker.Gaps(checker.Identified(shows), DateTime.Today);
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return;
        }
        if (version != _gapsVersion)
            return;
        var changed = gaps.Count != _gaps.Count || gaps.Any(g => !_gaps.TryGetValue(g.Key, out var old) || old.Note != g.Value.Note);
        _gaps = gaps;
        if (!changed)
            return;
        if (Mode == LibraryMode.Shows)
            RebuildNav();
        if (SeriesDetail is { } page)
            page.SetGaps(GapsFor(page.Card));
    }

    [RelayCommand]
    private Task CheckEpisodes() => CheckEpisodesAsync(asked: true);

    /// <summary>Asks TMDB which episodes the shows have: by hand from the Scan menu, by itself once a week.</summary>
    private async Task CheckEpisodesAsync(bool asked)
    {
        if (asked ? IsBusy("check") : IsScanning)
            return;
        using var client = _s.CreateTmdbClient();
        if (client is null)
        {
            if (asked)
                ShowStatusFor("Add a TMDB API key in Settings first.", 12);
            return;
        }
        var checker = new EpisodeChecker(_s.Database);
        var shows = checker.Identified(_shows.Select(c => c.Series).ToList());
        if (shows.Count == 0)
        {
            if (asked)
                ShowStatusFor("No show is identified on TMDB yet: download the metadata first.", 12);
            return;
        }

        BeginBusy("Asking TMDB about the episodes…");
        _scanCts = new CancellationTokenSource();
        EpisodeCheckResult result;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                ScanIndeterminate = false;
                ScanProgress = 100.0 * p.Done / p.Total;
                ScanStatus = $"Checking episodes  {p.Done:N0} of {p.Total:N0}";
            });
            var token = _scanCts.Token;
            result = await Task.Run(() => checker.CheckAsync(shows, client, progress, token));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or IOException)
        {
            result = new EpisodeCheckResult(0, 0, e.Message, false);
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
        if (result.Error is null && !result.Cancelled)
        {
            _s.Settings.LastEpisodeCheck = DateTime.UtcNow;
            _s.SaveSettings();
        }
        await RefreshGapsAsync();
        var lacking = _gaps.Count;
        var news = _gaps.Values.Sum(g => g.NewCount);
        var text = result.Error is { } error ? $"Episode check stopped: {error}"
            : result.Cancelled ? "Episode check cancelled"
            : lacking == 0 ? $"Episodes checked: every show has all of them ({Format.Count(result.Shows, "show", "shows")})"
            : $"Episodes checked: {Format.Count(lacking, "show lacks", "shows lack")} some" + (news > 0 ? $", {Format.Count(news, "episode is", "episodes are")} new" : "")
              + ". See Missing episodes.";
        // At startup a TMDB that can't be reached (no network) isn't news; it is asked again next time
        if (asked || (result.Error is null ? lacking > 0 : !result.Offline))
            ShowStatusFor(text, 14);
    }

    // ---- filters ----

    private const string AnyFilter = "Any";

    public IReadOnlyList<string> FilterResolutions { get; } = [AnyFilter, "4K", "1080p", "720p", LibraryFilter.LowerResolution];
    public IReadOnlyList<string> FilterRatings { get; } = [AnyFilter, "6+", "7+", "8+"];

    [ObservableProperty]
    public partial IReadOnlyList<string> FilterDecades { get; private set; } = [AnyFilter];

    [ObservableProperty]
    public partial IReadOnlyList<string> FilterCollections { get; private set; } = [AnyFilter];

    [ObservableProperty]
    public partial string? SelectedResolution { get; set; } = AnyFilter;

    [ObservableProperty]
    public partial string? SelectedDecade { get; set; } = AnyFilter;

    [ObservableProperty]
    public partial string? SelectedRating { get; set; } = AnyFilter;

    [ObservableProperty]
    public partial string? SelectedCollection { get; set; } = AnyFilter;

    [ObservableProperty]
    public partial bool OnlyHdr { get; set; }

    partial void OnSelectedResolutionChanged(string? value) => FilterChanged();
    partial void OnSelectedDecadeChanged(string? value) => FilterChanged();
    partial void OnSelectedRatingChanged(string? value) => FilterChanged();
    partial void OnSelectedCollectionChanged(string? value) => FilterChanged();
    partial void OnOnlyHdrChanged(bool value) => FilterChanged();

    private void FilterChanged()
    {
        OnPropertyChanged(nameof(HasFilter));
        ApplyFilter(toTop: true);
    }

    private static string? Chosen(string? value) => value is null or AnyFilter ? null : value;

    public bool HasFilter => CurrentFilter != new LibraryFilter();

    private LibraryFilter CurrentFilter => new()
    {
        Resolution = Chosen(SelectedResolution),
        Hdr = OnlyHdr,
        Decade = Chosen(SelectedDecade) is { } decade ? int.Parse(decade.TrimEnd('s'), CultureInfo.InvariantCulture) : null,
        MinRating = Chosen(SelectedRating) is { } rating ? double.Parse(rating.TrimEnd('+'), CultureInfo.InvariantCulture) : null,
        Collection = Chosen(SelectedCollection),
    };

    [RelayCommand]
    private void ClearFilters()
    {
        var suppressed = _suppressFilter;
        _suppressFilter = true;
        SelectedResolution = SelectedDecade = SelectedRating = SelectedCollection = AnyFilter;
        OnlyHdr = false;
        _suppressFilter = suppressed;
        ApplyFilter(toTop: true);
    }

    /// <summary>The decades and collections there are in this mode; a choice that has gone goes back to Any.</summary>
    private void RefreshFilterChoices()
    {
        List<string> decades = [AnyFilter, .. CurrentCards.Where(c => c.Year is not null).Select(c => c.Year!.Value / 10 * 10)
                                                          .Distinct().OrderDescending().Select(d => $"{d}s")];
        List<string> collections = [AnyFilter, .. _movies.Select(c => c.Movie.Collection).OfType<string>().Where(n => n.Length > 0)
                                                         .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        var decade = SelectedDecade;
        var collection = SelectedCollection;
        if (!FilterDecades.SequenceEqual(decades))
            FilterDecades = decades;
        if (!FilterCollections.SequenceEqual(collections))
            FilterCollections = collections;
        SelectedDecade = decades.Contains(decade ?? "") ? decade : AnyFilter;
        SelectedCollection = collections.Contains(collection ?? "") ? collection : AnyFilter;
    }

    /// <summary>From a movie's page: the other movies of its collection.</summary>
    public void ShowCollection(string name)
    {
        CloseDetail();
        ClosePerson();
        ClearSelection();
        Mode = LibraryMode.Movies;
        ClearFilters();
        SearchText = "";
        SelectedNav = NavItems[0];
        SelectedCollection = name;
    }

    /// <summary>Another view, search or order: start from the top, not where the last one was.</summary>
    public event Action? ScrollToTopRequested;

    public void SetViewport(double width, double scaling)
    {
        if (Math.Abs(width - _viewportWidth) < 1 && Math.Abs(scaling - _scaling) < 0.01)
            return;
        _viewportWidth = width;
        _scaling = scaling;
        LayoutRows();
    }

    private void LayoutRows()
    {
        if (_viewportWidth <= 0)
            return;
        var min = Math.Clamp(_s.Settings.CardWidth, 110, 320);
        var columns = Math.Max(1, (int)((_viewportWidth + Gap) / (min + Gap)));
        var width = Math.Floor((_viewportWidth - Gap * (columns - 1)) / columns);
        CardViewModel.DecodeWidth = Math.Min(AppServices.PosterWidth, (int)Math.Ceiling(width * _scaling));
        foreach (var card in _filtered)
            card.CardWidth = width;
        Rows = _filtered.Chunk(columns).Select(chunk => new MovieRow(chunk)).ToList();
    }

    // ---- scanning ----

    [RelayCommand]
    private Task Scan() => RunScanAsync(full: false);

    [RelayCommand]
    private Task FullScan() => RunScanAsync(full: true);

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private async Task RunScanAsync(bool full)
    {
        if (IsScanning || _roots.Count == 0)
            return;

        IsScanning = true;
        _statusVersion++;
        // Both write the watched marks: the scan from what it read before
        await _liveSync.WaitAsync();
        _scanCts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanIndeterminate = p.Total == 0;
            ScanProgress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ScanStatus = p.Total > 0 ? $"{p.Stage}  {p.Done:N0} of {p.Total:N0}" : p.Stage + "…";
        });

        int added = 0, updated = 0, removed = 0;
        var notes = new List<string>();
        var cancelled = false;
        string? stopped = null;
        try
        {
            foreach (var root in _roots.Values.OrderBy(r => r.Kind))
            {
                var result = root.Kind == LibraryKind.Movies
                    ? await Task.Run(() => _s.Scanner.ScanAsync(root, full, progress, _scanCts.Token))
                    : await Task.Run(() => _s.SeriesScanner.ScanAsync(root, full, progress, _scanCts.Token));
                if (result.RootAvailable && !result.Cancelled && root.Kind == LibraryKind.Movies)
                    _emptyFolders[root.Id] = result.EmptyFolders;
                if (result.RootAvailable)
                    _unavailable.Remove(root.Id);
                else
                {
                    _unavailable.Add(root.Id);
                    notes.Add($"{root.Name} is not reachable");
                }
                added += result.Added;
                updated += result.Updated;
                removed += result.Removed;
                if (result.Errors.Count > 0)
                    notes.Add(Format.Count(result.Errors.Count, "folder could not be read", "folders could not be read"));
                Reload();
                if (result.Cancelled)
                {
                    cancelled = true;
                    break;
                }
            }
            if (full && !cancelled && _unavailable.Count == 0)
            {
                var keys = _movies.Select(c => c.Movie.ThumbKey).Concat(_shows.Select(c => c.Series.ThumbKey)).OfType<string>().ToHashSet();
                await Task.Run(() => _s.Posters.Prune(keys));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Said on screen: left to the task it would just stop, with "Saving" still in the status bar
            stopped = $"The scan stopped: {e.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
            _liveSync.Release();
        }

        var changes = added + updated + removed == 0
            ? "no changes"
            : string.Join(", ", new[] { (added, "added"), (updated, "updated"), (removed, "removed") }
                                    .Where(x => x.Item1 > 0).Select(x => $"{x.Item1:N0} {x.Item2}"));
        var text = stopped ?? (cancelled ? "Scan cancelled" : $"Library scanned in {watch.Elapsed.TotalSeconds:0.0} s · {changes}");
        if (notes.Count > 0)
            text += " · " + string.Join(" · ", notes);
        ShowStatusFor(text, notes.Count > 0 || stopped is not null ? 20 : 8);

        if (!cancelled && stopped is null && _s.Settings.FetchAfterScan && _s.Settings.HasTmdbKey)
            await RunFetchAsync(retryNotFound: false, asked: false);
    }

    // ---- Emby or Jellyfin ----

    /// <summary>More unmarked than this, unasked, is more likely a server wiped or another user than a change of mind.</summary>
    private const int MostUnmarkedAlone = 20;

    public bool HasMediaServer => _s.Settings.HasMediaServer;
    public string SyncMenuText => $"Sync watched with {_s.Settings.MediaServerProduct}";

    [RelayCommand]
    private Task SyncMediaServer() => SyncMediaServerAsync(asked: true);

    /// <summary>
    /// Asked: says what would change and waits for a yes. At startup it goes on alone, but
    /// not the first time and not when a lot would be unmarked.
    /// </summary>
    private async Task SyncMediaServerAsync(bool asked)
    {
        if (asked ? IsBusy("sync") : IsScanning)
            return;
        var product = _s.Settings.MediaServerProduct;
        using var client = _s.CreateMediaServerClient();
        if (client is null || Dialogs is null || _s.Settings.MediaServerUserId is not { } user)
            return;

        BeginBusy($"Asking {product}…");
        await _liveSync.WaitAsync();   // a few titles going on their own finish first
        _scanCts = new CancellationTokenSource();
        string text;
        var touched = false;
        try
        {
            await client.ConnectAsync(_scanCts.Token);
            var sync = new MediaServerSync(_s.Database, client, user);
            var token = _scanCts.Token;
            var plan = await Task.Run(() => sync.PlanAsync(token));
            if (!plan.HasChanges)
            {
                await Task.Run(() => sync.ApplyAsync(plan, null, token));   // keeps what the two agree on
                text = $"In step with {product}: {Format.Count(plan.Pairs.Count, "title", "titles")} on both sides"
                       + (plan.NotFound > 0 ? $", {plan.NotFound:N0} only on the server" : "");
            }
            else if (!asked && (plan.FirstTime || plan.UnwatchedHere + plan.UnwatchedThere > MostUnmarkedAlone))
            {
                text = $"Sync with {product} not done: it would change a lot. Look at it from the Scan menu.";
            }
            else if (asked && !await ConfirmSyncAsync(plan, product))
            {
                text = "";
            }
            else
            {
                ScanIndeterminate = false;
                ScanStatus = $"Syncing with {product}…";
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    ScanProgress = 100.0 * p.Done / p.Total;
                    ScanStatus = $"Telling {product}  {p.Done:N0} of {p.Total:N0}";
                });
                touched = true;
                var result = await Task.Run(() => sync.ApplyAsync(plan, progress, token));
                text = $"Synced with {product}: {SyncSummary(plan)}"
                       + (result.Failed > 0 ? $" · {Format.Count(result.Failed, "title", "titles")} refused: {result.FirstError}" : "");
            }
        }
        catch (OperationCanceledException) when (_scanCts.IsCancellationRequested)
        {
            text = "Sync cancelled";
            touched = true;
        }
        catch (Exception e)
        {
            // At startup a server that isn't there (the laptop away from home) isn't news
            text = !asked && e is HttpRequestException or TaskCanceledException ? "" : $"Sync with {product} stopped: {e.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanIndeterminate = false;
            ScanStatus = "";
            _scanCts.Dispose();
            _scanCts = null;
            _liveSync.Release();
        }

        if (touched)
        {
            var movie = Detail?.Card;
            var show = SeriesDetail?.Card;
            Reload();
            if (movie is not null && _movies.Contains(movie))
                OpenDetail(movie);
            else if (show is not null && _shows.Contains(show))
                OpenSeries(show);
        }
        if (text.Length > 0)
            ShowStatusFor(text, 14);
    }

    // ---- Emby or Jellyfin, a few titles at a time ----

    private readonly Dictionary<(PlaybackKind, long), SyncTitle> _toSync = [];
    private readonly SemaphoreSlim _liveSync = new(1, 1);
    private bool _syncQueued;

    /// <summary>
    /// More titles than this unmarked (or their place cleared) by the server in one go is left to
    /// the sync of the Scan menu, which shows it first: a server reinstalled, or a library added
    /// again, keeps Jellyfin's ids and forgets what was watched.
    /// </summary>
    private const int MostUnmarkedLive = 3;

    /// <summary>
    /// What was just played or marked, or the page just opened: synced on their own a moment
    /// later, together with whatever else comes meanwhile. Quiet: the next whole sync does what
    /// this one can't.
    /// </summary>
    private void QueueSync(IEnumerable<SyncTitle> titles, double seconds = 1.5)
    {
        if (!_s.Settings.LiveSync || !_s.Settings.HasMediaServer)
            return;
        foreach (var title in titles)
            _toSync[(title.Kind, title.LocalId)] = title;
        if (_syncQueued || _toSync.Count == 0)
            return;
        _syncQueued = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _syncQueued = false;
            _ = SyncQueuedAsync();
        }, TimeSpan.FromSeconds(seconds));
    }

    private async Task SyncQueuedAsync()
    {
        if (IsScanning)
        {
            QueueSync([], 10);   // after the scan or the whole sync
            return;
        }
        await _liveSync.WaitAsync();
        try
        {
            var titles = Fresh(_toSync.Values.ToList());
            _toSync.Clear();
            using var client = _s.CreateMediaServerClient();
            if (client is null || _s.Settings.MediaServerUserId is not { } user || titles.Count == 0)
                return;
            var url = _s.Settings.MediaServerUrl;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(cts.Token);
            var sync = new MediaServerSync(_s.Database, client, user);
            var plan = await Task.Run(() => sync.PlanForAsync(titles, cts.Token));
            if (plan.Pairs.Count == 0)
                return;
            static bool Takes(SyncPair p) => p.Decision is { WatchedToKinora: true, Watched: false } || p.Decision.Position == PositionMove.ClearKinora;
            var taken = plan.Pairs.Count(Takes);
            if (taken > MostUnmarkedLive)
            {
                // Only what adds goes on; the rest stays to do, for the sync that asks first
                plan = new SyncPlan { Pairs = plan.Pairs.Where(p => !Takes(p)).ToList() };
                ShowStatusFor($"{_s.Settings.MediaServerProduct} has {taken:N0} of these as not watched: left as they are here. The sync in the Scan menu shows them.", 16);
            }
            // The settings may have changed meanwhile: another server or user starts from a whole sync
            if (_s.Settings.MediaServerUserId != user || _s.Settings.MediaServerUrl != url)
                return;
            await Task.Run(() => sync.ApplyAsync(plan, null, cts.Token));
            if (plan.WatchedHere + plan.UnwatchedHere + plan.PositionsHere > 0)
                ShowServerChanges(plan);
        }
        catch (Exception e) when (e is MediaServerException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
                                  or Microsoft.Data.Sqlite.SqliteException)
        {
        }
        finally
        {
            _liveSync.Release();
        }
    }

    /// <summary>Episodes as they are now: a page may have given older copies.</summary>
    private List<SyncTitle> Fresh(List<SyncTitle> titles)
    {
        var shows = new Dictionary<long, Dictionary<long, Episode>>();
        var result = new List<SyncTitle>();
        foreach (var title in titles)
        {
            if (title is { Series: { } series, Episode: { } episode })
            {
                if (!shows.TryGetValue(series.Id, out var episodes))
                    shows[series.Id] = episodes = _s.Database.GetEpisodes(series.Id).ToDictionary(e => e.Id);
                if (episodes.TryGetValue(episode.Id, out var now))
                    result.Add(SyncTitle.Of(series, now));
            }
            else if (title.Movie is { } movie && _movies.FirstOrDefault(c => c.Movie.Id == movie.Id) is { } card)
            {
                result.Add(SyncTitle.Of(card.Movie));   // a scan may have given the card a new copy
            }
        }
        return result;
    }

    /// <summary>The server had something newer: the posters and the open page follow.</summary>
    private void ShowServerChanges(SyncPlan plan)
    {
        var changed = plan.Pairs.Where(p => p.Decision.WatchedToKinora || p.Decision.Position is PositionMove.ToKinora or PositionMove.ClearKinora).ToList();
        foreach (var pair in changed.Where(p => p.Movie is not null))
        {
            if (_movies.FirstOrDefault(c => c.Movie.Id == pair.LocalId) is not { } card)
                continue;
            if (pair.Decision.WatchedToKinora)
                card.Movie.Watched = pair.Decision.Watched;   // a scan meanwhile may have given the card another copy
            card.NotifyWatchedChanged();
            var kept = _s.Database.GetPlayback(PlaybackKind.Movie, card.Movie.Id);
            card.Started = PlaybackRules.SeenFraction(kept);
            card.LastPlayed = kept?.Updated ?? 0;
            if (Detail?.Card == card)
            {
                Detail.NotifyWatchedChanged();
                Detail.NotifyStartedChanged();
            }
        }
        foreach (var group in changed.Where(p => p.Series is not null).GroupBy(p => p.Series!.Id))
        {
            if (_shows.FirstOrDefault(c => c.Series.Id == group.Key) is not { } card)
                continue;
            var all = _s.Database.GetEpisodes(card.Series.Id);
            card.SetWatchedCount(all.Count(e => e.Watched));
            ReadProgress(card);
            if (SeriesDetail?.Card == card)
            {
                SeriesDetail.EpisodesChanged(all);
                var started = _s.Database.GetPlaybackPositions(PlaybackKind.Episode);
                foreach (var pair in group)
                    SeriesDetail.SetStarted(pair.LocalId, PlaybackRules.SeenFraction(started.GetValueOrDefault(pair.LocalId)));
            }
        }
        AfterWatchedChange();
        ShowStatusFor($"From {_s.Settings.MediaServerProduct}: {Format.Count(changed.Count, "title", "titles")} brought up to date", 8);
    }

    private async Task<bool> ConfirmSyncAsync(SyncPlan plan, string product)
    {
        var message = $"{Format.Count(plan.Pairs.Count, "title is", "titles are")} on both sides"
                      + (plan.NotFound > 0 ? $", {plan.NotFound:N0} only on {product}." : ".")
                      + $"\n\nIn Kinora: {plan.WatchedHere:N0} marked watched, {plan.UnwatchedHere:N0} unmarked, {Format.Count(plan.PositionsHere, "place to resume", "places to resume")}."
                      + $"\nOn {product}: {plan.WatchedThere:N0} marked watched, {plan.UnwatchedThere:N0} unmarked, {Format.Count(plan.PositionsThere, "place to resume", "places to resume")}.";
        if (plan.FirstTime)
            message += "\n\nThis is the first sync: what is watched on one side becomes watched on the other, and nothing is unmarked.";
        ScanStatus = "Sync: waiting for your answer";
        return await Dialogs!.ConfirmAsync($"Sync with {product}", message, "Sync");
    }

    private static string SyncSummary(SyncPlan plan)
    {
        var parts = new List<string>();
        if (plan.WatchedHere + plan.UnwatchedHere + plan.PositionsHere > 0)
            parts.Add($"{plan.WatchedHere + plan.UnwatchedHere + plan.PositionsHere:N0} changes here");
        if (plan.WatchedThere + plan.UnwatchedThere + plan.PositionsThere > 0)
            parts.Add($"{plan.WatchedThere + plan.UnwatchedThere + plan.PositionsThere:N0} on the server");
        return string.Join(", ", parts);
    }

    // ---- online metadata ----

    /// <summary>From the Scan menu: also tries again what TMDB didn't find last time.</summary>
    [RelayCommand]
    private Task FetchMissing() => RunFetchAsync(retryNotFound: true, asked: true);

    private async Task RunFetchAsync(bool retryNotFound, bool asked)
    {
        if (IsScanning)
            return;
        using var client = _s.CreateTmdbClient();
        if (client is null)
        {
            if (asked)
                ShowStatusFor("Add a TMDB API key in Settings first.", 12);
            return;
        }

        var before = ScanStatus;
        IsScanning = true;
        ScanIndeterminate = true;
        _statusVersion++;
        ScanStatus = "Looking for what is missing…";
        _scanCts = new CancellationTokenSource();
        var sum = new FetchResult();
        try
        {
            var withCast = _s.Database.GetCastNames().Keys.ToHashSet();
            var movies = _movies.Select(c => c.Movie).ToList();
            var shows = _shows.Select(c => c.Series).ToList();
            var available = _roots.Values.Where(r => !_unavailable.Contains(r.Id)).ToList();
            var work = await Task.Run(() => available
                .Select(r => (Root: r, Movies: r.Kind == LibraryKind.Movies ? _s.Fetcher.Candidates(r, movies, withCast, retryNotFound) : [],
                                       Shows: r.Kind == LibraryKind.Series ? _s.SeriesFetcher.Candidates(r, shows, retryNotFound) : []))
                .Where(w => w.Movies.Count + w.Shows.Count > 0)
                .ToList());
            var total = work.Sum(w => w.Movies.Count + w.Shows.Count);
            if (total == 0)
            {
                IsScanning = false;
                if (asked)
                    ShowStatusFor("Nothing to download: everything has its metadata.", 8);
                else
                    ShowStatusFor(before, 8);   // keep what the scan said
                return;
            }

            var offset = 0;
            var progress = new Progress<FetchProgress>(p =>
            {
                ScanIndeterminate = false;
                ScanProgress = 100.0 * (offset + p.Done) / total;
                ScanStatus = $"Downloading metadata  {offset + p.Done:N0} of {total:N0}";
            });
            ScanStatus = "Asking TMDB…";

            foreach (var (root, rootMovies, rootShows) in work)
            {
                var r = rootMovies.Count > 0
                    ? await Task.Run(() => _s.Fetcher.FetchAsync(root, rootMovies, client, _s.Settings.WriteToMediaFolders, refresh: false, progress, _scanCts.Token))
                    : await Task.Run(() => _s.SeriesFetcher.FetchAsync(root, rootShows, client, _s.Settings.WriteToMediaFolders, refresh: false, progress, _scanCts.Token));
                offset += rootMovies.Count + rootShows.Count;
                Add(sum, r);
                Reload();
                if (r.Error is not null || r.Cancelled)
                    break;
            }
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }

        ShowStatusFor(FetchSummary(sum), sum.Error is null && sum.NotFound == 0 && sum.WriteErrors.Count == 0 ? 10 : 25);
    }

    private static void Add(FetchResult sum, FetchResult r)
    {
        sum.Identified += r.Identified;
        sum.Completed += r.Completed;
        sum.NotFound += r.NotFound;
        sum.Failed += r.Failed;
        sum.FilesWritten += r.FilesWritten;
        sum.WriteErrors.AddRange(r.WriteErrors);
        sum.Error ??= r.Error;
        sum.Cancelled |= r.Cancelled;
    }

    private static string FetchSummary(FetchResult r)
    {
        if (r.Error is not null)
            return r.Error + " Check the key in Settings.";
        var parts = new List<string>();
        if (r.Completed > 0) parts.Add($"{r.Completed:N0} completed");
        if (r.Identified > 0) parts.Add($"{r.Identified:N0} identified");
        if (r.NotFound > 0) parts.Add($"{r.NotFound:N0} to identify by hand (see Not identified)");
        if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed, TMDB didn't answer");
        if (r.FilesWritten > 0) parts.Add(Format.Count(r.FilesWritten, "file written in the folders", "files written in the folders"));
        if (r.WriteErrors.Count > 0) parts.Add($"{r.WriteErrors.Count:N0} folders not writable");
        var text = "Metadata: " + (parts.Count > 0 ? string.Join(", ", parts) : "nothing new");
        return r.Cancelled ? "Download cancelled · " + text : text;
    }

    /// <summary>Asks TMDB again for one title, ignoring what was kept.</summary>
    public async Task RefreshMetadataAsync(CardViewModel card)
    {
        if (IsScanning || !_roots.TryGetValue(card.RootId, out var root))
            return;
        using var client = _s.CreateTmdbClient();
        if (client is null)
        {
            ShowStatusFor("Add a TMDB API key in Settings first.", 12);
            return;
        }
        BeginBusy($"Asking TMDB about {card.Title}…");
        FetchResult result;
        try
        {
            result = card switch
            {
                MovieCardViewModel m => await Task.Run(() => _s.Fetcher.FetchAsync(root, [m.Movie], client, _s.Settings.WriteToMediaFolders, refresh: true, null, CancellationToken.None)),
                SeriesCardViewModel s => await Task.Run(() => _s.SeriesFetcher.FetchAsync(root, [s.Series], client, _s.Settings.WriteToMediaFolders, refresh: true, null, CancellationToken.None)),
                _ => new FetchResult(),
            };
        }
        finally
        {
            IsScanning = false;
        }
        AfterSingleFetch(card, result);
    }

    /// <summary>The user says which TMDB movie or show this is.</summary>
    public async Task IdentifyAsync(CardViewModel card)
    {
        if (IsScanning || Dialogs is null || !_roots.TryGetValue(card.RootId, out var root))
            return;
        using var client = _s.CreateTmdbClient();
        if (client is null)
        {
            ShowStatusFor("Add a TMDB API key in Settings first.", 12);
            return;
        }
        var key = card switch
        {
            MovieCardViewModel m => m.Movie.VideoFile,
            SeriesCardViewModel s => s.Series.Folder,
            _ => "",
        };
        if (!card.NeedsMatch && _s.Database.GetMatch(root.Id, key) is null)
        {
            // The ids come from the folder (nfo or name): the folder wins, a choice here would not count
            ShowStatusFor($"“{card.Title}” is identified by its folder (the nfo or the name): change it there.", 14);
            return;
        }

        string? chosen;
        var isShow = card is SeriesCardViewModel;
        using (var identify = new IdentifyViewModel(isShow ? client.SearchTvAsync : client.SearchMovieAsync,
                                                    isShow ? "Which show is this?" : "Which movie is this?", key, card.Title, card.Year))
            chosen = await Dialogs.ShowIdentifyAsync(identify);
        if (chosen is null)
            return;

        BeginBusy("Getting it from TMDB…");
        FetchResult result;
        try
        {
            result = card switch
            {
                MovieCardViewModel m => await Task.Run(() => _s.Fetcher.IdentifyAsync(root, m.Movie, chosen, client, _s.Settings.WriteToMediaFolders, CancellationToken.None)),
                SeriesCardViewModel s => await Task.Run(() => _s.SeriesFetcher.IdentifyAsync(root, s.Series, chosen, client, _s.Settings.WriteToMediaFolders, CancellationToken.None)),
                _ => new FetchResult(),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TmdbException)
        {
            result = new FetchResult { Error = e.Message };
        }
        finally
        {
            IsScanning = false;
        }
        AfterSingleFetch(card, result);
    }

    private void BeginBusy(string text)
    {
        IsScanning = true;
        ScanIndeterminate = true;
        _statusVersion++;
        ScanStatus = text;
    }

    private void AfterSingleFetch(CardViewModel card, FetchResult result)
    {
        var movieShowing = Detail?.Card == card;
        var showShowing = SeriesDetail?.Card == card;
        Reload();
        if (movieShowing && card is MovieCardViewModel m && _movies.Contains(m))
            OpenDetail(m);
        if (showShowing && card is SeriesCardViewModel s && _shows.Contains(s))
            OpenSeries(s);
        ShowStatusFor(FetchSummary(result), 10);
    }

    public bool HasTmdbKey => _s.Settings.HasTmdbKey;

    private async void ShowStatusFor(string text, int seconds)
    {
        var version = ++_statusVersion;
        ScanStatus = text;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (version == _statusVersion && !IsScanning)
            ScanStatus = "";
    }

    // ---- pages ----

    public void OpenDetail(MovieCardViewModel card, bool fromRandom = false)
    {
        var old = Detail;
        _roots.TryGetValue(card.RootId, out var root);
        Detail = new MovieDetailViewModel(this, _s, card, root, _unavailable.Contains(card.RootId)) { FromRandom = fromRandom };
        old?.Dispose();
        CloseOthers(keepMovie: true);
        QueueSync([SyncTitle.Of(card.Movie)]);
    }

    public void OpenSeries(SeriesCardViewModel card, int? season = null, bool fromRandom = false)
    {
        var old = SeriesDetail;
        _roots.TryGetValue(card.RootId, out var root);
        SeriesDetail = new SeriesDetailViewModel(this, _s, card, root, _unavailable.Contains(card.RootId), season) { FromRandom = fromRandom };
        old?.Dispose();
        CloseOthers(keepMovie: false);
        if (_s.Settings is { LiveSync: true, HasMediaServer: true })
            QueueSync(_s.Database.GetEpisodes(card.Series.Id).Select(e => SyncTitle.Of(card.Series, e)));
    }

    [RelayCommand]
    private void CloseDetail()
    {
        var oldMovie = Detail;
        var oldShow = SeriesDetail;
        Detail = null;
        SeriesDetail = null;
        oldMovie?.Dispose();
        oldShow?.Dispose();
    }

    /// <summary>
    /// The page of an actor or director, over the page it was opened from (Back returns there).
    /// Lists every movie and show of the library they are in, newest first.
    /// </summary>
    public void OpenPerson(string name, string? tmdbId, bool director)
    {
        var movies = _movies.ToDictionary(c => c.Movie.Id);
        var shows = _shows.ToDictionary(c => c.Series.Id);
        var titles = _s.Database.GetCredits(name, tmdbId)
            .Select(c => c.IsSeries
                ? shows.TryGetValue(c.Id, out var s) ? new PersonTitle(s, c.Role) : null
                : movies.TryGetValue(c.Id, out var m) ? new PersonTitle(m, c.Role) : null)
            .OfType<PersonTitle>()
            .OrderByDescending(t => t.Card.Year ?? 0)
            .ToList();
        var old = Person;
        Person = new PersonViewModel(this, _s, name, tmdbId, director, titles);
        old?.Dispose();
    }

    public void ClosePerson()
    {
        var old = Person;
        Person = null;
        old?.Dispose();
    }

    /// <summary>From a person page: the movie or show page.</summary>
    public void OpenTitle(CardViewModel card)
    {
        switch (card)
        {
            case MovieCardViewModel m: OpenDetail(m); break;
            case SeriesCardViewModel s: OpenSeries(s); break;
        }
    }

    /// <summary>The movies of the library related to one (collection, director, leading actors), worked out off the UI thread.</summary>
    public async Task<List<ShelfItem>> RelatedToAsync(MovieCardViewModel card)
    {
        var cards = _movies.ToList();
        var related = await Task.Run(() => RelatedTitles.Find(card.Movie, cards.Select(c => c.Movie).ToList(), _s.Database.GetLeadingCast(5)));
        var byId = cards.ToDictionary(c => c.Movie.Id);
        return related.Where(r => byId.ContainsKey(r.Movie.Id))
                      .Select(r =>
                      {
                          var other = byId[r.Movie.Id];
                          return new ShelfItem(other, r.Reason, () => OpenDetail(other));
                      })
                      .ToList();
    }

    /// <summary>A movie of the library by its TMDB id (the first copy, when there are two).</summary>
    public MovieCardViewModel? MovieByTmdbId(string tmdbId) => _movies.FirstOrDefault(c => c.Movie.TmdbId == tmdbId);

    /// <summary>One page at a time: opening a movie closes a show page and the person page, and the other way round.</summary>
    private void CloseOthers(bool keepMovie)
    {
        if (keepMovie && SeriesDetail is { } show)
        {
            SeriesDetail = null;
            show.Dispose();
        }
        if (!keepMovie && Detail is { } movie)
        {
            Detail = null;
            movie.Dispose();
        }
        ClosePerson();
    }

    // ---- playing and folders ----

    public void Play(MovieCardViewModel card, string? videoFile)
    {
        if (!_roots.TryGetValue(card.RootId, out var root))
            return;
        PlayFile(new PlayRequest(RelPath.ToFull(root.Path, videoFile ?? card.Movie.VideoFile), card.Title,
                                 card.Movie.Year?.ToString(CultureInfo.InvariantCulture), PlaybackKind.Movie, card.Movie.Id,
                                 () => SetWatched(card, true), watched => MovieKept(card, watched), PosterPath(card.Movie.ThumbKey),
                                 FindSubtitles: added => FindSubtitlesAsync(card, added)));
    }

    public void PlayEpisode(SeriesCardViewModel card, Episode episode)
    {
        if (EpisodeRequest(card, episode) is { } request)
            PlayFile(request);
    }

    /// <summary>An episode for the player, which goes on to the ones around it by itself.</summary>
    private PlayRequest? EpisodeRequest(SeriesCardViewModel card, Episode episode)
    {
        if (!_roots.TryGetValue(card.RootId, out var root))
            return null;
        var what = string.Join(" · ", new[] { episode.Code, episode.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new PlayRequest(RelPath.ToFull(root.Path, episode.VideoFile), card.Title, what, PlaybackKind.Episode, episode.Id,
                               () => SetEpisodesWatched(card, [episode], true), watched => EpisodeKept(card, episode, watched), PosterPath(card.Series.ThumbKey),
                               TrackScope: $"series:{card.Series.Id}",
                               Next: () => Neighbour(EpisodeOrder.After),
                               Previous: () => Neighbour(EpisodeOrder.Before),
                               FindSubtitles: added => FindSubtitlesAsync(card, episode, added),
                               Intro: IntroFor(episode));

        PlayRequest? Neighbour(Func<IEnumerable<Episode>, Episode, Episode?> pick) =>
            pick(_s.Database.GetEpisodes(card.Series.Id), episode) is { } other ? EpisodeRequest(card, other) : null;
    }

    /// <summary>Where the server found the intro of an episode (Emby and Jellyfin look for it in shows only).</summary>
    private Func<CancellationToken, Task<(long StartMs, long EndMs)?>>? IntroFor(Episode episode)
    {
        if (_s.Settings is not { LiveSync: true, HasMediaServer: true })
            return null;
        return async ct =>
        {
            if (_s.Database.GetServerId(PlaybackKind.Episode, episode.Id) is not { } id || _s.Settings.MediaServerUserId is not { } user)
                return null;
            using var client = _s.CreateMediaServerClient();
            if (client is null)
                return null;
            await client.ConnectAsync(ct);
            return await client.GetIntroAsync(user, id, ct);
        };
    }

    private string? PosterPath(string? key) => key is null ? null : _s.Posters.PathFor(key);

    /// <summary>The episode left halfway, otherwise the first not seen yet.</summary>
    public void PlayNextEpisode(SeriesCardViewModel card)
    {
        var next = EpisodeOrder.ToPlay(_s.Database.GetEpisodes(card.Series.Id), card.Resume?.EpisodeId);
        if (next is not null)
            PlayEpisode(card, next);
    }

    /// <summary>
    /// Plays a video: in the built-in player when the settings say so and VLC's engine starts,
    /// otherwise with the app the system opens videos with.
    /// </summary>
    private async void PlayFile(PlayRequest request)
    {
        if (!await ExistsAsync(request.Path))
        {
            ShowStatusFor($"Can't find {request.Path}", 12);
            return;
        }
        if (_s.Settings.UseBuiltInPlayer)
        {
            var (vlc, error) = await _s.Video.GetAsync(_s.Settings.SubtitleScale);
            if (vlc is not null)
            {
                Player?.Close();
                Player = new PlayerViewModel(_s, vlc, request, ClosePlayer);
                return;
            }
            ShowStatusFor($"{error} The video opens in the system's player.", 16);
        }
        try
        {
            Launcher.Open(request.Path);
        }
        catch (Exception e)
        {
            ShowStatusFor($"Can't open the video: {e.Message}", 12);
        }
    }

    private void ClosePlayer(PlayerViewModel player)
    {
        if (Player == player)
            Player = null;
        player.Dispose();
        AfterProgressChange();
        if (_justWatched is { } movie)
        {
            _justWatched = null;
            // Once the player's windows have gone
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = OfferMoveAsync(movie), Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private MovieCardViewModel? _justWatched;

    /// <summary>The folder's rule in Settings: seen here, it may go there (MoviesToWatch to Movies). Always asked.</summary>
    private async Task OfferMoveAsync(MovieCardViewModel card)
    {
        var from = _s.Database.GetRoots().FirstOrDefault(r => r.Id == card.RootId);
        if (from?.WatchedMoveRoot is not { } target || !_roots.TryGetValue(target, out var to) || to.Kind != from.Kind
            || to.Id == from.Id || _unavailable.Contains(to.Id) || _unavailable.Contains(from.Id))
            return;
        await MoveManyAsync([card], to, $"You have watched “{card.Title}”. Move it from {from.Name} to {to.Name}?");
    }

    /// <summary>The "started" ring follows where it stopped (or goes, once watched).</summary>
    private void MovieKept(MovieCardViewModel card, bool watched)
    {
        if (watched)
            _justWatched = card;
        var kept = _s.Database.GetPlayback(PlaybackKind.Movie, card.Movie.Id);
        card.Started = PlaybackRules.SeenFraction(kept);
        card.LastPlayed = kept?.Updated ?? 0;
        if (Detail?.Card == card)
            Detail.NotifyStartedChanged();
        QueueSync([SyncTitle.Of(card.Movie)]);
    }

    private void EpisodeKept(SeriesCardViewModel card, Episode episode, bool watched)
    {
        var seen = PlaybackRules.SeenFraction(_s.Database.GetPlayback(PlaybackKind.Episode, episode.Id));
        if (watched || seen is not null)
            _s.Database.SetSeriesPlayed(card.Series.Id);
        ReadProgress(card);
        if (SeriesDetail?.Card == card)
            SeriesDetail.SetStarted(episode.Id, seen);
        QueueSync([SyncTitle.Of(card.Series, episode)]);
    }

    private void ReadProgress(SeriesCardViewModel card) =>
        card.SetProgress(_s.Database.GetStartedEpisodes().GetValueOrDefault(card.Series.Id),
                         _s.Database.GetSeriesPlayed().GetValueOrDefault(card.Series.Id));

    /// <summary>Marked unwatched from the start: it leaves "Continue watching", unless an episode is still halfway.</summary>
    private void ForgetProgress(SeriesCardViewModel card)
    {
        _s.Database.ClearSeriesPlayed(card.Series.Id);
        ReadProgress(card);
    }

    /// <summary>"Continue watching" comes and goes with what is in it.</summary>
    private void AfterProgressChange()
    {
        var going = CurrentCards.Count(c => c.InProgress);
        var item = NavItems.FirstOrDefault(n => n.Kind == NavKind.Continue);
        if ((item is null) != (going == 0))
        {
            RebuildNav();
        }
        else if (item is not null)
        {
            item.Count = going;
            if (SelectedNav == item)
                ApplyFilter();
        }
    }

    public async void Reveal(CardViewModel card)
    {
        if (!_roots.TryGetValue(card.RootId, out var root))
            return;
        string path;
        if (card is MovieCardViewModel m)
        {
            path = RelPath.ToFull(root.Path, m.Movie.VideoFile);
            if (!await ExistsAsync(path))
                path = RelPath.ToFull(root.Path, m.Movie.Folder);
        }
        else
        {
            path = RelPath.ToFull(root.Path, ((SeriesCardViewModel)card).Series.Folder);
        }
        RevealPath(path);
    }

    public void RevealEpisode(SeriesCardViewModel card, Episode episode)
    {
        if (_roots.TryGetValue(card.RootId, out var root))
            RevealPath(RelPath.ToFull(root.Path, episode.VideoFile));
    }

    private void RevealPath(string path)
    {
        try
        {
            Launcher.Reveal(path);
        }
        catch (Exception e)
        {
            ShowStatusFor($"Can't open the folder: {e.Message}", 12);
        }
    }

    // ---- updates ----

    public static Version CurrentVersion { get; } =
        typeof(MainWindowViewModel).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText), nameof(UpdateButtonText))]
    public partial Core.Updates.UpdateInfo? AvailableUpdate { get; private set; }

    public bool HasUpdate => AvailableUpdate is not null;
    public string UpdateText => AvailableUpdate is { } u ? $"Kinora {u.Version.ToString(3)} is available (you have {CurrentVersion.ToString(3)})." : "";
    public string UpdateButtonText => Core.Updates.UpdateService.CanInstallItself ? "Update now" : "Download";

    /// <summary>At startup, at most once a day, quietly: only a newer version shows anything.</summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        Core.Updates.UpdateService.CleanUp();
        var settings = _s.Settings;
        if (!settings.CheckForUpdates
            || (settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < Core.Updates.UpdateService.CheckInterval))
            return;
        await Task.Delay(TimeSpan.FromSeconds(10));   // let the first scan go first
        await CheckForUpdatesAsync();
    }

    /// <summary>Asks GitHub; returns what to tell the user when they asked.</summary>
    public async Task<string> CheckForUpdatesAsync()
    {
        try
        {
            var update = await new Core.Updates.UpdateService(CurrentVersion).CheckAsync();
            _s.Settings.LastUpdateCheck = DateTime.UtcNow;
            _s.SaveSettings();
            AvailableUpdate = update;
            return update is null ? $"Kinora {CurrentVersion.ToString(3)} is the latest version." : UpdateText;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return $"Can't reach GitHub: {e.Message}";
        }
    }

    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;

    [RelayCommand]
    private void OpenUpdatePage() => Launcher.OpenUrl(AvailableUpdate?.PageUrl ?? Core.Updates.UpdateService.ReleasesPage);

    /// <summary>
    /// Windows, installed with the setup: downloads the new setup, checks it, runs it and closes;
    /// the setup starts Kinora again when it's done. Elsewhere: the release page.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } update)
            return;
        if (!Core.Updates.UpdateService.CanInstallItself)
        {
            OpenUpdatePage();
            return;
        }
        if (IsScanning)
        {
            ShowStatusFor("Wait for the current work to finish, then update.", 10);
            return;
        }

        IsScanning = true;
        ScanIndeterminate = false;
        ScanProgress = 0;
        _statusVersion++;
        ScanStatus = $"Downloading Kinora {update.Version.ToString(3)}…";
        string path;
        try
        {
            var progress = new Progress<double>(p =>
            {
                ScanProgress = p * 100;
                ScanStatus = $"Downloading Kinora {update.Version.ToString(3)}  {Format.Size((long)(p * update.AssetSize))} of {Format.Size(update.AssetSize)}";
            });
            path = await new Core.Updates.UpdateService(CurrentVersion).DownloadAsync(update, progress);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            IsScanning = false;
            ShowStatusFor($"Update not downloaded: {e.Message}", 20);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1",
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Refused at the UAC prompt (an all-users install), or the file was blocked
            IsScanning = false;
            ShowStatusFor($"The update did not start: {e.Message}", 20);
            return;
        }
        _s.SaveSettings();
        ShutdownRequested?.Invoke();
    }

    /// <summary>The window closes Kinora: the update's setup is waiting for it.</summary>
    public event Action? ShutdownRequested;

    // ---- selection ----

    private readonly List<CardViewModel> _selected = [];
    private CardViewModel? _anchor;

    /// <summary>The keys held at the last click (set by the window): Ctrl adds to the selection, Shift selects a range.</summary>
    public Avalonia.Input.KeyModifiers LastModifiers { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(CanMoveSelection), nameof(SelectionMoveTargets))]
    public partial bool HasSelection { get; private set; }

    public string SelectionText => Format.Count(_selected.Count, "selected", "selected");

    /// <summary>A click on a poster: opens it, or selects when Ctrl/Shift is held or a selection is going on.</summary>
    public void Activate(CardViewModel card)
    {
        var mods = LastModifiers;
        LastModifiers = Avalonia.Input.KeyModifiers.None;
        if (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift) && _anchor is not null && _filtered.Contains(_anchor))
        {
            var a = _filtered.IndexOf(_anchor);
            var b = _filtered.IndexOf(card);
            foreach (var c in _filtered.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                Select(c, true);
            SelectionChanged();
            return;
        }
        if (mods.HasFlag(Avalonia.Input.KeyModifiers.Control) || HasSelection)
        {
            ToggleSelection(card);
            return;
        }
        // Here a click means going on with it; the page is in the menu
        if (SelectedNav?.Kind == NavKind.Continue)
        {
            card.PlayCommand.Execute(null);
            return;
        }
        switch (card)
        {
            case MovieCardViewModel m: OpenDetail(m); break;
            case SeriesCardViewModel s: OpenSeries(s); break;
        }
    }

    public void ToggleSelection(CardViewModel card)
    {
        Select(card, !card.IsSelected);
        _anchor = card;
        SelectionChanged();
    }

    private void Select(CardViewModel card, bool selected)
    {
        card.IsSelected = selected;
        if (selected && !_selected.Contains(card))
            _selected.Add(card);
        else if (!selected)
            _selected.Remove(card);
    }

    private void SelectionChanged()
    {
        var active = _selected.Count > 0;
        if (active != HasSelection)
            foreach (var c in _movies.Cast<CardViewModel>().Concat(_shows))
                c.SelectionActive = active;
        HasSelection = active;
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectionMoveTargets));
        OnPropertyChanged(nameof(CanMoveSelection));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var c in _filtered)
            Select(c, true);
        SelectionChanged();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var c in _selected.ToList())
            Select(c, false);
        _anchor = null;
        SelectionChanged();
    }

    [RelayCommand]
    private void MarkSelectedWatched() => MarkWatched(_selected.ToList(), true);

    [RelayCommand]
    private void MarkSelectedUnwatched() => MarkWatched(_selected.ToList(), false);

    private void MarkWatched(IReadOnlyList<CardViewModel> cards, bool watched)
    {
        foreach (var card in cards)
        {
            switch (card)
            {
                case MovieCardViewModel m when m.Movie.Watched != watched:
                    _s.Database.SetWatched(m.Movie, watched);
                    m.NotifyWatchedChanged();
                    QueueSync([SyncTitle.Of(m.Movie)]);
                    break;
                case SeriesCardViewModel s:
                    var episodes = _s.Database.GetEpisodes(s.Series.Id).Where(e => e.Watched != watched).ToList();
                    if (episodes.Count == 0)
                        break;
                    _s.Database.SetEpisodesWatched(s.Series, episodes, watched);
                    QueueSync(episodes.Select(e => SyncTitle.Of(s.Series, e)));
                    s.SetWatchedCount(watched ? s.Series.EpisodeCount : 0);
                    if (!watched)
                        ForgetProgress(s);
                    break;
            }
        }
        Detail?.NotifyWatchedChanged();
        AfterWatchedChange();
        ShowStatusFor($"{Format.Count(cards.Count, "title", "titles")} marked as {(watched ? "watched" : "unwatched")}", 6);
    }

    public bool CanMoveSelection => SelectionMoveTargets.Count > 0;

    /// <summary>Library folders of the current kind the selection could go to.</summary>
    public IReadOnlyList<MoveTarget> SelectionMoveTargets =>
        !HasSelection ? []
        : _roots.Values.Where(r => r.Kind == CurrentKind && !_unavailable.Contains(r.Id) && _selected.Any(c => c.RootId != r.Id))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => new MoveTarget(r.Name, r.Path, new AsyncRelayCommand(() => MoveManyAsync(_selected.ToList(), r))))
                .ToList();

    /// <summary>Marks everything in a library folder (right click in the sidebar).</summary>
    public void MarkFolderWatched(long rootId, bool watched) =>
        MarkWatched(CurrentCards.Where(c => c.RootId == rootId).ToList(), watched);

    // ---- random ----

    private readonly List<CardViewModel> _randomShown = [];

    /// <summary>
    /// Something to watch: a title not seen yet, picked from the current view with its filters
    /// (any title when all of them are seen). "Another one" doesn't come back to one already
    /// offered until the others have all been.
    /// </summary>
    [RelayCommand]
    private void Random()
    {
        var unseen = _filtered.Where(c => !c.Watched).ToList();
        var all = unseen.Count > 0 ? unseen : _filtered;
        var pool = all.Where(c => !_randomShown.Contains(c)).ToList();
        if (pool.Count == 0)
        {
            var last = _randomShown.Count > 0 && all.Count > 1 ? _randomShown[^1] : null;
            _randomShown.Clear();
            pool = all.Where(c => c != last).ToList();
        }
        if (pool.Count == 0)
            return;
        var card = pool[System.Random.Shared.Next(pool.Count)];
        _randomShown.Add(card);
        switch (card)
        {
            case MovieCardViewModel m: OpenDetail(m, fromRandom: true); break;
            case SeriesCardViewModel s: OpenSeries(s, fromRandom: true); break;
        }
    }

    // ---- moving ----

    private IReadOnlyList<MoveTarget> MoveTargetsFor(CardViewModel card)
    {
        if (!_roots.TryGetValue(card.RootId, out var from))
            return [];
        return _roots.Values.Where(r => r.Kind == from.Kind && r.Id != from.Id && !_unavailable.Contains(r.Id))
                     .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Select(r => new MoveTarget(r.Name, r.Path, new AsyncRelayCommand(() => MoveAsync(card, r))))
                     .ToList();
    }

    /// <summary>Moves one title (from its menu).</summary>
    public Task MoveAsync(CardViewModel card, LibraryRoot to) => MoveManyAsync([card], to);

    /// <summary>
    /// Moves titles to another library folder: asks once (with how much will be copied), then
    /// one after the other with the progress in the status bar. Cancel stops the one being copied
    /// (it stays where it was) and the ones after it; a title whose name is already there is
    /// skipped and the others go on.
    /// </summary>
    /// <param name="question">The question, when it isn't the plain "Move this?".</param>
    public async Task MoveManyAsync(IReadOnlyList<CardViewModel> cards, LibraryRoot to, string? question = null)
    {
        if (Dialogs is null || IsBusy("move"))
            return;
        var items = cards.Where(c => c.RootId != to.Id && _roots.TryGetValue(c.RootId, out var r) && r.Kind == to.Kind)
                         .Select(c => (Card: c, From: _roots[c.RootId]))
                         .ToList();
        if (items.Count == 0)
            return;

        // Busy already while sizing and asking: no scan reads the folders in the meantime
        IsScanning = true;
        ScanIndeterminate = true;
        _statusVersion++;
        ScanStatus = "Looking at what would be moved…";
        long size = 0;
        var go = false;
        try
        {
            size = await Task.Run(() => items.Sum(i => i.Card switch
            {
                MovieCardViewModel m => Core.Moving.LibraryMover.SizeOf(m.Movie, i.From),
                SeriesCardViewModel s => Core.Moving.LibraryMover.SizeOf(s.Series, i.From),
                _ => 0L,
            })).WaitAsync(TimeSpan.FromSeconds(60));

            var allSameVolume = items.All(i => string.Equals(Path.GetPathRoot(i.From.Path), Path.GetPathRoot(to.Path), StringComparison.OrdinalIgnoreCase));
            var how = allSameVolume
                ? "They are on the same drive: the folders are just renamed."
                : $"{Format.Size(size)} will be copied to {to.Path}. Each original is deleted only after its copy has been checked.";
            var what = items.Count == 1 ? $"“{items[0].Card.Title}” from {items[0].From.Name}" : Format.Count(items.Count, "title", "titles");
            ScanStatus = "Move: waiting for your answer";
            go = await Dialogs.ConfirmAsync("Move", $"{question ?? $"Move {what} to {to.Name}?"}\n\n{how}", "Move");
        }
        catch (Exception e)
        {
            NotBusy();
            ShowStatusFor($"Can't read the files to move: {e.Message}", 14);
            return;
        }
        finally
        {
            if (!go && IsScanning)
                NotBusy();
        }
        if (!go)
            return;

        IsScanning = true;
        ScanIndeterminate = false;
        ScanProgress = 0;
        _statusVersion++;
        _scanCts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        long doneBefore = 0;
        var current = "";
        var progress = new Progress<Core.Moving.MoveProgress>(p =>
        {
            var done = doneBefore + p.BytesDone;
            ScanProgress = size == 0 ? 0 : 100.0 * done / size;
            var speed = watch.Elapsed.TotalSeconds > 1 ? $" · {Format.Size((long)(done / watch.Elapsed.TotalSeconds))}/s" : "";
            ScanStatus = $"Moving {current}  {Format.Size(done)} of {Format.Size(size)}{speed}";
        });

        int moved = 0;
        var problems = new List<string>();
        var cancelled = false;
        try
        {
            for (var n = 0; n < items.Count; n++)
            {
                var (card, from) = items[n];
                current = items.Count == 1 ? card.Title : $"{card.Title} ({n + 1} of {items.Count})";
                ScanStatus = $"Moving {current}…";
                var itemSize = card switch
                {
                    MovieCardViewModel m => await Task.Run(() => Core.Moving.LibraryMover.SizeOf(m.Movie, from)),
                    SeriesCardViewModel s => await Task.Run(() => Core.Moving.LibraryMover.SizeOf(s.Series, from)),
                    _ => 0L,
                };
                var result = card switch
                {
                    MovieCardViewModel m => await _s.Mover.MoveMovieAsync(m.Movie, from, to, progress, _scanCts.Token),
                    SeriesCardViewModel s => await _s.Mover.MoveSeriesAsync(s.Series, from, to, progress, _scanCts.Token),
                    _ => new Core.Moving.MoveResult(Core.Moving.MoveOutcome.Failed),
                };
                doneBefore += itemSize;
                switch (result.Outcome)
                {
                    case Core.Moving.MoveOutcome.Moved:
                        moved++;
                        break;
                    case Core.Moving.MoveOutcome.MovedSourceLeft:
                        moved++;
                        problems.Add($"“{card.Title}” moved, but some files stayed in {from.Name}");
                        break;
                    case Core.Moving.MoveOutcome.Cancelled:
                        cancelled = true;
                        break;
                    default:
                        problems.Add($"“{card.Title}” not moved: {result.Error}");
                        break;
                }
                if (cancelled)
                    break;
            }
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }

        var showingMovie = Detail?.Card;
        var showingShow = SeriesDetail?.Card;
        if (moved > 0)
        {
            ClearSelection();
            Reload();
            if (showingMovie is not null && _movies.Contains(showingMovie))
                OpenDetail(showingMovie);
            else if (showingShow is not null && _shows.Contains(showingShow))
                OpenSeries(showingShow);
        }

        var text = moved == 0 && cancelled ? "Move cancelled: nothing changed."
            : items.Count == 1 && moved == 1 && problems.Count == 0 ? $"“{items[0].Card.Title}” moved to {to.Name} in {watch.Elapsed.TotalSeconds:0} s"
            : $"{Format.Count(moved, "title", "titles")} moved to {to.Name} in {watch.Elapsed.TotalSeconds:0} s" + (cancelled ? ", then cancelled" : "");
        if (problems.Count > 0)
            text += " · " + string.Join(" · ", problems.Take(3)) + (problems.Count > 3 ? $" · and {problems.Count - 3} more" : "");
        ShowStatusFor(text, problems.Count == 0 && !cancelled ? 12 : 30);
    }

    // ---- deleting ----

    /// <summary>Deletes one title (from its menu or its page).</summary>
    public Task DeleteAsync(CardViewModel card) => DeleteManyAsync([card]);

    [RelayCommand]
    private Task DeleteSelected() => DeleteManyAsync(_selected.ToList());

    private void NotBusy()
    {
        IsScanning = false;
        ScanIndeterminate = false;
        ScanStatus = "";
    }

    /// <summary>A scan, a move or a delete is going on: said, rather than a click that does nothing.</summary>
    private bool IsBusy(string what)
    {
        if (!IsScanning)
            return false;
        ShowStatusFor($"Wait for the scan (or the move or delete) to finish, then {what} again.", 8);
        return true;
    }

    /// <summary>
    /// Deletes titles from the disk and the library, after one question that says what goes and
    /// how much. Titles that can't be deleted (folder with other titles in it, folder offline)
    /// are left out and said; the others go. Busy from the first moment, so no scan reads the
    /// folders while they are being looked at or deleted.
    /// </summary>
    public async Task DeleteManyAsync(IReadOnlyList<CardViewModel> cards)
    {
        if (Dialogs is null || IsBusy("delete"))
            return;
        IsScanning = true;
        ScanIndeterminate = true;
        _statusVersion++;
        string? status;
        bool touched;
        var gone = new HashSet<CardViewModel>();
        try
        {
            (status, touched) = await DeleteManyCoreAsync(cards, gone);
        }
        catch (Exception e)
        {
            status = $"Delete stopped: {e.Message}";
            touched = true;   // can't tell what went: read the library again
        }
        finally
        {
            IsScanning = false;
            ScanIndeterminate = false;
            ScanStatus = "";
        }

        if (touched)
        {
            ClearSelection();
            if (Detail?.Card is { } m0 && gone.Contains(m0) || SeriesDetail?.Card is { } s0 && gone.Contains(s0))
                CloseDetail();
            Reload();
        }
        if (status is not null)
            ShowStatusFor(status, status.EndsWith(" deleted", StringComparison.Ordinal) ? 10 : 30);
    }

    private async Task<(string? Status, bool Touched)> DeleteManyCoreAsync(IReadOnlyList<CardViewModel> cards, HashSet<CardViewModel> gone)
    {
        var offline = cards.Count(c => _unavailable.Contains(c.RootId) || !_roots.ContainsKey(c.RootId));
        var candidates = cards.Where(c => !_unavailable.Contains(c.RootId) && _roots.ContainsKey(c.RootId)).ToList();
        if (candidates.Count == 0)
            return ("The library folder is offline: nothing can be deleted.", false);

        ScanStatus = "Looking at what would be deleted…";
        List<(CardViewModel Card, Core.Moving.DeletePlan Plan)> plans;
        try
        {
            plans = await Task.Run(() => candidates.Select(c => (Card: c, Plan: c switch
            {
                MovieCardViewModel m => _s.Deleter.PlanMovie(m.Movie, _roots[c.RootId]),
                SeriesCardViewModel s => _s.Deleter.PlanSeries(s.Series, _roots[c.RootId]),
                _ => throw new InvalidOperationException(),
            })).ToList()).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return ($"Can't read the files to delete: {e.Message}", false);
        }

        var ok = plans.Where(p => p.Plan.CanDelete).ToList();
        var refused = plans.Where(p => !p.Plan.CanDelete).Select(p => $"“{p.Card.Title}”: {p.Plan.Refusal}").ToList();
        if (offline > 0)
            refused.Add($"{Format.Count(offline, "title is", "titles are")} in an offline folder");
        if (ok.Count == 0)
            return ("Nothing deleted · " + string.Join(" · ", refused), false);

        string message;
        if (ok.Count == 1)
        {
            var (card, plan) = ok[0];
            message = plan.Folder is not null
                ? $"Delete “{card.Title}”?\n\nThe folder and everything in it go from the disk: "
                  + $"{Format.Count(plan.FileCount, "file", "files")}, {Format.Size(plan.Size)}.\n{plan.Where}"
                : $"Delete “{card.Title}”?\n\n{Format.Count(plan.FileCount, "file goes", "files go")} from the disk, {Format.Size(plan.Size)}:\n"
                  + FileList(plan.Files);
        }
        else
        {
            var folders = ok.Count(p => p.Plan.Folder is not null);
            var what = folders == ok.Count ? "Their folders and everything in them go"
                : folders == 0 ? "Their videos and the files named like them go"
                : "Their folders (or, for loose files, the video and the files named like it) go";
            message = $"Delete {Format.Count(ok.Count, "title", "titles")}?\n\n{what} from the disk: "
                      + $"{Format.Count(ok.Sum(p => p.Plan.FileCount), "file", "files")}, {Format.Size(ok.Sum(p => p.Plan.Size))}.";
        }
        var unknown = ok.SelectMany(p => (p.Plan.UnknownVideos ?? []).Select(v => ok.Count == 1 ? v : $"{p.Card.Title}: {v}")).ToList();
        if (unknown.Count > 0)
            message += $"\n\nWatch out: {Format.Count(unknown.Count, "video that isn't part of it goes", "videos that aren't part of it go")} too:\n"
                       + string.Join("\n", unknown.Take(6)) + (unknown.Count > 6 ? $"\nand {unknown.Count - 6} more" : "");
        message += "\n\nThis can't be undone: the files don't go to the Recycle Bin.";
        if (refused.Count > 0)
            message += "\n\nLeft out: " + string.Join("; ", refused.Take(3)) + (refused.Count > 3 ? $"; and {refused.Count - 3} more" : "") + ".";
        ScanStatus = "Delete: waiting for your answer";
        if (!await Dialogs!.ConfirmAsync("Delete", message, "Delete", destructive: true))
            return (null, false);

        int deleted = 0;
        var touched = false;
        var problems = new List<string>(refused);
        for (var n = 0; n < ok.Count; n++)
        {
            var card = ok[n].Card;
            ScanStatus = ok.Count == 1 ? $"Deleting {card.Title}…" : $"Deleting {card.Title} ({n + 1} of {ok.Count})…";
            var root = _roots[card.RootId];
            Core.Moving.DeleteResult result;
            try
            {
                result = card switch
                {
                    MovieCardViewModel m => await _s.Deleter.DeleteMovieAsync(m.Movie, root),
                    SeriesCardViewModel s => await _s.Deleter.DeleteSeriesAsync(s.Series, root),
                    _ => new Core.Moving.DeleteResult(false),
                };
            }
            catch (Exception e)
            {
                result = new Core.Moving.DeleteResult(false, e.Message, Partly: true);
            }
            if (result.Deleted)
            {
                deleted++;
                gone.Add(card);
            }
            else
            {
                problems.Add(result.Partly ? $"“{card.Title}” only partly deleted: {result.Error}" : $"“{card.Title}” not deleted: {result.Error}");
            }
            touched |= result.Deleted || result.Partly;
        }

        var text = ok.Count == 1 && deleted == 1 ? $"“{ok[0].Card.Title}” deleted" : $"{Format.Count(deleted, "title", "titles")} deleted";
        if (problems.Count > 0)
            text += " · " + string.Join(" · ", problems.Take(3)) + (problems.Count > 3 ? $" · and {problems.Count - 3} more" : "");
        return (text, touched);
    }

    private static string FileList(IReadOnlyList<string> files) =>
        string.Join("\n", files.Take(8).Select(Path.GetFileName)) + (files.Count > 8 ? $"\nand {files.Count - 8} more" : "");

    /// <summary>Deletes one episode's video and the files named like it; the show is read again.</summary>
    public async Task DeleteEpisodeAsync(SeriesCardViewModel card, Episode episode)
    {
        if (Dialogs is null || IsBusy("delete"))
            return;
        if (_unavailable.Contains(card.RootId) || !_roots.TryGetValue(card.RootId, out var root))
        {
            ShowStatusFor("The library folder is offline: nothing can be deleted.", 12);
            return;
        }
        var name = $"{card.Title} {episode.Code}".Trim() + (episode.Title.Length > 0 ? $" · {episode.Title}" : "");
        IsScanning = true;
        ScanIndeterminate = true;
        _statusVersion++;
        string? status = null;
        var touched = false;
        try
        {
            ScanStatus = "Looking at what would be deleted…";
            Core.Moving.DeletePlan plan;
            try
            {
                plan = await Task.Run(() => _s.Deleter.PlanEpisode(episode, root)).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                status = $"Can't read the files to delete: {e.Message}";
                return;
            }
            if (!plan.CanDelete)
            {
                status = $"“{name}” not deleted: {plan.Refusal}";
                return;
            }
            ScanStatus = "Delete: waiting for your answer";
            if (!await Dialogs.ConfirmAsync("Delete episode",
                    $"Delete “{name}”?\n\n{Format.Count(plan.FileCount, "file goes", "files go")} from the disk, {Format.Size(plan.Size)}:\n{FileList(plan.Files)}"
                    + "\n\nThis can't be undone: the files don't go to the Recycle Bin.", "Delete", destructive: true))
                return;

            ScanStatus = $"Deleting {name}…";
            Core.Moving.DeleteResult result;
            try
            {
                result = await _s.Deleter.DeleteEpisodeAsync(card.Series, episode, root);
            }
            catch (Exception e)
            {
                result = new Core.Moving.DeleteResult(false, e.Message, Partly: true);
            }
            touched = result.Deleted || result.Partly;
            status = result.Deleted ? $"“{name}” deleted"
                : result.Partly ? $"“{name}” only partly deleted: {result.Error}"
                : $"“{name}” not deleted: {result.Error}";
        }
        finally
        {
            IsScanning = false;
            ScanIndeterminate = false;
            ScanStatus = "";
            if (touched)
            {
                var showing = SeriesDetail?.Card == card;
                Reload();
                if (showing && _shows.Contains(card))
                    OpenSeries(card, episode.Season);
            }
            if (status is not null)
                ShowStatusFor(status, status.EndsWith(" deleted", StringComparison.Ordinal) ? 10 : 30);
        }
    }

    // ---- subtitles ----

    public bool HasOpenSubtitlesKey => _s.Settings.HasOpenSubtitlesKey;

    /// <param name="added">From the player: given the file saved, to show it at once.</param>
    public async Task FindSubtitlesAsync(MovieCardViewModel card, Action<string>? added = null)
    {
        if (Dialogs is null || !_roots.TryGetValue(card.RootId, out var root) || !CheckSubtitlesKey())
            return;
        var m = card.Movie;
        var query = new SubtitleQuery(ImdbId: m.ImdbId, TmdbId: m.TmdbId, Title: m.Title, Year: m.Year);
        var video = RelPath.ToFull(root.Path, m.VideoFile);
        using var vm = new SubtitlesViewModel(_s, Dialogs, m.Title, video, query, async name =>
        {
            added?.Invoke(Path.Combine(Path.GetDirectoryName(video)!, name));
            await Task.Run(() => _s.Scanner.RebuildAsync(root, [m], CancellationToken.None));
            var showing = Detail?.Card == card;
            Reload();
            if (showing)
                OpenDetail(card);
            ShowStatusFor($"Subtitle saved as {name}", 10);
        });
        await Dialogs.ShowSubtitlesAsync(vm);
    }

    public async Task FindSubtitlesAsync(SeriesCardViewModel card, Episode episode, Action<string>? added = null)
    {
        if (Dialogs is null || !_roots.TryGetValue(card.RootId, out var root) || !CheckSubtitlesKey())
            return;
        var s = card.Series;
        var query = EpisodeQuery(s, episode);
        var title = $"{s.Title} · {(episode.Code.Length > 0 ? episode.Code + " · " : "")}{episode.Title}";
        var video = RelPath.ToFull(root.Path, episode.VideoFile);
        using var vm = new SubtitlesViewModel(_s, Dialogs, title, video, query, async name =>
        {
            added?.Invoke(Path.Combine(Path.GetDirectoryName(video)!, name));
            await Task.Run(() => _s.SeriesScanner.RebuildAsync(root, [s], CancellationToken.None));
            var showing = SeriesDetail?.Card == card;
            Reload();
            if (showing)
                OpenSeries(card, episode.Season);
            ShowStatusFor($"Subtitle saved as {name}", 10);
        });
        await Dialogs.ShowSubtitlesAsync(vm);
    }

    /// <summary>By the show's ids with season and number, otherwise by the episode's own id and its name.</summary>
    private static SubtitleQuery EpisodeQuery(Series s, Episode episode) =>
        episode.Season is not null && episode.Number is not null && (s.ImdbId is not null || s.TmdbId is not null)
            ? new SubtitleQuery(ParentImdbId: s.ImdbId, ParentTmdbId: s.TmdbId, Season: episode.Season, Episode: episode.Number)
            : new SubtitleQuery(ImdbId: episode.ImdbId, Title: $"{s.Title} {episode.Code}".Trim());

    /// <summary>
    /// A subtitle for each episode of a season that has none in the languages of the settings:
    /// says how many first, and each one counts against the daily quota.
    /// </summary>
    public async Task SeasonSubtitlesAsync(SeriesCardViewModel card, IReadOnlyList<Episode> episodes, string seasonName)
    {
        if (Dialogs is null || IsBusy("download subtitles") || !_roots.TryGetValue(card.RootId, out var root) || !CheckSubtitlesKey())
            return;
        if (_unavailable.Contains(root.Id))
        {
            ShowStatusFor($"{root.Name} is not reachable right now.", 10);
            return;
        }
        if (string.IsNullOrEmpty(_s.Settings.OpenSubtitlesUser))
        {
            ShowStatusFor("Downloads need your OpenSubtitles user name and password: add them in Settings.", 14);
            return;
        }
        var languages = SubtitleFiles.ParseLanguages(_s.Settings.SubtitleLanguages);
        var languageText = languages.Count > 0 ? string.Join(", ", languages.Select(Format.Language)) : "any language";
        var show = card.Series;

        BeginBusy($"Looking at the subtitles of {seasonName}…");
        List<SubtitleJob> jobs;
        try
        {
            jobs = await Task.Run(() => episodes.OrderBy(e => e.Number ?? int.MaxValue)
                .Select(e => new SubtitleJob(RelPath.ToFull(root.Path, e.VideoFile), e.Code.Length > 0 ? e.Code : Path.GetFileName(e.VideoFile),
                                             EpisodeQuery(show, e), Movie.Split(e.Subtitles)))
                .Where(j => SeasonSubtitles.Lacks(j, languages))
                .ToList());
        }
        finally
        {
            IsScanning = false;
            ScanStatus = "";
        }
        if (jobs.Count == 0)
        {
            ShowStatusFor($"Every episode of {seasonName} already has subtitles in {languageText}.", 10);
            return;
        }
        var question = $"{Format.Count(jobs.Count, "episode", "episodes")} of {episodes.Count:N0} in {seasonName} of {show.Title} "
                       + $"{(jobs.Count == 1 ? "has" : "have")} no subtitles in {languageText}.\n\n"
                       + "Kinora picks the best one for each (made for that file when there is one) and saves it next to the video. "
                       + "Each download counts against your OpenSubtitles daily quota.";
        if (!await Dialogs.ConfirmAsync("Subtitles for the season", question, "Download"))
            return;

        BeginBusy("Logging in to OpenSubtitles…");
        _scanCts = new CancellationTokenSource();
        SeasonSubtitlesResult? result = null;
        string? error = null;
        using var client = new OpenSubtitlesClient(_s.Settings.OpenSubtitlesKey);
        try
        {
            var token = _scanCts.Token;
            var account = await client.LoginAsync(_s.Settings.OpenSubtitlesUser, _s.Settings.OpenSubtitlesPassword, token);
            if (account.RemainingDownloads == 0)
            {
                error = "No downloads left today on OpenSubtitles.";
            }
            else
            {
                ScanIndeterminate = false;
                var progress = new Progress<(int Done, int Total, string Label)>(p =>
                {
                    ScanProgress = 100.0 * p.Done / p.Total;
                    ScanStatus = $"Subtitles  {Math.Min(p.Done + 1, p.Total):N0} of {p.Total:N0}  {p.Label}";
                });
                var batch = new SeasonSubtitles(client);
                result = await Task.Run(() => batch.RunAsync(jobs, languages, _s.Settings.SkipMachineTranslated, null, progress, token));
            }
        }
        catch (OperationCanceledException) when (_scanCts.IsCancellationRequested)
        {
            error = "Subtitle download cancelled.";
        }
        catch (Exception e)
        {
            // Whatever it was (a proxy's page instead of OpenSubtitles' answer...), said, and Kinora goes on
            error = e.Message;
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
        }

        // What was saved before a cancel or an error is there: the show is read again in any case
        try
        {
            ScanStatus = "Reading the folder again…";
            await Task.Run(() => _s.SeriesScanner.RebuildAsync(root, [show], CancellationToken.None));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            error ??= $"The folder could not be read again: {e.Message}";
        }
        finally
        {
            IsScanning = false;
        }
        var showing = SeriesDetail?.Card == card;
        var season = SeriesDetail?.SelectedSeason?.Number;
        Reload();
        if (showing && _shows.Contains(card))
            OpenSeries(card, season);

        string text;
        if (result is null)
        {
            text = error ?? "";
        }
        else
        {
            var parts = new List<string> { $"{Format.Count(result.Saved, "subtitle saved", "subtitles saved")}" };
            if (result.NotFound > 0)
                parts.Add($"{result.NotFound:N0} not found in {languageText}");
            if (result.Failed > 0)
                parts.Add($"{result.Failed:N0} failed");
            if (result.Remaining >= 0)
                parts.Add($"{result.Remaining:N0} downloads left today");
            text = $"{seasonName}: {string.Join(", ", parts)}" + (result.Stopped is { } why ? $" · stopped: {why}" : "");
        }
        if (text.Length > 0)
            ShowStatusFor(text, 20);
    }

    private bool CheckSubtitlesKey()
    {
        if (_s.Settings.HasOpenSubtitlesKey)
            return true;
        ShowStatusFor("Add an OpenSubtitles API key in Settings first.", 12);
        return false;
    }

    // ---- watched ----

    public void SetWatched(MovieCardViewModel card, bool watched)
    {
        _s.Database.SetWatched(card.Movie, watched);
        card.NotifyWatchedChanged();
        Detail?.NotifyWatchedChanged();
        AfterWatchedChange();
        QueueSync([SyncTitle.Of(card.Movie)]);
    }

    public void SetSeriesWatched(SeriesCardViewModel card, bool watched)
    {
        var episodes = _s.Database.GetEpisodes(card.Series.Id);
        SetEpisodesWatched(card, episodes, watched);
    }

    public void SetEpisodesWatched(SeriesCardViewModel card, IReadOnlyCollection<Episode> episodes, bool watched)
    {
        _s.Database.SetEpisodesWatched(card.Series, episodes, watched);
        QueueSync(episodes.Select(e => SyncTitle.Of(card.Series, e)));
        var all = _s.Database.GetEpisodes(card.Series.Id);
        card.SetWatchedCount(all.Count(e => e.Watched));
        if (!watched && all.All(e => !e.Watched))
            ForgetProgress(card);
        if (SeriesDetail?.Card == card)
            SeriesDetail.EpisodesChanged(all);
        AfterWatchedChange();
    }

    private void AfterWatchedChange()
    {
        var unwatched = NavItems.FirstOrDefault(n => n.Kind == NavKind.Unwatched);
        if (unwatched is not null)
            unwatched.Count = CurrentCards.Count(c => !c.Watched);
        if (SelectedNav?.Kind == NavKind.Unwatched)
            ApplyFilter();
        AfterProgressChange();
    }

    /// <summary>File.Exists on a sleeping or vanished NAS can hang: give it a deadline.</summary>
    private static async Task<bool> ExistsAsync(string path)
    {
        try
        {
            return await Task.Run(() => File.Exists(ExactPath.ForIo(path))).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ---- settings ----

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Dialogs is null)
            return;
        var settings = new SettingsViewModel(_s, Dialogs, CheckForUpdatesAsync);
        var cardWidth = _s.Settings.CardWidth;
        await Dialogs.ShowSettingsAsync(settings);
        _s.SaveSettings();
        OnPropertyChanged(nameof(HasMediaServer));
        OnPropertyChanged(nameof(SyncMenuText));
        if (Math.Abs(cardWidth - _s.Settings.CardWidth) > 0.5)
            LayoutRows();
        OnPropertyChanged(nameof(HasTmdbKey));
        if (settings.RootsChanged)
        {
            Reload();
            await RunScanAsync(full: false);
        }
        else if (settings.OnlineChanged && _s.Settings.HasTmdbKey && _s.Settings.FetchAfterScan)
        {
            await RunFetchAsync(retryNotFound: true, asked: false);
        }
    }
}
