using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;

namespace Kinora.ViewModels;

/// <summary>A collection in the Collections view: the poster of one of its movies, its name, how many.</summary>
public sealed partial class CollectionTile(string name, IReadOnlyList<MovieCardViewModel> movies, Action<CollectionTile> open) : ObservableObject
{
    public string Name { get; } = name;
    public IReadOnlyList<MovieCardViewModel> Movies { get; } = movies;

    /// <summary>The first movie with a poster: the collection's own isn't in the library.</summary>
    public MovieCardViewModel Cover { get; } = movies.FirstOrDefault(m => m.Movie.ThumbKey is not null) ?? movies[0];

    public bool AllWatched => Movies.All(m => m.Watched);

    public string CountText
    {
        get
        {
            var seen = Movies.Count(m => m.Watched);
            var count = Format.Count(Movies.Count, "movie", "movies");
            return seen == 0 || seen == Movies.Count ? count : $"{count} · {seen} watched";
        }
    }

    public string YearsText
    {
        get
        {
            var years = Movies.Select(m => m.Movie.Year).OfType<int>().ToList();
            return years.Count == 0 ? "" : years.Min() == years.Max() ? $"{years.Min()}" : $"{years.Min()}–{years.Max()}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PosterHeight))]
    public partial double Width { get; set; } = 170;

    public double PosterHeight => Math.Round(Width * 1.5);

    [RelayCommand]
    private void Open() => open(this);
}

public sealed class CollectionRow(IReadOnlyList<CollectionTile> items)
{
    public IReadOnlyList<CollectionTile> Items { get; } = items;
}

/// <summary>
/// The Collections view: every collection of the movies as a poster, and a click on one shows its
/// movies in the order they came out. Back (Esc, the mouse's back button) returns to them all.
/// </summary>
public partial class MainWindowViewModel
{
    private List<CollectionTile> _tiles = [];

    /// <summary>The collection whose movies the view shows; null: all the collections.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InCollection))]
    public partial string? OpenCollectionName { get; private set; }

    public bool InCollection => OpenCollectionName is not null && SelectedNav?.Kind == NavKind.Collections;

    /// <summary>Opened from the movie's page: Back goes back there, not to all the collections.</summary>
    private bool _collectionFromPage;

    private bool ShowingTiles => SelectedNav?.Kind == NavKind.Collections && OpenCollectionName is null;

    private void OpenTile(CollectionTile tile) => OpenCollection(tile.Name, fromPage: false);

    private void OpenCollection(string name, bool fromPage)
    {
        ClearSelection();
        _collectionFromPage = fromPage;
        OpenCollectionName = name;
        SearchText = "";   // the search was for the collection
        ApplyFilter(toTop: true);
    }

    /// <summary>Back to all the collections (the button above the title, Esc).</summary>
    [RelayCommand]
    public void CloseCollection()
    {
        if (OpenCollectionName is null)
            return;
        ClearSelection();
        OpenCollectionName = null;
        _collectionFromPage = false;
        ApplyFilter(toTop: true);
    }

    /// <summary>Back in the library, inside a collection opened from the list: out of it.</summary>
    public bool BackOutOfCollection()
    {
        if (!ShowLibrary || !InCollection || _collectionFromPage)
            return false;
        CloseCollection();
        return true;
    }

    /// <summary>The collections whose movies pass the search and the filters.</summary>
    private List<CollectionTile> BuildTiles(string query, IEnumerable<MovieCardViewModel> allowed)
    {
        var groups = MovieCollections.Group(allowed, c => c.Movie);
        return groups.Where(g => query.Length == 0
                                 || Core.SearchText.Matches(Core.SearchText.Normalize(g.Name), query)
                                 || g.Movies.Any(m => Core.SearchText.Matches(m.SearchKey, query)))
                     .Select(g => new CollectionTile(g.Name, g.Movies, OpenTile))
                     .ToList();
    }

    private int CollectionCount() => MovieCollections.Group(_movies, c => c.Movie).Count;

    /// <summary>
    /// All the collections, as posters in the grid: those with a movie that passes the filters,
    /// found by their name or by one of their movies (a search for "Ripley" finds Alien's).
    /// </summary>
    private void ShowTiles(string query, bool toTop)
    {
        var filter = CurrentFilter;
        var allowed = _movies.Where(c => filter.Allows(c.Movie, CardViewModel.RatingSource) && (!OnlyUnwatched || !c.Watched));
        _tiles = BuildTiles(query, allowed);
        _filtered = [];
        foreach (var card in CurrentCards)
            card.Note = null;
        OnPropertyChanged(nameof(EmptyFolders));
        OnPropertyChanged(nameof(EmptyFoldersTitle));
        OnPropertyChanged(nameof(ShowEmptyFolders));
        ViewTitle = "Collections";
        CountText = query.Length > 0
            ? $"{Format.Count(_tiles.Count, "collection", "collections")} matching “{SearchText.Trim()}”"
            : Format.Count(_tiles.Count, "collection", "collections");
        HasNoResults = _tiles.Count == 0;
        if (_tiles.Count == 0)
            Rows = [];
        LayoutRows();
        if (toTop)
            ScrollToTopRequested?.Invoke();
    }
}
