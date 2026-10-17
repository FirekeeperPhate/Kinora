using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Kinora.Core.Imaging;

/// <summary>
/// Resized copies of posters and backdrops on the local disk. The grid only ever reads these:
/// opening the library never touches the NAS, and 3000 originals of half a megabyte each would
/// not fit in memory anyway.
/// </summary>
public sealed class ImageCache(string directory, int width, int quality = 88)
{
    public string Directory { get; } = directory;
    public int Width { get; } = width;

    public string PathFor(string key) => Path.Combine(Directory, key + ".jpg");

    public bool Has(string key) => File.Exists(PathFor(key));

    /// <summary>A key that changes whenever the source file changes.</summary>
    public static string KeyFor(string sourcePath, long length, DateTime lastWriteUtc)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{sourcePath.ToLowerInvariant()}|{length}|{lastWriteUtc.Ticks}"));
        return Convert.ToHexStringLower(bytes, 0, 12);
    }

    /// <summary>
    /// Makes the resized copy unless it is already there. The source is read in one go (one
    /// request to the NAS), decoded and written as JPEG. False when the source is not a picture.
    /// </summary>
    public bool Ensure(string key, string sourcePath)
    {
        var target = PathFor(key);
        if (File.Exists(target))
            return true;

        byte[] data;
        try
        {
            data = File.ReadAllBytes(sourcePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return Write(data, target);
    }

    private bool Write(byte[] data, string target)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null)
            return false;

        var info = codec.Info;
        // Let the codec decode at a smaller scale when it can (JPEG does it for free)
        var scaled = codec.GetScaledDimensions(Math.Min(1f, (float)Width * 2 / info.Width));
        using var decoded = new SKBitmap(new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return false;

        var height = (int)Math.Round((double)Width * scaled.Height / scaled.Width);
        using var resized = scaled.Width > Width
            ? decoded.Resize(new SKImageInfo(Width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
            : decoded.Copy();
        if (resized is null)
            return false;

        // Transparent logos and PNG posters: flatten on black, JPEG has no alpha
        using var surface = SKSurface.Create(new SKImageInfo(resized.Width, resized.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        surface.Canvas.DrawBitmap(resized, 0, 0);
        using var image = surface.Snapshot();
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        if (jpeg is null)
            return false;

        System.IO.Directory.CreateDirectory(Directory);
        var temp = target + "." + Environment.ProcessId + ".tmp";
        using (var file = File.Create(temp))
            jpeg.SaveTo(file);
        File.Move(temp, target, overwrite: true);
        return true;
    }

    /// <summary>Drops the copies no item points to any more.</summary>
    public int Prune(IReadOnlySet<string> keysInUse)
    {
        if (!System.IO.Directory.Exists(Directory))
            return 0;
        var removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.jpg"))
        {
            if (keysInUse.Contains(Path.GetFileNameWithoutExtension(file)))
                continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
        }
        return removed;
    }
}
