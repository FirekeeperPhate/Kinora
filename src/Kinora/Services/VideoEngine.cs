using LibVLCSharp.Shared;

namespace Kinora.Services;

/// <summary>
/// VLC's engine for the built-in player, started once, on first use. On Windows it comes with
/// Kinora; on macOS the one inside VLC.app is used, on Linux the system's libvlc (the vlc
/// package). When it can't start, the reason says what to install.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibVLC? _vlc;
    private string? _error;

    /// <summary>The engine, or null with <paramref name="Error"/> saying why.</summary>
    public async Task<(LibVLC? Vlc, string? Error)> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_vlc is null && _error is null)
                (_vlc, _error) = await Task.Run(Start);
            return (_vlc, _error);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (LibVLC?, string?) Start()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                const string vlcApp = "/Applications/VLC.app/Contents/MacOS/lib";
                if (!Directory.Exists(vlcApp))
                    return (null, "VLC is not installed: get it from videolan.org, then try again.");
                LibVLCSharp.Shared.Core.Initialize(vlcApp);
            }
            else
            {
                LibVLCSharp.Shared.Core.Initialize();
            }
            // Kinora shows its own title and messages; subtitles next to the video are found by VLC
            string[] options = ["--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--no-stats"];
            // Without its module cache VLC loads all of its ~280 modules at every start (37 s from
            // a slow disk, 0.85 s with the cache): a copy that lacks it writes it now, once
            if (BundledPluginsCache is { } cache && !File.Exists(cache))
                options = [.. options, "--reset-plugins-cache"];
            return (new LibVLC(options), null);
        }
        catch (Exception e) when (e is VLCException or DllNotFoundException or TypeInitializationException or BadImageFormatException or InvalidOperationException)
        {
            return (null, OperatingSystem.IsLinux()
                ? "VLC is not installed: install the vlc package (it brings libvlc), then try again."
                : $"VLC's engine didn't start: {e.Message}");
        }
    }

    /// <summary>The module cache of the VLC that comes with Kinora (Windows); null where Kinora uses the computer's VLC.</summary>
    private static string? BundledPluginsCache =>
        OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins", "plugins.dat") : null;

    /// <summary>
    /// Writes VLC's module cache next to the modules (Kinora.exe --vlc-plugins-cache, when the
    /// packages are built): the first Play then loads only the modules it needs.
    /// </summary>
    public static bool WritePluginsCache()
    {
        if (BundledPluginsCache is not { } cache)
            return false;
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            using (new LibVLC("--reset-plugins-cache"))
            {
            }
            return File.Exists(cache);
        }
        catch (Exception e) when (e is VLCException or DllNotFoundException or TypeInitializationException or IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _vlc?.Dispose();
        _gate.Dispose();
    }
}
