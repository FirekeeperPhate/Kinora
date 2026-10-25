using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kinora.Views;

/// <summary>
/// The player's controls, in a borderless transparent window owned by Kinora's and kept over the
/// video by the page. Unlike a popup it may cover the taskbar's place in full screen. It never
/// takes the focus: the keys keep going to Kinora's window, the title bar doesn't go grey.
/// </summary>
internal sealed class PlayerOverlayWindow : Window
{
    public PlayerOverlayWindow(Control content)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Content = content;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            // Clicks don't activate it, and it isn't a window of its own for Alt+Tab
            var style = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
            // Windows 11 rounds the corners of every window: not this one
            var square = DwmWcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref square, sizeof(int));
        }
    }

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWcpDoNotRound = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
