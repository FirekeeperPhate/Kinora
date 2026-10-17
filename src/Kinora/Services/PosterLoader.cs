using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kinora.Core.Imaging;

namespace Kinora.Services;

public interface IPosterHolder
{
    /// <summary>
    /// Called on the UI thread when the loader takes a picture back to free memory: drop it if it
    /// is the one still shown.
    /// </summary>
    void ReleasePoster(Bitmap bitmap);
}

/// <summary>
/// Decodes posters from the local thumbnail cache, a few at a time, and keeps only the most
/// recently requested ones in memory. The grid is virtualized, so what was asked for last is
/// what is on screen; the ones evicted are long scrolled away.
/// </summary>
public sealed class PosterLoader(ImageCache cache, int capacity = 360)
{
    private readonly SemaphoreSlim _decoders = new(4);
    private readonly LinkedList<(IPosterHolder Holder, Bitmap Bitmap)> _recent = new();

    public async Task<Bitmap?> LoadAsync(IPosterHolder holder, string key, int width)
    {
        var path = cache.PathFor(key);
        await _decoders.WaitAsync();
        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(() =>
            {
                if (!File.Exists(path))
                    return null;
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
            });
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            bitmap = null;
        }
        finally
        {
            _decoders.Release();
        }

        if (bitmap is not null)
            Dispatcher.UIThread.Post(() => Remember(holder, bitmap));
        return bitmap;
    }

    private void Remember(IPosterHolder holder, Bitmap bitmap)
    {
        _recent.AddLast((holder, bitmap));
        while (_recent.Count > capacity)
        {
            var (oldHolder, oldBitmap) = _recent.First!.Value;
            _recent.RemoveFirst();
            // First let the Image let go of it, then free it
            oldHolder.ReleasePoster(oldBitmap);
            oldBitmap.Dispose();
        }
    }
}
