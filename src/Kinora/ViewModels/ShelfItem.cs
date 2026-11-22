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

    /// <param name="add">
    /// Adds it to Radarr or Sonarr (<paramref name="addTo"/>), giving the note to show after:
    /// "Added to Radarr", or what went wrong ("Radarr: …"); null when the user changed their mind.
    /// </param>
    public ShelfItem(TmdbSimilar similar, Action open, string? note = null, string? addTo = null, Func<Task<string?>>? add = null)
    {
        Similar = similar;
        Title = similar.Title;
        YearText = similar.Year?.ToString(CultureInfo.InvariantCulture);
        Note = note ?? (similar.Rating is { } r ? $"★ {r.ToString("0.0", CultureInfo.InvariantCulture)} · on TMDB" : "On TMDB");
        _open = open;
        _add = add;
        AddTo = addTo;
        CanAdd = add is not null && addTo is not null;
    }

    private readonly Func<Task<string?>>? _add;

    public CardViewModel? Card { get; }
    public TmdbSimilar? Similar { get; }
    public bool InLibrary => Card is not null;
    public string Title { get; }
    public string? YearText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteTip))]
    public partial string Note { get; private set; }

    /// <summary>The note in full, for one cut short (an error).</summary>
    public string NoteTip => Note;

    public string Tip => InLibrary ? Title : $"{Title}: open on TMDB";

    /// <summary>"Radarr" or "Sonarr", where the + button adds it.</summary>
    public string? AddTo { get; }
    public string AddTip => $"Add to {AddTo}: it looks for it and downloads it";

    [ObservableProperty]
    public partial bool CanAdd { get; private set; }

    [RelayCommand]
    private async Task Add()
    {
        if (_add is null || !CanAdd)
            return;
        CanAdd = false;
        var note = await _add();
        if (note is null)
        {
            CanAdd = true;   // not added after all
            return;
        }
        Note = note;
        CanAdd = note.StartsWith(AddTo + ":", StringComparison.Ordinal);   // an error: it can be tried again
    }

    /// <summary>TMDB's poster, for a movie not in the library; the library's come from the card.</summary>
    [ObservableProperty]
    public partial Bitmap? Picture { get; set; }

    [RelayCommand]
    private void Open() => _open();
}
