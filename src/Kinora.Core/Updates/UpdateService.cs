using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kinora.Core.Updates;

/// <summary>A newer release on GitHub, with the package for this computer.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string AssetName, string AssetUrl, long AssetSize, string? Sha256, string? Notes);

/// <summary>
/// Looks for a newer Kinora on GitHub and downloads its package. Only the release published as
/// "latest" counts (never drafts or pre-releases), and the file must come from GitHub. While the
/// repository is private GitHub answers "not found" to anyone else: there is simply no update.
/// </summary>
public sealed class UpdateService(Version current, HttpMessageHandler? handler = null)
{
    public const string Repository = "FirekeeperPhate/Kinora";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>Automatic checks happen at most this often.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>Longest wait for the next piece of a download.</summary>
    internal TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = CreateClient(current, handler);

    public Version Current { get; } = current;

    private static HttpClient CreateClient(Version current, HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(20);
        // GitHub's API refuses requests without a User-Agent
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kinora", current.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The package built for this computer, as installer\build.ps1 names it.</summary>
    public static string AssetNameFor(string version)
    {
        if (OperatingSystem.IsWindows())
            return $"Kinora-Setup-{version}.exe";
        if (OperatingSystem.IsMacOS())
            return $"Kinora-{version}-macos-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.zip";
        return $"Kinora-{version}-linux-x64.tar.gz";
    }

    /// <returns>The newer release, or null if this is the latest (or there is no package for this computer).</returns>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(LatestReleaseApi, ct);
        // No release yet, or the repository is not public: nothing to update to
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseRelease(json.RootElement, Current, AssetNameFor);
    }

    /// <summary>Reads GitHub's "latest release" answer (separate for the tests).</summary>
    internal static UpdateInfo? ParseRelease(JsonElement release, Version current, Func<string, string> assetName)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;
        var tag = (release.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version))
            return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= current)
            return null;

        var wanted = assetName(tag);
        if (!release.TryGetProperty("assets", out var assets))
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsGitHubUrl(url))
                return null;
            var sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                      && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
            var page = release.TryGetProperty("html_url", out var html) && html.GetString() is { } h && IsGitHubUrl(h) ? h : ReleasesPage;
            // Without a size the download could not be checked: no update rather than an error
            if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out var bytes) || bytes <= 0)
                return null;
            var notes = release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
            return new UpdateInfo(version, page, wanted, url, bytes, sha, notes);
        }
        return null;
    }

    internal static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "Kinora-update");

    /// <summary>Deletes the packages downloaded for earlier updates (except one of the last hour).</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder))
                return;
            foreach (var file in Directory.EnumerateFiles(DownloadFolder))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(1))
                    continue;
                try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (!Directory.EnumerateFileSystemEntries(DownloadFolder).Any())
                Directory.Delete(DownloadFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Downloads the package into a temporary folder and checks its size (and SHA-256, when GitHub
    /// gives it). Returns the path of the checked file.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadFolder);
        var path = Path.Combine(DownloadFolder, update.AssetName);
        var partial = path + ".part";
        try
        {
            using (var response = await _http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is { } final && !IsGitHubUrl(final.AbsoluteUri))
                    throw new InvalidDataException("The download was redirected outside GitHub.");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                // HttpClient.Timeout ends with the headers: a connection that stalls later would wait forever
                using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = SHA256.Create();
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    stalled.CancelAfter(ReadTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stalled.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException("The download stopped responding.");
                    }
                    if (read == 0)
                        break;
                    total += read;
                    if (total > update.AssetSize)
                        throw new InvalidDataException("The download is larger than announced.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    progress?.Report((double)total / update.AssetSize);
                }
                sha.TransformFinalBlock([], 0, 0);
                if (total != update.AssetSize)
                    throw new InvalidDataException("The download is incomplete.");
                if (update.Sha256 is { } expected && !string.Equals(Convert.ToHexString(sha.Hash!), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded package does not match the release (checksum).");
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    /// <summary>Installed with the Windows setup (its uninstaller is next to it): it can update itself.</summary>
    public static bool CanInstallItself =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
}
