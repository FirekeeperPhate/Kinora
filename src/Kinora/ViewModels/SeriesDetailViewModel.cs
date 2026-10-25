using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Services;

namespace Kinora.ViewModels;

public partial class SeasonTab : ObservableObject
{
    public int? Number { get; init; }
    public string Label => Number switch { null => "Other", 0 => "Specials", { } n => $"Season {n}" };
    public string? PosterFile { get; init; }
    public string? OnlinePoster { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int WatchedCount { get; set; }

    public string CountText => Count == 0 ? ""
        : WatchedCount >= Count ? $"✓ {Count}"
        : WatchedCount == 0 ? Count.ToString(CultureInfo.InvariantCulture)
        : $"{Count - WatchedCount} of {Count} left";
}

/// <summary>One line of the episode list: an episode on disk, or one TMDB knows that isn't there.</summary>
public partial class EpisodeRow : ObservableObject
{
    private readonly SeriesDetailViewModel _page;

    public EpisodeRow(SeriesDetailViewModel page, Episode? episode, TmdbEpisode? missing, int? season)
    {
        _page = page;
        Episode = episode;
        Missing = missing;
        Season = season;
    }

    public Episode? Episode { get; }
    public TmdbEpisode? Missing { get; }
    public int? Season { get; }
    public bool IsMissing => Episode is null;

    public string NumberText => Episode is { Number: { } n } e
        ? e.NumberEnd is { } end && end != n ? $"{n}-{end}" : n.ToString(CultureInfo.InvariantCulture)
        : Missing?.Number.ToString(CultureInfo.InvariantCulture) ?? "•";

    public string Title => Episode?.Title ?? Missing?.Name ?? "";
    public string? Plot => Episode?.Plot ?? Missing?.Overview;

    public string Details
    {
        get
        {
            var parts = new List<string>();
            var aired = Episode?.Aired ?? Missing?.AirDate;
            if (DateTime.TryParse(aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                parts.Add(date.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
            if ((Episode?.Runtime ?? Missing?.Runtime) is { } minutes)
                parts.Add(Format.Runtime(minutes));
            if (Episode?.Resolution is { } res)
                parts.Add(res);
            if (IsMissing)
                parts.Add(date > DateTime.Today ? "not aired yet" : "not in the folder");
            return string.Join("  ·  ", parts);
        }
    }

    public bool Watched => Episode?.Watched ?? false;

    [ObservableProperty]
    public partial Bitmap? Thumb { get; set; }

    /// <summary>How much of it the built-in player has shown, 0-1, while it is kept to resume; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarted), nameof(StartedText))]
    public partial double? Started { get; set; }

    /// <summary>Begun and not finished: a bar under its picture.</summary>
    public bool IsStarted => Started is not null && !Watched;

    public string StartedText => Started is { } f ? $"Started: {f:P0} seen" : "";

    public void NotifyWatched()
    {
        OnPropertyChanged(nameof(Watched));
        OnPropertyChanged(nameof(IsStarted));
    }

    [RelayCommand]
    private void Play()
    {
        if (Episode is not null)
            _page.Play(Episode);
    }

    [RelayCommand]
    private void ToggleWatched()
    {
        if (Episode is not null)
            _page.SetWatched([Episode], !Episode.Watched);
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Episode is not null)
            _page.Reveal(Episode);
    }

    [RelayCommand]
    private Task FindSubtitles() => Episode is null ? Task.CompletedTask : _page.FindSubtitles(Episode);

    public bool CanFindSubtitles => Episode is not null && _page.HasOpenSubtitlesKey;

    [RelayCommand]
    private Task Delete() => Episode is null ? Task.CompletedTask : _page.DeleteEpisode(Episode);
}

/// <summary>The page of a show: what it is, its seasons, and the episodes of the chosen season.</summary>
public partial class SeriesDetailViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly AppServices _s;
    private readonly CancellationTokenSource _photos = new();
    private readonly LibraryRoot? _root;
    private readonly bool _rootUnavailable;
    private readonly Dictionary<int, TmdbSeason> _tmdbSeasons;
    private List<Episode> _episodes;
    private CancellationTokenSource _thumbs = new();
    private bool _disposed;

    public SeriesDetailViewModel(MainWindowViewModel owner, AppServices services, SeriesCardViewModel card, LibraryRoot? root, bool rootUnavailable,
                                 int? season = null)
    {
        _owner = owner;
        _s = services;
        _root = root;
        _rootUnavailable = rootUnavailable;
        Card = card;
        Cast = _s.Database.GetSeriesCast(card.Series.Id).Select(c => new CastItem(c)).ToList();
        var folder = root is not null && !rootUnavailable ? RelPath.ToFull(root.Path, card.Series.Folder) : null;
        _ = CastItem.LoadPhotosAsync(_s, Cast, series: true, card.Series.TmdbId, folder, _photos.Token);
        _episodes = _s.Database.GetEpisodes(card.Series.Id);
        _tmdbSeasons = card.Series.TmdbId is { } id ? _s.Database.GetTmdbSeasons(id).ToDictionary(x => x.Number) : [];
        CanIdentify = card.Series.NeedsMatch || (root is not null && _s.Database.GetMatch(root.Id, card.Series.Folder) is not null);

        var seasons = _s.Database.GetSeasons(card.Series.Id).ToDictionary(x => x.Number);
        foreach (var group in _episodes.GroupBy(e => e.Season).OrderBy(g => g.Key is 0 ? 9999 : g.Key ?? 10000))
        {
            seasons.TryGetValue(group.Key ?? -1, out var seasonInfo);
            Seasons.Add(new SeasonTab
            {
                Number = group.Key,
                PosterFile = seasonInfo?.PosterFile,
                OnlinePoster = seasonInfo?.OnlinePoster,
                Count = group.Count(),
                WatchedCount = group.Count(e => e.Watched),
            });
        }
        var next = MainWindowViewModel.NextEpisode(_episodes);
        SelectedSeason = Seasons.FirstOrDefault(t => season is not null && t.Number == season)
                         ?? Seasons.FirstOrDefault(t => t.Number == next?.Season) ?? Seasons.FirstOrDefault();
        _ = LoadImagesAsync();
    }

    public SeriesCardViewModel Card { get; }
    private Series Series => Card.Series;

    public string Title => Series.Title;
    public string? OriginalTitle => !string.IsNullOrEmpty(Series.OriginalTitle) && Series.OriginalTitle != Series.Title ? Series.OriginalTitle : null;
    public string Plot => Series.Plot ?? "No plot available.";
    public string? YearsText => Card.Subtitle.Split("  ·  ")[0] is { Length: > 0 } y && Series.Year is not null ? y : null;
    public string? Mpaa => Series.Mpaa;
    public string? Status => Series.Status;
    public string? RatingText => Series.Rating is { } r ? r.ToString("0.0", CultureInfo.InvariantCulture) : null;
    public IReadOnlyList<string> Genres => Series.GenreList;
    public string? Networks => Series.StudioList.Count > 0 ? string.Join(", ", Series.StudioList.Take(3)) : null;
    public string EpisodesText => Format.Count(_episodes.Count, "episode", "episodes") + (Series.WatchedCount > 0 ? $", {Series.WatchedCount:N0} watched" : "");
    public List<CastItem> Cast { get; }
    public bool HasCast => Cast.Count > 0;
    public bool IsUnavailable => _rootUnavailable;
    public bool HasTmdbKey => _owner.HasTmdbKey;
    public bool NeedsMatch => Series.NeedsMatch;
    public bool CanIdentify { get; }
    public bool CanIdentifyOnline => CanIdentify && HasTmdbKey;

    public ObservableCollection<SeasonTab> Seasons { get; } = [];
    public ObservableCollection<EpisodeRow> Episodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeasonWatchedText))]
    public partial SeasonTab? SelectedSeason { get; set; }

    public string SeasonWatchedText => SelectedSeason is { } t && t.WatchedCount >= t.Count ? "Mark season unwatched" : "Mark season watched";

    public string PlayNextText => MainWindowViewModel.NextEpisode(_episodes) is { } e
        ? e.Code.Length > 0 ? $"Play {e.Code}" : "Play"
        : "Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoster))]
    public partial Bitmap? Poster { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Backdrop { get; private set; }

    [ObservableProperty]
    public partial bool BackdropIsPoster { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo), nameof(ShowTitleText))]
    public partial Bitmap? Logo { get; private set; }

    public bool HasPoster => Poster is not null;
    public bool HasLogo => Logo is not null;
    public bool ShowTitleText => Logo is null;

    partial void OnSelectedSeasonChanged(SeasonTab? value) => ShowSeason(value);

    private void ShowSeason(SeasonTab? tab)
    {
        _thumbs.Cancel();
        _thumbs = new CancellationTokenSource();
        foreach (var row in Episodes)
            row.Thumb?.Dispose();
        Episodes.Clear();
        if (tab is null)
            return;

        var onDisk = _episodes.Where(e => e.Season == tab.Number).ToList();
        var rows = onDisk.Select(e => new EpisodeRow(this, e, null, tab.Number)).ToList();
        if (tab.Number is { } n && _tmdbSeasons.TryGetValue(n, out var tmdb))
        {
            var have = onDisk.SelectMany(e => e.Number is { } a ? Enumerable.Range(a, Math.Max(1, (e.NumberEnd ?? a) - a + 1)) : []).ToHashSet();
            rows.AddRange(tmdb.Episodes.Where(te => !have.Contains(te.Number)).Select(te => new EpisodeRow(this, null, te, n)));
        }
        var started = _s.Database.GetPlaybackPositions(Core.Data.PlaybackKind.Episode);
        foreach (var row in rows.OrderBy(r => r.Episode?.Number ?? r.Missing?.Number ?? int.MaxValue)
                                .ThenBy(r => r.Episode?.VideoFile, StringComparer.OrdinalIgnoreCase))
        {
            if (row.Episode is { } e)
                row.Started = Core.Playback.PlaybackRules.SeenFraction(started.GetValueOrDefault(e.Id));
            Episodes.Add(row);
        }
        _ = LoadThumbsAsync(Episodes.ToList(), _thumbs.Token);
    }

    /// <summary>
    /// Episode pictures: the folder's own (-thumb.jpg) or TMDB's still, resized into the local
    /// cache the first time, a few at a time.
    /// </summary>
    private async Task LoadThumbsAsync(List<EpisodeRow> rows, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var bitmap = await Task.Run(() => MakeThumb(row, ct), ct);
                if (bitmap is null || ct.IsCancellationRequested)
                {
                    bitmap?.Dispose();
                    return;
                }
                row.Thumb = bitmap;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ArgumentException or InvalidOperationException)
            {
            }
            finally
            {
                gate.Release();
            }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<Bitmap?> MakeThumb(EpisodeRow row, CancellationToken ct)
    {
        string? key = null;
        if (row.Episode?.ThumbFile is { } local && _root is not null && !_rootUnavailable)
        {
            var info = new FileInfo(RelPath.ToIo(_root.Path, local));
            if (info.Exists)
            {
                key = ImageCache.KeyFor(info.FullName, info.Length, info.LastWriteTimeUtc);
                if (!_s.Stills.Ensure(key, info.FullName))
                    key = null;
            }
        }
        var still = row.Episode?.OnlineStill ?? row.Missing?.StillPath;
        if (key is null && still is not null)
        {
            key = ImageCache.KeyForOnline(still);
            if (!_s.Stills.Has(key) && await TmdbImages.DownloadAsync(still, "w300", ct) is { } bytes)
                _s.Stills.EnsureFromBytes(key, bytes);
            if (!_s.Stills.Has(key))
                key = null;
        }
        if (key is null)
            return null;
        await using var stream = File.OpenRead(_s.Stills.PathFor(key));
        return Bitmap.DecodeToWidth(stream, 320);
    }

    private async Task LoadImagesAsync()
    {
        if (Series.ThumbKey is { } key)
            Poster = await Load(() => File.Exists(_s.Posters.PathFor(key)) ? new Bitmap(_s.Posters.PathFor(key)) : null);

        if (_root is not null && !_rootUnavailable)
        {
            if (Series.FanartFile is { } fanart)
            {
                var full = RelPath.ToIo(_root.Path, fanart);
                Backdrop = await Load(() =>
                {
                    var info = new FileInfo(full);
                    if (!info.Exists)
                        return null;
                    var cacheKey = ImageCache.KeyFor(full, info.Length, info.LastWriteTimeUtc);
                    return _s.Backdrops.Ensure(cacheKey, full) ? new Bitmap(_s.Backdrops.PathFor(cacheKey)) : null;
                });
            }
            if (Series.LogoFile is { } logo)
            {
                var full = RelPath.ToIo(_root.Path, logo);
                Logo = await Load(() =>
                {
                    if (!File.Exists(full))
                        return null;
                    using var stream = File.OpenRead(full);
                    return Bitmap.DecodeToWidth(stream, 800);
                });
            }
        }
        if (Backdrop is null && Series.OnlineFanart is { } onlineFanart)
        {
            var fanartKey = ImageCache.KeyForOnline(onlineFanart);
            if (!_s.Backdrops.Has(fanartKey) && await TmdbImages.DownloadAsync(onlineFanart, "w1280", CancellationToken.None) is { } bytes)
                await Task.Run(() => _s.Backdrops.EnsureFromBytes(fanartKey, bytes));
            Backdrop = await Load(() => _s.Backdrops.Has(fanartKey) ? new Bitmap(_s.Backdrops.PathFor(fanartKey)) : null);
        }
        if (Logo is null && Series.OnlineLogo is { } onlineLogo)
        {
            var logoKey = ImageCache.KeyForOnline(onlineLogo);
            if (!_s.Logos.Has(logoKey) && await TmdbImages.DownloadAsync(onlineLogo, "w500", CancellationToken.None) is { } bytes)
                _s.Logos.Save(logoKey, bytes);
            Logo = await Load(() =>
            {
                if (!_s.Logos.Has(logoKey))
                    return null;
                using var stream = File.OpenRead(_s.Logos.PathFor(logoKey));
                return Bitmap.DecodeToWidth(stream, 800);
            });
        }
        if (Backdrop is null && Poster is not null && !_disposed)
        {
            BackdropIsPoster = true;
            Backdrop = Poster;
        }
    }

    private async Task<Bitmap?> Load(Func<Bitmap?> load)
    {
        if (_disposed)
            return null;
        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(load).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception e) when (e is IOException or TimeoutException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
        if (_disposed)
        {
            bitmap?.Dispose();
            return null;
        }
        return bitmap;
    }

    // ---- actions ----

    public void Play(Episode e) => _owner.PlayEpisode(Card, e);
    public void Reveal(Episode e) => _owner.RevealEpisode(Card, e);
    public Task FindSubtitles(Episode e) => _owner.FindSubtitlesAsync(Card, e);
    public Task DeleteEpisode(Episode e) => _owner.DeleteEpisodeAsync(Card, e);

    /// <summary>After the player: the episode's bar follows where it stopped.</summary>
    public void SetStarted(long episodeId, double? seen)
    {
        if (Episodes.FirstOrDefault(r => r.Episode?.Id == episodeId) is { } row)
            row.Started = seen;
    }
    public bool HasOpenSubtitlesKey => _owner.HasOpenSubtitlesKey;
    public void SetWatched(IReadOnlyCollection<Episode> episodes, bool watched) => _owner.SetEpisodesWatched(Card, episodes, watched);

    /// <summary>Called back after episodes changed state, with the fresh list.</summary>
    public void EpisodesChanged(List<Episode> all)
    {
        var byId = all.ToDictionary(e => e.Id);
        foreach (var e in _episodes)
            if (byId.TryGetValue(e.Id, out var fresh))
                e.Watched = fresh.Watched;
        foreach (var tab in Seasons)
            tab.WatchedCount = _episodes.Count(e => e.Season == tab.Number && e.Watched);
        foreach (var row in Episodes)
            row.NotifyWatched();
        OnPropertyChanged(nameof(SeasonWatchedText));
        OnPropertyChanged(nameof(PlayNextText));
        OnPropertyChanged(nameof(EpisodesText));
    }

    [RelayCommand]
    private void PlayNext() => _owner.PlayNextEpisode(Card);

    [RelayCommand]
    private void ToggleSeasonWatched()
    {
        if (SelectedSeason is not { } tab)
            return;
        var episodes = _episodes.Where(e => e.Season == tab.Number).ToList();
        SetWatched(episodes, !(tab.WatchedCount >= tab.Count));
    }

    [RelayCommand]
    private void RevealShow() => _owner.Reveal(Card);

    [RelayCommand]
    private Task Identify() => _owner.IdentifyAsync(Card);

    [RelayCommand]
    private Task Delete() => _owner.DeleteAsync(Card);

    [RelayCommand]
    private Task RefreshMetadata() => _owner.RefreshMetadataAsync(Card);

    /// <summary>Opened by Random: the page offers another pick.</summary>
    public bool FromRandom { get; init; }

    [RelayCommand]
    private void AnotherRandom() => _owner.RandomCommand.Execute(null);

    [RelayCommand]
    private void OpenPerson(CastItem actor) => _owner.OpenPerson(actor.Name, actor.Member.TmdbId, director: false);

    [RelayCommand]
    private void OpenDirector(string name) => _owner.OpenPerson(name, null, director: true);

    [RelayCommand]
    private void Back() => _owner.CloseDetailCommand.Execute(null);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _thumbs.Cancel();
        _photos.Cancel();
        foreach (var actor in Cast)
            actor.Release();
        foreach (var row in Episodes)
            row.Thumb?.Dispose();
        if (!ReferenceEquals(Backdrop, Poster))
            Backdrop?.Dispose();
        Poster?.Dispose();
        Logo?.Dispose();
    }
}
