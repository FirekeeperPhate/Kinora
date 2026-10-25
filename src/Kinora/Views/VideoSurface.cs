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
    public MediaPlayer? Player { get; set; }

    /// <summary>VLC has its window: playback can start.</summary>
    public event Action? Ready;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (Player is { } player)
        {
            if (OperatingSystem.IsWindows())
                player.Hwnd = handle.Handle;
            else if (OperatingSystem.IsMacOS())
                player.NsObject = handle.Handle;
            else
                player.XWindow = (uint)handle.Handle;
            Ready?.Invoke();
        }
        return handle;
    }

    // The window goes a moment after the page, when the player has been stopped and disposed:
    // VLC is not told anything then (a disposed player crashes the process).
}
