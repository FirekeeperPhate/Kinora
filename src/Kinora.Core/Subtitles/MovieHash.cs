namespace Kinora.Core.Subtitles;

/// <summary>
/// The OpenSubtitles file hash: the size plus the first and the last 64 KB summed as 64-bit
/// words. Two small reads, even for a 20 GB file on the NAS, and it finds subtitles made for that
/// exact copy (same cut, same timing).
/// </summary>
public static class MovieHash
{
    private const int Chunk = 64 * 1024;

    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        return Compute(stream);
    }

    public static string Compute(Stream stream)
    {
        var length = stream.Length;
        ulong hash = (ulong)length;
        var buffer = new byte[Chunk];

        hash = unchecked(hash + Sum(stream, 0, buffer, (int)Math.Min(Chunk, length)));
        hash = unchecked(hash + Sum(stream, Math.Max(0, length - Chunk), buffer, (int)Math.Min(Chunk, length)));
        return hash.ToString("x16");
    }

    private static ulong Sum(Stream stream, long offset, byte[] buffer, int count)
    {
        stream.Position = offset;
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
                break;
            read += n;
        }
        ulong sum = 0;
        for (var i = 0; i + 8 <= read; i += 8)
            sum = unchecked(sum + BitConverter.ToUInt64(buffer, i));
        return sum;
    }
}
