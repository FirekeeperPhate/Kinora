using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Online;

namespace Kinora.ViewModels;

/// <summary>TMDB's posters of the titles not in the library: small, into the poster cache, a few at a time.</summary>
public static class ShelfPosters
{
    public static async Task LoadAsync(Services.AppServices s, List<ShelfItem> items, Func<bool> gone, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(items.Where(i => i.Similar?.PosterPath is not null).Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = item.Similar!.PosterPath!;
                var key = Core.Imaging.ImageCache.KeyForOnline(path);
                if (!s.Posters.Has(key) && await TmdbImages.DownloadAsync(path, "w185", ct) is { } bytes)
                    await Task.Run(() => s.Posters.EnsureFromBytes(key, bytes), ct);
                var bitmap = await Task.Run(() => s.Posters.Has(key) ? new Bitmap(s.Posters.PathFor(key)) : null, ct);
                if (gone())
                    bitmap?.Dispose();
                else
                    item.Picture = bitmap;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or ArgumentException or InvalidOperationException)
            {
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}

/// <summary>
/// A poster in a row of a movie or show page: a movie of the library (it opens its page) or one TMDB
/// recommends that isn't there (it opens TMDB's page, with TMDB's poster).
/// </summary>
public sealed partial class ShelfItem : ObservableObject
{
    private readonly Action _open;

    public ShelfItem(CardViewModel card, string note, Action open)
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

    public CardViewModel? Card { get; }
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
