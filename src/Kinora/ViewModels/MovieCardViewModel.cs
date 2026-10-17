using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Services;

namespace Kinora.ViewModels;

/// <summary>One poster in the grid.</summary>
public partial class MovieCardViewModel : ViewModelBase, IPosterHolder
{
    private readonly MainWindowViewModel _owner;
    private readonly PosterLoader _loader;
    private Bitmap? _poster;
    private bool _requested;

    public MovieCardViewModel(MainWindowViewModel owner, PosterLoader loader, Movie movie, string castNames)
    {
        _owner = owner;
        _loader = loader;
        Movie = movie;
        CastNames = castNames;
        Refresh();
    }

    public Movie Movie { get; private set; }
    public string CastNames { get; private set; }
    public string SearchKey { get; private set; } = "";
    public string SortKey { get; private set; } = "";

    public string Title => Movie.Title;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (Movie.Year is { } year)
                parts.Add(year.ToString());
            if (Movie.Runtime is { } minutes)
                parts.Add(Format.Runtime(minutes));
            return string.Join("  ·  ", parts);
        }
    }

    public bool Watched => Movie.Watched;
    public string WatchedMenuText => Movie.Watched ? "Mark as unwatched" : "Mark as watched";

    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PosterHeight))]
    public partial double CardWidth { get; set; } = 170;

    public double PosterHeight => Math.Round(CardWidth * 1.5);

    /// <summary>Pixels to decode posters at; set from the card size and the screen scaling.</summary>
    public static int DecodeWidth { get; set; } = 240;

    public bool HasPoster => _poster is not null;

    public Bitmap? Poster
    {
        get
        {
            if (!_requested && Movie.ThumbKey is { } key)
            {
                _requested = true;
                _ = LoadPosterAsync(key);
            }
            return _poster;
        }
    }

    private async Task LoadPosterAsync(string key)
    {
        var bitmap = await _loader.LoadAsync(this, key, DecodeWidth);
        if (bitmap is null || key != Movie.ThumbKey)
            return;
        _poster = bitmap;
        OnPropertyChanged(nameof(Poster));
        OnPropertyChanged(nameof(HasPoster));
    }

    public void ReleasePoster(Bitmap bitmap)
    {
        if (!ReferenceEquals(_poster, bitmap))
            return;
        _poster = null;
        _requested = false;
        OnPropertyChanged(nameof(Poster));
        OnPropertyChanged(nameof(HasPoster));
    }

    /// <summary>After a scan: same card, fresh data.</summary>
    public void Update(Movie movie, string castNames)
    {
        var newPoster = movie.ThumbKey != Movie.ThumbKey;
        Movie = movie;
        CastNames = castNames;
        if (newPoster)
        {
            _poster = null;
            _requested = false;
        }
        Refresh();
        OnPropertyChanged(string.Empty);
    }

    public void NotifyWatchedChanged()
    {
        OnPropertyChanged(nameof(Watched));
        OnPropertyChanged(nameof(WatchedMenuText));
    }

    private void Refresh()
    {
        var m = Movie;
        SearchKey = SearchText.Normalize(new StringBuilder()
            .Append(m.Title).Append(' ').Append(m.OriginalTitle).Append(' ').Append(m.Year).Append(' ')
            .Append(m.Directors.Replace('|', ' ')).Append(' ').Append(m.Collection).Append(' ').Append(CastNames)
            .ToString());
        SortKey = SortTitle(m.SortTitle ?? m.Title);
    }

    /// <summary>"The Matrix" sorts under M, as in every library.</summary>
    private static string SortTitle(string title)
    {
        var t = SearchText.Normalize(title);
        foreach (var article in new[] { "the ", "a ", "an " })
            if (t.StartsWith(article, StringComparison.Ordinal) && t.Length > article.Length)
                return t[article.Length..];
        return t;
    }

    [RelayCommand]
    private void Open() => _owner.OpenDetail(this);

    [RelayCommand]
    private void Play() => _owner.Play(this, null);

    [RelayCommand]
    private void Reveal() => _owner.Reveal(this);

    [RelayCommand]
    private void ToggleWatched() => _owner.SetWatched(this, !Movie.Watched);
}
