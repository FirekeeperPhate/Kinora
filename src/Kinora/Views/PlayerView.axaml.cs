using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kinora.ViewModels;

namespace Kinora.Views;

/// <summary>
/// The built-in player's page: VLC's window below, the controls in a window of their own above it, sized to
/// the video. The controls and the cursor go after three seconds without moving the mouse.
/// </summary>
public partial class PlayerView : UserControl
{
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _hideTimer;
    private PlayerViewModel? _vm;
    private Window? _window;
    private PlayerOverlayWindow? _overlay;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _menuOpen;
    private Point _lastPointer;

    public PlayerView()
    {
        InitializeComponent();
        // The controls live in a window of their own (see PlayerOverlayWindow)
        ((Panel)Content!).Children.Remove(OverlayRoot);
        _hideTimer = new DispatcherTimer { Interval = HideAfter };
        _hideTimer.Tick += (_, _) => HideControls();

        Surface.Ready += () => Dispatcher.UIThread.Post(() => _vm?.Start());
        Surface.SizeChanged += (_, _) => FitOverlay();

        OverlayRoot.PointerMoved += (_, e) =>
        {
            // A move of a pixel or two (a table knock) doesn't wake the controls
            var p = e.GetPosition(OverlayRoot);
            if (Math.Abs(p.X - _lastPointer.X) + Math.Abs(p.Y - _lastPointer.Y) > 3)
                ShowControls();
            _lastPointer = p;
        };
        OverlayRoot.PointerWheelChanged += (_, e) =>
        {
            _vm?.ChangeVolume(e.Delta.Y > 0 ? 5 : -5);
            ShowControls();
            e.Handled = true;
        };
        OverlayRoot.Tapped += (_, e) =>
        {
            if (IsOnVideo(e))
                _vm?.PlayPause();
        };
        OverlayRoot.DoubleTapped += (_, e) =>
        {
            if (!IsOnVideo(e))
                return;
            _vm?.PlayPause();   // the first click paused: undo it
            ToggleFullScreen();
        };

        // The seek bar: while it is held the timer leaves it alone, the time shown follows it
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => { if (_vm is { } vm) vm.IsSeeking = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => EndSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerCaptureLostEvent, (_, _) => EndSeek(), RoutingStrategies.Bubble, handledEventsToo: true);
        SeekBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && _vm is { IsSeeking: true } vm)
                vm.PreviewSeek();
        };

        foreach (var button in new[] { AudioButton, SubtitlesButton })
        {
            button.Flyout!.Opened += (_, _) => { _menuOpen = true; ShowControls(); };
            button.Flyout!.Closed += (_, _) => { _menuOpen = false; ShowControls(); };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.FullScreenToggleRequested -= ToggleFullScreen;
        _vm = DataContext as PlayerViewModel;
        if (_vm is null)
            return;
        Surface.Player = _vm.Player;
        _vm.FullScreenToggleRequested += ToggleFullScreen;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayerViewModel.IsPlaying))
                ShowControls();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null)
            return;
        _window.PositionChanged += OnWindowMoved;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _overlay = new PlayerOverlayWindow(OverlayRoot) { DataContext = DataContext };
        // A click on the controls moves Avalonia's keyboard focus over there: the keys still
        // work, and the focus goes back to Kinora's window right after
        _overlay.AddHandler(KeyDownEvent, (_, args) => args.Handled = HandleKey(args), RoutingStrategies.Tunnel);
        _overlay.AddHandler(PointerReleasedEvent, (_, _) =>
            Dispatcher.UIThread.Post(() => { if (!_menuOpen) _window?.Focus(); }), RoutingStrategies.Tunnel, handledEventsToo: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (_overlay is null || _window is null)
                return;
            FitOverlay();
            _overlay.Show(_window);
            FitOverlay();
            ShowControls();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _hideTimer.Stop();
        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay.Content = null;
            _overlay = null;
        }
        if (_window is not null)
        {
            _window.PositionChanged -= OnWindowMoved;
            _window.PropertyChanged -= OnWindowPropertyChanged;
            if (_window.WindowState == WindowState.FullScreen)
                _window.WindowState = _stateBeforeFullScreen;
            _window.Cursor = Cursor.Default;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowMoved(object? sender, PixelPointEventArgs e) => FitOverlay();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            if (_vm is not null)
                _vm.IsFullScreen = _window?.WindowState == WindowState.FullScreen;
            // Going full screen the window has its new size only after layout
            FitOverlay();
            Dispatcher.UIThread.Post(FitOverlay, DispatcherPriority.Background);
        }
    }

    /// <summary>The controls' window covers the video exactly.</summary>
    private void FitOverlay()
    {
        var size = Surface.Bounds.Size;
        if (_overlay is null || size.Width <= 0 || size.Height <= 0 || !Surface.IsAttachedToVisualTree())
            return;
        _overlay.Width = size.Width;
        _overlay.Height = size.Height;
        _overlay.Position = Surface.PointToScreen(new Point(0, 0));
    }

    /// <summary>A click on the picture (the gradients included), not on a button or a bar.</summary>
    private static bool IsOnVideo(RoutedEventArgs e)
    {
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button or Slider or MenuItem)
                return false;
            if (v is Grid { Name: "OverlayRoot" })
                return true;
        }
        return false;
    }

    private void EndSeek()
    {
        if (_vm is not { IsSeeking: true } vm)
            return;
        vm.SeekToProgress();
        vm.IsSeeking = false;
    }

    // ---- controls that come and go ----

    private void ShowControls()
    {
        if (_vm is null)
            return;
        _vm.ControlsVisible = true;
        OverlayRoot.Cursor = Cursor.Default;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideControls()
    {
        _hideTimer.Stop();
        if (_vm is null || !_vm.IsPlaying || _menuOpen || BottomBar.IsPointerOver || _vm.Message is not null)
            return;
        _vm.ControlsVisible = false;
        OverlayRoot.Cursor = new Cursor(StandardCursorType.None);
    }

    // ---- full screen ----

    private void ToggleFullScreen()
    {
        if (_window is null)
            return;
        if (_window.WindowState == WindowState.FullScreen)
        {
            _window.WindowState = _stateBeforeFullScreen;
        }
        else
        {
            _stateBeforeFullScreen = _window.WindowState;
            _window.WindowState = WindowState.FullScreen;
        }
    }

    // ---- keyboard ----

    /// <summary>The player's keys; the window passes them on while the player is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return false;
        // With a track menu open, Esc closes the menu, not the player
        if (_menuOpen && e.Key == Key.Escape)
        {
            AudioButton.Flyout?.Hide();
            SubtitlesButton.Flyout?.Hide();
            return true;
        }
        switch (e.Key)
        {
            case Key.Space or Key.K or Key.MediaPlayPause:
                vm.PlayPause();
                break;
            case Key.Left or Key.J:
                vm.SeekBy(-10);
                break;
            case Key.Right or Key.L:
                vm.SeekBy(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 60 : 30);
                break;
            case Key.Up:
                vm.ChangeVolume(5);
                break;
            case Key.Down:
                vm.ChangeVolume(-5);
                break;
            case Key.M:
                vm.ToggleMute();
                break;
            case Key.F or Key.F11:
                ToggleFullScreen();
                break;
            case Key.A:
                vm.CycleTrack(subtitles: false);
                break;
            case Key.S:
                vm.CycleTrack(subtitles: true);
                break;
            case Key.Escape or Key.Back or Key.BrowserBack:
                if (_window?.WindowState == WindowState.FullScreen && e.Key == Key.Escape)
                    ToggleFullScreen();
                else
                    vm.Close();
                break;
            default:
                return false;
        }
        ShowControls();
        return true;
    }
}
