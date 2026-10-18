using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Services;

namespace Kinora.ViewModels;

public sealed record VideoVersion(string Label, string File);

/// <summary>The page of one movie: backdrop, poster, everything the nfo said, and Play.</summary>
public partial class MovieDetailViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly AppServices _s;
    private readonly LibraryRoot? _root;
    private readonly bool _rootUnavailable;
    private bool _disposed;

    public MovieDetailViewModel(MainWindowViewModel owner, AppServices services, MovieCardViewModel card, LibraryRoot? root, bool rootUnavailable)
    {
        _owner = owner;
        _s = services;
        _root = root;
        _rootUnavailable = rootUnavailable;
        Card = card;
        Cast = _s.Database.GetCast(card.Movie.Id);
        CanIdentify = card.Movie.NeedsMatch || (root is not null && _s.Database.GetMatch(root.Id, card.Movie.VideoFile) is not null);

        var m = card.Movie;
        var all = new[] { m.VideoFile }.Concat(m.OtherVideoList).ToList();
        if (all.Count > 1)
            Versions = all.Select(f => new VideoVersion(Path.GetFileName(f), f)).ToList();

        _ = LoadImagesAsync();
    }

    public MovieCardViewModel Card { get; }
    private Movie Movie => Card.Movie;

    public string Title => Movie.Title;
    public string? OriginalTitle => !string.IsNullOrEmpty(Movie.OriginalTitle) && Movie.OriginalTitle != Movie.Title ? Movie.OriginalTitle : null;
    public string? Tagline => Movie.Tagline;
    public string Plot => Movie.Plot ?? "No plot available.";
    public string? YearText => Movie.Year?.ToString();
    public string? RuntimeText => Movie.Runtime is { } r ? Format.Runtime(r) : null;
    public string? Mpaa => Movie.Mpaa;
    public string? Resolution => Movie.Resolution;
    public string? RatingText => Movie.Rating is { } r ? r.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : null;
    public IReadOnlyList<string> Genres => Movie.GenreList;
    public string? Directors => Join(Movie.DirectorList);
    public IReadOnlyList<string> DirectorNames => Movie.DirectorList;
    public string? Studios => Join(Movie.StudioList.Take(4));
    public string? Countries => Join(Movie.CountryList);
    public string? Collection => Movie.Collection;
    public string? Subtitles => Join(Movie.SubtitleList.Select(Format.Language));
    public List<CastMember> Cast { get; }
    public bool HasCast => Cast.Count > 0;
    public IReadOnlyList<VideoVersion>? Versions { get; }
    public bool HasVersions => Versions is not null;

    public string FilePath => _root is null ? Movie.VideoFile : RelPath.ToFull(_root.Path, Movie.VideoFile);
    public string SizeText => Format.Size(Movie.VideoSize);

    public string? TechText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Movie.VideoCodec)) parts.Add(Movie.VideoCodec.ToUpperInvariant());
            if (!string.IsNullOrEmpty(Movie.AudioCodec))
                parts.Add(Movie.AudioCodec.ToUpperInvariant() + Channels(Movie.AudioChannels));
            parts.Add(SizeText);
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsUnavailable => _rootUnavailable;
    public bool Watched => Movie.Watched;
    public string WatchedText => Movie.Watched ? "Watched" : "Mark as watched";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoster))]
    public partial Bitmap? Poster { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Backdrop { get; private set; }

    /// <summary>No fanart: the poster, blurred, stands in for it.</summary>
    [ObservableProperty]
    public partial bool BackdropIsPoster { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo), nameof(ShowTitleText))]
    public partial Bitmap? Logo { get; private set; }

    public bool HasPoster => Poster is not null;
    public bool HasLogo => Logo is not null;
    public bool ShowTitleText => Logo is null;

    private async Task LoadImagesAsync()
    {
        if (Movie.ThumbKey is { } key)
            Poster = await Load(() => File.Exists(_s.Posters.PathFor(key)) ? new Bitmap(_s.Posters.PathFor(key)) : null);

        if (_root is not null && !_rootUnavailable)
        {
            if (Movie.FanartFile is { } fanart)
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
            if (Movie.LogoFile is { } logo)
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

        // Pictures the folder lacks, from TMDB (downloaded once, then from the cache)
        if (Backdrop is null && Movie.OnlineFanart is { } onlineFanart)
        {
            var fanartKey = ImageCache.KeyForOnline(onlineFanart);
            if (!_s.Backdrops.Has(fanartKey) && await TmdbImages.DownloadAsync(onlineFanart, "w1280", CancellationToken.None) is { } bytes)
                await Task.Run(() => _s.Backdrops.EnsureFromBytes(fanartKey, bytes));
            Backdrop = await Load(() => _s.Backdrops.Has(fanartKey) ? new Bitmap(_s.Backdrops.PathFor(fanartKey)) : null);
        }
        if (Logo is null && Movie.OnlineLogo is { } onlineLogo)
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

    /// <summary>Loads off the UI thread, with a deadline (the NAS may be asleep).</summary>
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

    public void NotifyWatchedChanged()
    {
        OnPropertyChanged(nameof(Watched));
        OnPropertyChanged(nameof(WatchedText));
    }

    [RelayCommand]
    private void Play() => _owner.Play(Card, null);

    [RelayCommand]
    private void PlayVersion(VideoVersion version) => _owner.Play(Card, version.File);

    [RelayCommand]
    private void ToggleWatched() => _owner.SetWatched(Card, !Movie.Watched);

    [RelayCommand]
    private void Reveal() => _owner.Reveal(Card);

    [RelayCommand]
    private Task FindSubtitles() => _owner.FindSubtitlesAsync(Card);

    public bool HasOpenSubtitlesKey => _owner.HasOpenSubtitlesKey;

    [RelayCommand]
    private Task Identify() => _owner.IdentifyAsync(Card);

    [RelayCommand]
    private Task Delete() => _owner.DeleteAsync(Card);

    [RelayCommand]
    private Task RefreshMetadata() => _owner.RefreshMetadataAsync(Card);

    public bool HasTmdbKey => _owner.HasTmdbKey;
    public bool NeedsMatch => Movie.NeedsMatch;

    /// <summary>Choosing by hand counts only for a movie the folder doesn't identify.</summary>
    public bool CanIdentify { get; }
    public bool CanIdentifyOnline => CanIdentify && HasTmdbKey;

    /// <summary>Opened by Random: the page offers another pick.</summary>
    public bool FromRandom { get; init; }

    [RelayCommand]
    private void AnotherRandom() => _owner.RandomCommand.Execute(null);

    [RelayCommand]
    private void OpenPerson(CastMember member) => _owner.OpenPerson(member.Name, member.TmdbId, director: false);

    [RelayCommand]
    private void OpenDirector(string name) => _owner.OpenPerson(name, null, director: true);

    [RelayCommand]
    private void Back() => _owner.CloseDetailCommand.Execute(null);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!ReferenceEquals(Backdrop, Poster))
            Backdrop?.Dispose();
        Poster?.Dispose();
        Logo?.Dispose();
    }

    private static string? Join(IEnumerable<string> values)
    {
        var text = string.Join(", ", values);
        return text.Length == 0 ? null : text;
    }

    private static string Channels(int? channels) => channels switch
    {
        null or 0 => "",
        1 => " 1.0",
        2 => " 2.0",
        6 => " 5.1",
        8 => " 7.1",
        _ => $" {channels}ch",
    };
}
