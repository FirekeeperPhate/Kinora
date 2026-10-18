using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Services;

namespace Kinora.ViewModels;

/// <summary>One poster in the grid: a movie or a TV show.</summary>
public abstract partial class CardViewModel : ViewModelBase, IPosterHolder
{
    private readonly PosterLoader _loader;
    private Bitmap? _poster;
    private bool _requested;

    protected CardViewModel(PosterLoader loader) => _loader = loader;

    public abstract long Id { get; }
    public abstract long RootId { get; }
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public abstract int? Year { get; }
    public abstract double? Rating { get; }
    public abstract long DateAdded { get; }
    public abstract bool NeedsMatch { get; }
    public abstract IReadOnlyList<string> Genres { get; }
    protected abstract string? ThumbKey { get; }

    public string SearchKey { get; protected set; } = "";
    public string SortKey { get; protected set; } = "";

    /// <summary>Fully watched (a movie, or every episode of a show).</summary>
    public abstract bool Watched { get; }

    /// <summary>Episodes still to see; 0 for movies.</summary>
    public virtual int UnwatchedCount => 0;
    public bool ShowUnwatchedCount => UnwatchedCount > 0 && !Watched;
    public abstract string WatchedMenuText { get; }

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
            if (!_requested && ThumbKey is { } key)
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
        if (bitmap is null || key != ThumbKey)
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

    /// <summary>After a scan the same card shows fresh data; a new poster is loaded again.</summary>
    protected void Refreshed(bool posterChanged)
    {
        if (posterChanged)
        {
            _poster = null;
            _requested = false;
        }
        OnPropertyChanged(string.Empty);
    }

    public void NotifyWatchedChanged()
    {
        OnPropertyChanged(nameof(Watched));
        OnPropertyChanged(nameof(WatchedMenuText));
        OnPropertyChanged(nameof(UnwatchedCount));
        OnPropertyChanged(nameof(ShowUnwatchedCount));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>"The Matrix" sorts under M, as in every library.</summary>
    protected static string SortTitle(string title)
    {
        var t = SearchText.Normalize(title);
        foreach (var article in new[] { "the ", "a ", "an " })
            if (t.StartsWith(article, StringComparison.Ordinal) && t.Length > article.Length)
                return t[article.Length..];
        return t;
    }

    // ---- selection ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelector))]
    public partial bool IsSelected { get; set; }

    /// <summary>Something is selected: every card shows its select dot, a click selects instead of opening.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelector))]
    public partial bool SelectionActive { get; set; }

    public bool ShowSelector => IsSelected || SelectionActive;

    public static Action<CardViewModel>? ToggleSelectHandler { get; set; }

    [RelayCommand]
    private void ToggleSelect() => ToggleSelectHandler?.Invoke(this);

    /// <summary>The other library folders of the same kind, for "Move to"; filled when the menu opens.</summary>
    public IReadOnlyList<MoveTarget> MoveTargets => MoveTargetsProvider?.Invoke(this) ?? [];
    public bool CanMove => MoveTargets.Count > 0;

    public static Func<CardViewModel, IReadOnlyList<MoveTarget>>? MoveTargetsProvider { get; set; }

    [RelayCommand]
    protected abstract void Open();

    [RelayCommand]
    protected abstract void Play();

    [RelayCommand]
    protected abstract void Reveal();

    [RelayCommand]
    protected abstract void ToggleWatched();

    [RelayCommand]
    protected abstract Task Identify();

    [RelayCommand]
    protected abstract Task Delete();
}
