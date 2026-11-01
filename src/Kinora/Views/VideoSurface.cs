using Avalonia.Controls;
using Avalonia.Platform;
using LibVLCSharp.Shared;

namespace Kinora.Views;

/// <summary>
/// A native child window for VLC to draw the video in (hardware decoding and HDR included).
/// Nothing drawn by Avalonia can sit on top of it: the player's controls live in a popup,
/// which is a window of its own.
/// </summary>
public sealed class VideoSurface : NativeControlHost
{
    private IPlatformHandle? _window;

    public MediaPlayer? Player
    {
        get;
        set
        {
            field = value;
            if (value is not null && _window is not null)
                Attach(value, _window);
        }
    }

    /// <summary>VLC has its window: playback can start.</summary>
    public event Action? Ready;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _window = handle;
        if (Player is { } player)
        {
            Attach(player, handle);
            Ready?.Invoke();
        }
        return handle;
    }

    private static void Attach(MediaPlayer player, IPlatformHandle window)
    {
        if (OperatingSystem.IsWindows())
            player.Hwnd = window.Handle;
        else if (OperatingSystem.IsMacOS())
            player.NsObject = window.Handle;
        else
            player.XWindow = (uint)window.Handle;
    }

    // The window goes a moment after the page, when the player has been stopped and disposed:
    // VLC is not told anything then (a disposed player crashes the process).
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _window = null;
        base.DestroyNativeControlCore(control);
    }
}
