using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Platform;
using Kinora.Core.Scanning;
using Kinora.Core.Settings;
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

    public bool ShowLibrary => Detail is null && SeriesDetail is null;

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
        _s.Settings.ShowTvShows = value == LibraryMode.Shows;
        SearchText = "";
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
        if (_s.Settings.ScanOnStartup && _roots.Count > 0)
            _ = RunScanAsync(full: false);
    }

    private void Reload()
    {
        _roots = _s.Database.GetRoots().ToDictionary(r => r.Id);

        var movies = _s.Database.GetMovies().Where(m => _roots.ContainsKey(m.RootId)).ToList();
        var cast = _s.Database.GetCastNames();
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
            _movies.Add(card);
        }

        var shows = _s.Database.GetSeries().Where(s => _roots.ContainsKey(s.RootId)).ToList();
        var showCast = _s.Database.GetSeriesCastNames();
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
            _shows.Add(card);
        }

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
        NavItems.Add(new NavItem(NavKind.Unwatched, "Unwatched", Icons.Eye) { Count = cards.Count(c => !c.Watched) });
        NavItems.Add(new NavItem(NavKind.RecentlyAdded, "Recently added", Icons.Clock) { Count = Math.Min(RecentCount, cards.Count) });
        var missing = cards.Count(c => c.NeedsMatch);
        if (missing > 0)
            NavItems.Add(new NavItem(NavKind.NeedsMetadata, "Not identified", Icons.Question) { Count = missing });

        var roots = _roots.Values.Where(r => r.Kind == CurrentKind).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (roots.Count > 0)
        {
            NavItems.Add(new NavItem(NavKind.Header, "FOLDERS"));
            foreach (var root in roots)
                NavItems.Add(new NavItem(NavKind.Root, root.Name, Icons.Folder)
                {
                    RootId = root.Id,
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
            NavKind.Unwatched => cards.Where(c => !c.Watched),
            NavKind.RecentlyAdded => cards.OrderByDescending(c => c.DateAdded).Take(RecentCount),
            NavKind.NeedsMetadata => cards.Where(c => c.NeedsMatch),
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
        var items = InView(nav).Where(c => Core.SearchText.Matches(c.SearchKey, query));

        IEnumerable<CardViewModel> sorted;
        if (nav.Kind == NavKind.RecentlyAdded)
        {
            sorted = items.OrderByDescending(c => c.DateAdded);
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
        try
        {
            foreach (var root in _roots.Values.OrderBy(r => r.Kind))
            {
                var result = root.Kind == LibraryKind.Movies
                    ? await Task.Run(() => _s.Scanner.ScanAsync(root, full, progress, _scanCts.Token))
                    : await Task.Run(() => _s.SeriesScanner.ScanAsync(root, full, progress, _scanCts.Token));
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
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }

        var changes = added + updated + removed == 0
            ? "no changes"
            : string.Join(", ", new[] { (added, "added"), (updated, "updated"), (removed, "removed") }
                                    .Where(x => x.Item1 > 0).Select(x => $"{x.Item1:N0} {x.Item2}"));
        var text = cancelled ? "Scan cancelled" : $"Library scanned in {watch.Elapsed.TotalSeconds:0.0} s · {changes}";
        if (notes.Count > 0)
            text += " · " + string.Join(" · ", notes);
        ShowStatusFor(text, notes.Count > 0 ? 20 : 8);

        if (!cancelled && _s.Settings.FetchAfterScan && _s.Settings.HasTmdbKey)
            await RunFetchAsync(retryNotFound: false, asked: false);
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

    public void OpenDetail(MovieCardViewModel card)
    {
        var old = Detail;
        _roots.TryGetValue(card.RootId, out var root);
        Detail = new MovieDetailViewModel(this, _s, card, root, _unavailable.Contains(card.RootId));
        old?.Dispose();
    }

    public void OpenSeries(SeriesCardViewModel card)
    {
        var old = SeriesDetail;
        _roots.TryGetValue(card.RootId, out var root);
        SeriesDetail = new SeriesDetailViewModel(this, _s, card, root, _unavailable.Contains(card.RootId));
        old?.Dispose();
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

    // ---- playing and folders ----

    public void Play(MovieCardViewModel card, string? videoFile) =>
        PlayFile(card.RootId, videoFile ?? card.Movie.VideoFile);

    public void PlayEpisode(SeriesCardViewModel card, Episode episode) => PlayFile(card.RootId, episode.VideoFile);

    /// <summary>The first episode not seen yet, in order; the first one if all are seen.</summary>
    public void PlayNextEpisode(SeriesCardViewModel card)
    {
        var next = NextEpisode(_s.Database.GetEpisodes(card.Series.Id));
        if (next is not null)
            PlayEpisode(card, next);
    }

    public static Episode? NextEpisode(IEnumerable<Episode> episodes)
    {
        var ordered = episodes.OrderBy(e => e.Season is 0 or null ? int.MaxValue : e.Season)
                              .ThenBy(e => e.Number ?? int.MaxValue).ThenBy(e => e.VideoFile, StringComparer.OrdinalIgnoreCase)
                              .ToList();
        return ordered.FirstOrDefault(e => !e.Watched && e.Season is not 0) ?? ordered.FirstOrDefault();
    }

    private async void PlayFile(long rootId, string relative)
    {
        if (!_roots.TryGetValue(rootId, out var root))
            return;
        var path = RelPath.ToFull(root.Path, relative);
        if (!await ExistsAsync(path))
        {
            ShowStatusFor($"Can't find {path}", 12);
            return;
        }
        try
        {
            Launcher.Open(path);
        }
        catch (Exception e)
        {
            ShowStatusFor($"Can't open the video: {e.Message}", 12);
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

    // ---- watched ----

    public void SetWatched(MovieCardViewModel card, bool watched)
    {
        _s.Database.SetWatched(card.Movie, watched);
        card.NotifyWatchedChanged();
        Detail?.NotifyWatchedChanged();
        AfterWatchedChange();
    }

    public void SetSeriesWatched(SeriesCardViewModel card, bool watched)
    {
        var episodes = _s.Database.GetEpisodes(card.Series.Id);
        SetEpisodesWatched(card, episodes, watched);
    }

    public void SetEpisodesWatched(SeriesCardViewModel card, IReadOnlyCollection<Episode> episodes, bool watched)
    {
        _s.Database.SetEpisodesWatched(card.Series, episodes, watched);
        var all = _s.Database.GetEpisodes(card.Series.Id);
        card.SetWatchedCount(all.Count(e => e.Watched));
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
    }

    /// <summary>File.Exists on a sleeping or vanished NAS can hang: give it a deadline.</summary>
    private static async Task<bool> ExistsAsync(string path)
    {
        try
        {
            return await Task.Run(() => File.Exists(path)).WaitAsync(TimeSpan.FromSeconds(15));
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
        var settings = new SettingsViewModel(_s, Dialogs);
        var cardWidth = _s.Settings.CardWidth;
        await Dialogs.ShowSettingsAsync(settings);
        _s.SaveSettings();
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
