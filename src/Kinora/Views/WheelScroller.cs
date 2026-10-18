using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kinora.Views;

/// <summary>
/// Mouse wheel scrolling that goes further per notch (Avalonia moves 50 px, which across a grid
/// of posters feels slow) and glides there instead of jumping. Works on whatever scrollable area
/// is under the pointer.
/// </summary>
internal sealed class WheelScroller
{
    private const double AvaloniaStep = 50;

    private readonly Func<double> _speed;
    private readonly DispatcherTimer _timer;
    private ScrollViewer? _target;
    private double _goal;

    public WheelScroller(Func<double> speed)
    {
        _speed = speed;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Step());
    }

    public void Attach(Interactive root) =>
        root.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl or Shift + wheel keep their usual meaning; horizontal wheels are left alone
        if (e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0 || e.Source is not Visual source)
            return;
        var viewer = source.GetSelfAndVisualAncestors().OfType<ScrollViewer>()
                           .FirstOrDefault(v => v.Extent.Height > v.Viewport.Height + 1);
        if (viewer is null)
            return;

        // A new area, or the bar was dragged since: start from where it really is
        if (viewer != _target || !_timer.IsEnabled)
        {
            _target = viewer;
            _goal = viewer.Offset.Y;
        }
        var max = viewer.Extent.Height - viewer.Viewport.Height;
        _goal = Math.Clamp(_goal - e.Delta.Y * AvaloniaStep * Math.Clamp(_speed(), 0.5, 10), 0, max);
        _timer.Start();
        e.Handled = true;
    }

    private void Step()
    {
        if (_target is null)
        {
            _timer.Stop();
            return;
        }
        var y = _target.Offset.Y;
        var next = y + (_goal - y) * 0.3;
        if (Math.Abs(_goal - next) < 0.5)
        {
            next = _goal;
            _timer.Stop();
        }
        _target.Offset = new Vector(_target.Offset.X, next);
    }
}
