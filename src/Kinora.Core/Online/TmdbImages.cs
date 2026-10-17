namespace Kinora.Core.Online;

/// <summary>Pictures come from TMDB's public image server: no key needed.</summary>
public static class TmdbImages
{
    public const string Base = "https://image.tmdb.org/t/p/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>Replaced in tests.</summary>
    internal static Func<string, CancellationToken, Task<byte[]?>>? Override { get; set; }

    public static string Url(string path, string size) => Base + size + path;

    public static async Task<byte[]?> DownloadAsync(string path, string size, CancellationToken ct)
    {
        if (Override is { } fake)
            return await fake(Url(path, size), ct);
        try
        {
            using var response = await Http.GetAsync(Url(path, size), ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
