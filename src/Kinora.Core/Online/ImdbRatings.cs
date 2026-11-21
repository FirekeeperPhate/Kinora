using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using Kinora.Core.Data;

namespace Kinora.Core.Online;

/// <summary>
/// IMDb's ratings, from the file IMDb itself publishes for personal and non-commercial use
/// (no key, no account). The file (some 9 MB) is kept and downloaded again when it is a few
/// days old; only the ratings of the titles in the library go into the database.
/// </summary>
public sealed class ImdbRatings(LibraryDatabase db, string filePath, HttpMessageHandler? handler = null)
{
    public const string Url = "https://datasets.imdbws.com/title.ratings.tsv.gz";

    /// <summary>Required by IMDb's terms for its data.</summary>
    public const string Attribution = "Information courtesy of IMDb (https://www.imdb.com). Used with permission.";

    /// <summary>IMDb refreshes the file every day; ratings move slowly.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    private readonly HttpClient _http = CreateClient(handler);

    /// <summary>The ids looked for the last time the file was read: a title IMDb lacks doesn't make it read again.</summary>
    private HashSet<string>? _lastRead;

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kinora", null));
        return client;
    }

    /// <summary>
    /// Downloads the file when it is missing or old, and reads it when it is new or the library
    /// has titles it wasn't read for. Says how many titles got a different rating.
    /// </summary>
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        var ids = db.GetLibraryImdbIds();
        if (ids.Count == 0)
            return 0;
        var file = new FileInfo(filePath);
        var downloaded = false;
        if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > MaxAge)
        {
            try
            {
                await DownloadAsync(ct);
                downloaded = true;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested && file.Exists)
            {
                // Offline: the old file still says more than nothing
            }
        }
        if (!downloaded && _lastRead is { } last && ids.IsSubsetOf(last))
            return 0;

        Dictionary<string, (double Rating, int Votes)> found;
        try
        {
            await using var stream = File.OpenRead(filePath);
            found = await Task.Run(() => Read(stream, ids, ct), ct);
        }
        catch (InvalidDataException)
        {
            File.Delete(filePath);   // not the file it should be: downloaded again next time
            throw;
        }
        _lastRead = ids;
        return db.ReplaceImdbRatings(found);
    }

    /// <summary>Forgets IMDb's ratings (turned off): the titles go back to what their nfo says. Says how many changed.</summary>
    public int Clear()
    {
        _lastRead = null;   // turned on again, the file is read again
        return db.ReplaceImdbRatings(new Dictionary<string, (double, int)>());
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var part = filePath + ".part";
        using (var response = await _http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(part);
            await from.CopyToAsync(to, ct);
        }
        File.Move(part, filePath, overwrite: true);
    }

    /// <summary>
    /// The ratings of these titles in IMDb's gzipped file: tconst, averageRating and numVotes,
    /// separated by tabs, after a header line.
    /// </summary>
    public static Dictionary<string, (double Rating, int Votes)> Read(Stream gzipped, HashSet<string> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, (double, int)>(StringComparer.Ordinal);
        var lookup = new HashSet<string>(ids, StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        using var reader = new StreamReader(new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true));
        if (reader.ReadLine() is not { } header || !header.StartsWith("tconst\t", StringComparison.Ordinal))
            throw new InvalidDataException("Not IMDb's ratings file.");
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++count % 100_000 == 0)
                ct.ThrowIfCancellationRequested();
            var span = line.AsSpan();
            var tab = span.IndexOf('\t');
            if (tab < 0 || !lookup.Contains(span[..tab]))
                continue;
            var rest = span[(tab + 1)..];
            var tab2 = rest.IndexOf('\t');
            if (tab2 < 0)
                continue;
            if (double.TryParse(rest[..tab2], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                && int.TryParse(rest[(tab2 + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes))
                result[line[..tab]] = (rating, votes);
        }
        return result;
    }
}
