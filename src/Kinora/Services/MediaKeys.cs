using Avalonia.Threading;
#if WINDOWS
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
#endif

namespace Kinora.Services;

public enum MediaKey
{
    Play,
    Pause,
    Stop,
    FastForward,
    Rewind,
}

/// <summary>
/// Windows' media controls for the built-in player: the media keys, and the buttons of Bluetooth
/// headphones, reach Kinora even when another window has the focus, and Windows' media panel shows
/// what plays, with its poster. Windows sends them to the session that played last. Elsewhere
/// <see cref="TryCreate"/> gives null and the player keeps its own keys.
/// </summary>
public sealed class MediaKeys : IDisposable
{
#if WINDOWS
    /// <summary>A key or a button, on the UI thread.</summary>
    public event Action<MediaKey>? Pressed;

    private readonly SystemMediaTransportControls _controls;

    private MediaKeys(SystemMediaTransportControls controls)
    {
        _controls = controls;
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsStopEnabled = true;
        controls.IsFastForwardEnabled = true;
        controls.IsRewindEnabled = true;
        controls.IsNextEnabled = false;
        controls.IsPreviousEnabled = false;
        controls.ButtonPressed += OnButtonPressed;
    }

    /// <summary>The media controls of a window, or null where there are none.</summary>
    public static MediaKeys? TryCreate(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return null;
        try
        {
            return new MediaKeys(SystemMediaTransportControlsInterop.GetForWindow(window));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        MediaKey? key = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaKey.Play,
            SystemMediaTransportControlsButton.Pause => MediaKey.Pause,
            SystemMediaTransportControlsButton.Stop => MediaKey.Stop,
            SystemMediaTransportControlsButton.FastForward => MediaKey.FastForward,
            SystemMediaTransportControlsButton.Rewind => MediaKey.Rewind,
            _ => null,
        };
        // Windows' thread: the player is called on the UI thread
        if (key is { } k)
            Dispatcher.UIThread.Post(() => Pressed?.Invoke(k));
    }

    /// <summary>Windows chooses Play or Pause for the play/pause key from this.</summary>
    public void SetPlaying(bool playing) =>
        _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

    /// <summary>What Windows' media panel shows: the title, the line under it, the poster.</summary>
    public async void SetInfo(string title, string? subtitle, string? picture)
    {
        try
        {
            var display = _controls.DisplayUpdater;
            display.Type = MediaPlaybackType.Video;
            display.VideoProperties.Title = title;
            display.VideoProperties.Subtitle = subtitle ?? "";
            if (picture is not null && File.Exists(picture))
                display.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(picture));
            display.Update();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a title the keys still work
        }
    }

    public void Dispose()
    {
        _controls.ButtonPressed -= OnButtonPressed;
        try
        {
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            _controls.IsEnabled = false;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
#else
    /// <summary>Never raised here: there are no system media controls.</summary>
    public event Action<MediaKey>? Pressed
    {
        add { }
        remove { }
    }

    private MediaKeys()
    {
    }

    public static MediaKeys? TryCreate(IntPtr window) => null;

    public void SetPlaying(bool playing)
    {
    }

    public void SetInfo(string title, string? subtitle, string? picture)
    {
    }

    public void Dispose()
    {
    }
#endif
}
