using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Core.Platform;
using Kinora.Core.Scanning;
using Kinora.Core.Settings;
using Kinora.Services;

namespace Kinora.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public const double Gap = 22;
    private const int RecentCount = 100;

    private readonly AppServices _s;
    private readonly List<MovieCardViewModel> _cards = [];
    private readonly HashSet<long> _unavailable = [];
    private Dictionary<long, LibraryRoot> _roots = [];
    private List<MovieCardViewModel> _filtered = [];
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
        _suppressFilter = false;
    }

    public AppServices Services => _s;
    public IDialogs? Dialogs { get; set; }
    public ObservableCollection<NavItem> NavItems { get; } = [];
    public IReadOnlyList<SortOption> SortOptions { get; }

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
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
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
    public partial MovieDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLibrary))]
    public partial bool HasRoots { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLibrary))]
    public partial bool HasMovies { get; private set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; private set; }

    public bool ShowEmptyLibrary => !HasRoots || (!HasMovies && !IsScanning);

    public string EmptyLibraryText => HasRoots
        ? "No movies found yet. Run a scan, or check the folders in Settings."
        : "Add the folders where your movies are, and Kinora builds the library from them.";

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

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyLibrary));

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
        var existing = _cards.ToDictionary(c => c.Movie.Id);
        _cards.Clear();
        foreach (var movie in movies)
        {
            var names = cast.GetValueOrDefault(movie.Id, "");
            if (existing.TryGetValue(movie.Id, out var card))
                card.Update(movie, names);
            else
                card = new MovieCardViewModel(this, _s.PosterLoader, movie, names);
            card.IsUnavailable = _unavailable.Contains(movie.RootId);
            _cards.Add(card);
        }
        HasRoots = _roots.Count > 0;
        HasMovies = _cards.Count > 0;
        OnPropertyChanged(nameof(EmptyLibraryText));
        RebuildNav();
    }

    private void RebuildNav()
    {
        var previous = SelectedNav;
        _suppressFilter = true;
        NavItems.Clear();

        NavItems.Add(new NavItem(NavKind.AllMovies, "All movies", Icons.Film) { Count = _cards.Count });
        NavItems.Add(new NavItem(NavKind.Unwatched, "Unwatched", Icons.Eye) { Count = _cards.Count(c => !c.Movie.Watched) });
        NavItems.Add(new NavItem(NavKind.RecentlyAdded, "Recently added", Icons.Clock) { Count = Math.Min(RecentCount, _cards.Count) });
        var missing = _cards.Count(c => c.Movie.NeedsMatch);
        if (missing > 0)
            NavItems.Add(new NavItem(NavKind.NeedsMetadata, "Not identified", Icons.Question) { Count = missing });

        if (_roots.Count > 0)
        {
            NavItems.Add(new NavItem(NavKind.Header, "FOLDERS"));
            foreach (var root in _roots.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
                NavItems.Add(new NavItem(NavKind.Root, root.Name, Icons.Folder)
                {
                    RootId = root.Id,
                    Count = _cards.Count(c => c.Movie.RootId == root.Id),
                    IsUnavailable = _unavailable.Contains(root.Id),
                });
        }

        var genres = _cards.SelectMany(c => c.Movie.GenreList)
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

    private IEnumerable<MovieCardViewModel> InView(NavItem nav) => nav.Kind switch
    {
        NavKind.Unwatched => _cards.Where(c => !c.Movie.Watched),
        NavKind.RecentlyAdded => _cards.OrderByDescending(c => c.Movie.DateAdded).Take(RecentCount),
        NavKind.NeedsMetadata => _cards.Where(c => c.Movie.NeedsMatch),
        NavKind.Root => _cards.Where(c => c.Movie.RootId == nav.RootId),
        NavKind.Genre => _cards.Where(c => c.Movie.GenreList.Contains(nav.Genre!, StringComparer.CurrentCultureIgnoreCase)),
        _ => _cards,
    };

    private void ApplyFilter(bool toTop = false)
    {
        if (_suppressFilter)
            return;
        var nav = SelectedNav ?? NavItems.FirstOrDefault();
        if (nav is null)
            return;

        var query = Core.SearchText.Normalize(SearchText);
        var items = InView(nav).Where(c => Core.SearchText.Matches(c.SearchKey, query));

        IEnumerable<MovieCardViewModel> sorted;
        if (nav.Kind == NavKind.RecentlyAdded)
        {
            sorted = items.OrderByDescending(c => c.Movie.DateAdded);
        }
        else
        {
            // Each order has its natural direction (titles A→Z, the rest newest or best first);
            // the toggle reverses it.
            var title = items.OrderBy(c => c.SortKey, StringComparer.Ordinal);
            sorted = SelectedSort.Value switch
            {
                SortOrder.Year => SortDescending
                    ? items.OrderBy(c => c.Movie.Year ?? int.MaxValue).ThenBy(c => c.SortKey, StringComparer.Ordinal)
                    : items.OrderByDescending(c => c.Movie.Year ?? 0).ThenBy(c => c.SortKey, StringComparer.Ordinal),
                SortOrder.DateAdded => SortDescending
                    ? items.OrderBy(c => c.Movie.DateAdded)
                    : items.OrderByDescending(c => c.Movie.DateAdded),
                SortOrder.Rating => SortDescending
                    ? items.OrderBy(c => c.Movie.Rating ?? double.MaxValue).ThenBy(c => c.SortKey, StringComparer.Ordinal)
                    : items.OrderByDescending(c => c.Movie.Rating ?? 0).ThenBy(c => c.SortKey, StringComparer.Ordinal),
                _ => SortDescending ? items.OrderByDescending(c => c.SortKey, StringComparer.Ordinal) : title,
            };
        }

        _filtered = sorted.ToList();
        ViewTitle = nav.Title;
        CountText = query.Length > 0
            ? $"{Format.Count(_filtered.Count, "movie", "movies")} matching “{SearchText.Trim()}”"
            : Format.Count(_filtered.Count, "movie", "movies");
        HasNoResults = _filtered.Count == 0 && _cards.Count > 0;
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
        MovieCardViewModel.DecodeWidth = Math.Min(AppServices.PosterWidth, (int)Math.Ceiling(width * _scaling));
        foreach (var card in _filtered)
            card.CardWidth = width;
        Rows = _filtered.Chunk(columns).Select(chunk => new MovieRow(chunk)).ToList();
    }

    public void RefreshLayout() => LayoutRows();

    // ---- scanning ----

    [RelayCommand]
    private Task Scan() => RunScanAsync(full: false);

    [RelayCommand]
    private Task FullScan() => RunScanAsync(full: true);

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private async Task RunScanAsync(bool full)
    {
        if (IsScanning)
            return;
        var roots = _roots.Values.Where(r => r.Kind == LibraryKind.Movies).ToList();
        if (roots.Count == 0)
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
            foreach (var root in roots)
            {
                var result = await Task.Run(() => _s.Scanner.ScanAsync(root, full, progress, _scanCts.Token));
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
                var keys = _cards.Select(c => c.Movie.ThumbKey).OfType<string>().ToHashSet();
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
    }

    private async void ShowStatusFor(string text, int seconds)
    {
        var version = ++_statusVersion;
        ScanStatus = text;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (version == _statusVersion && !IsScanning)
            ScanStatus = "";
    }

    // ---- actions on movies ----

    public void OpenDetail(MovieCardViewModel card)
    {
        var old = Detail;
        _roots.TryGetValue(card.Movie.RootId, out var root);
        Detail = new MovieDetailViewModel(this, _s, card, root, _unavailable.Contains(card.Movie.RootId));
        old?.Dispose();
    }

    [RelayCommand]
    private void CloseDetail()
    {
        var old = Detail;
        Detail = null;
        old?.Dispose();
    }

    public async void Play(MovieCardViewModel card, string? videoFile)
    {
        if (!_roots.TryGetValue(card.Movie.RootId, out var root))
            return;
        var path = RelPath.ToFull(root.Path, videoFile ?? card.Movie.VideoFile);
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

    public async void Reveal(MovieCardViewModel card)
    {
        if (!_roots.TryGetValue(card.Movie.RootId, out var root))
            return;
        var path = RelPath.ToFull(root.Path, card.Movie.VideoFile);
        if (!await ExistsAsync(path))
            path = RelPath.ToFull(root.Path, card.Movie.Folder);
        try
        {
            Launcher.Reveal(path);
        }
        catch (Exception e)
        {
            ShowStatusFor($"Can't open the folder: {e.Message}", 12);
        }
    }

    public void SetWatched(MovieCardViewModel card, bool watched)
    {
        _s.Database.SetWatched(card.Movie, watched);
        card.NotifyWatchedChanged();
        Detail?.NotifyWatchedChanged();
        var unwatched = NavItems.FirstOrDefault(n => n.Kind == NavKind.Unwatched);
        if (unwatched is not null)
            unwatched.Count = _cards.Count(c => !c.Movie.Watched);
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
        if (settings.RootsChanged)
        {
            Reload();
            await RunScanAsync(full: false);
        }
    }
}
