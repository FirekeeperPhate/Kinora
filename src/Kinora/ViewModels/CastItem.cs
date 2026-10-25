using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Services;

namespace Kinora.ViewModels;

/// <summary>An actor on a movie or show page: name, role, and the photo once it has been found.</summary>
public sealed partial class CastItem : ObservableObject
{
    public CastItem(CastMember member) => Member = member;

    public CastMember Member { get; }
    public string Name => Member.Name;
    public string? Role => Member.Role;

    /// <summary>"Tom Sizemore" → "TS", in the circle while there is no photo.</summary>
    public string Initials => string.Concat(Member.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                  .Where(w => char.IsLetter(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    public partial Bitmap? Photo { get; private set; }

    public bool HasPhoto => Photo is not null;

    public void Release()
    {
        var photo = Photo;
        Photo = null;
        photo?.Dispose();
    }

    /// <summary>
    /// Finds the photos of a page's cast, a few at a time, in the order shown. An actor matched
    /// by name in TMDB's credits also gets its TMDB id, so its page opens straight away.
    /// </summary>
    public static async Task LoadPhotosAsync(AppServices s, IReadOnlyList<CastItem> items, bool series, string? tmdbId,
                                             string? titleFolder, CancellationToken ct)
    {
        if (items.Count == 0)
            return;
        try
        {
            var credits = await s.CastPhotos.CreditsAsync(s.CreateTmdbClient(), series, tmdbId, ct);
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
            {
                var match = CastPhotos.Match(item.Member, credits);
                if (item.Member.TmdbId is null && match?.Id is { } id)
                    item.Member.TmdbId = id;
                if (await s.CastPhotos.PhotoAsync(item.Member, match, titleFolder, token) is not { } file)
                    return;
                var bitmap = await Task.Run(() =>
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        return Bitmap.DecodeToWidth(stream, 96);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        return null;   // not a picture after all
                    }
                }, token);
                if (bitmap is null)
                    return;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                        bitmap.Dispose();
                    else
                        item.Photo = bitmap;
                });
            });
        }
        catch (OperationCanceledException)
        {
            // the page closed
        }
    }
}
