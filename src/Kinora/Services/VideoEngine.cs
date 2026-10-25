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
            return (new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--no-stats"), null);
        }
        catch (Exception e) when (e is VLCException or DllNotFoundException or TypeInitializationException or BadImageFormatException or InvalidOperationException)
        {
            return (null, OperatingSystem.IsLinux()
                ? "VLC is not installed: install the vlc package (it brings libvlc), then try again."
                : $"VLC's engine didn't start: {e.Message}");
        }
    }

    public void Dispose()
    {
        _vlc?.Dispose();
        _gate.Dispose();
    }
}
