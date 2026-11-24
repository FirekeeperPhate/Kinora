using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kinora.Services;

/// <summary>
/// The size of everything in Kinora's windows (text, buttons, posters), for a TV across the room:
/// 100% to 200%, from Settings → Appearance or Ctrl+plus and Ctrl+minus. A window's content is
/// scaled as a whole, so nothing is left small and nothing overlaps; the main window keeps its
/// size and shows less, a dialog grows with it as far as the screen allows.
/// </summary>
public static class UiScale
{
    public static readonly double[] Steps = [1, 1.1, 1.25, 1.5, 1.75, 2];

    public static double Current { get; private set; } = 1;

    public static event Action? Changed;

    /// <summary>The nearest of the steps: a value written by hand in the settings file too.</summary>
    public static double Clamp(double scale) => double.IsFinite(scale) ? Steps.MinBy(s => Math.Abs(s - scale)) : 1;

    public static void Set(double scale)
    {
        scale = Clamp(scale);
        if (Math.Abs(scale - Current) < 0.001)
            return;
        Current = scale;
        ApplyToPopups();
        Changed?.Invoke();
    }

    /// <summary>The text of tooltips, menus and lists, which the windows' scale doesn't reach.</summary>
    public static void ApplyToPopups()
    {
        if (Application.Current is not { } app)
            return;
        app.Resources["KinoraTipFontSize"] = Math.Round(12 * Current);
        app.Resources["KinoraPopupFontSize"] = Math.Round(14 * Current);
    }

    /// <summary>The next step up or down from the current one.</summary>
    public static double Step(int direction) => direction > 0
        ? Steps.FirstOrDefault(s => s > Current + 0.001, Steps[^1])
        : Steps.LastOrDefault(s => s < Current - 0.001, Steps[0]);

    public static string Percent(double scale) => $"{Math.Round(scale * 100)}%";

    /// <summary>
    /// Puts the window's content under the scale, now and whenever it changes while it is open.
    /// <paramref name="growWindow"/>: a dialog, whose size grows with its content.
    /// </summary>
    public static void Attach(Window window, bool growWindow)
    {
        if (window.Content is not Control content)
            return;
        window.Content = null;
        var host = new LayoutTransformControl { Child = content };
        window.Content = host;

        void Apply() => host.LayoutTransform = Math.Abs(Current - 1) < 0.001 ? null : new ScaleTransform(Current, Current);
        Apply();
        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;

        if (growWindow && Current > 1.001)
        {
            window.MinWidth *= Current;
            window.MinHeight *= Current;
            if (!double.IsNaN(window.Width))
                window.Width *= Current;
            if (!double.IsNaN(window.Height) && window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight))
                window.Height *= Current;
            // Not larger than the screen it opens on
            window.Opened += (_, _) =>
            {
                if (window.Screens.ScreenFromWindow(window) is not { } screen)
                    return;
                var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
                if (window.Width > area.Width - 40)
                    window.Width = Math.Max(window.MinWidth = Math.Min(window.MinWidth, area.Width - 40), area.Width - 40);
                if (window.Height > area.Height - 40)
                    window.Height = Math.Max(window.MinHeight = Math.Min(window.MinHeight, area.Height - 40), area.Height - 40);
            };
        }
    }
}
