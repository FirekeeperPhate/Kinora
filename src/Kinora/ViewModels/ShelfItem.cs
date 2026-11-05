using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Online;

namespace Kinora.ViewModels;

/// <summary>
/// A poster in a row of a movie page: a movie of the library (it opens its page) or one TMDB
/// recommends that isn't there (it opens TMDB's page, with TMDB's poster).
/// </summary>
public sealed partial class ShelfItem : ObservableObject
{
    private readonly Action _open;

    public ShelfItem(MovieCardViewModel card, string note, Action open)
    {
        Card = card;
        Title = card.Title;
        YearText = card.Year?.ToString(CultureInfo.InvariantCulture);
        Note = note;
        _open = open;
    }

    public ShelfItem(TmdbSimilar similar, Action open)
    {
        Similar = similar;
        Title = similar.Title;
        YearText = similar.Year?.ToString(CultureInfo.InvariantCulture);
        Note = similar.Rating is { } r ? $"★ {r.ToString("0.0", CultureInfo.InvariantCulture)} · on TMDB" : "On TMDB";
        _open = open;
    }

    public MovieCardViewModel? Card { get; }
    public TmdbSimilar? Similar { get; }
    public bool InLibrary => Card is not null;
    public string Title { get; }
    public string? YearText { get; }
    public string Note { get; }
    public string Tip => InLibrary ? Title : $"{Title}: open on TMDB";

    /// <summary>TMDB's poster, for a movie not in the library; the library's come from the card.</summary>
    [ObservableProperty]
    public partial Bitmap? Picture { get; set; }

    [RelayCommand]
    private void Open() => _open();
}
