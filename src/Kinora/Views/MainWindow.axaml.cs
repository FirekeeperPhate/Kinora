using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Kinora.ViewModels;

namespace Kinora.Views;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        GridScroller.SizeChanged += (_, _) => UpdateViewport();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        new WheelScroller(() => Vm?.Services.Settings.ScrollSpeed ?? 3).Attach(this);
        // A poster's click arrives as a command, without the keys: note them on the way down
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (Vm is { } vm)
                vm.LastModifiers = e.KeyModifiers;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnMouseSideButton, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        PositionChanged += OnPositionChanged;
    }

    private DateTime _sideButtonAt;

    /// <summary>The mouse's back and forward buttons, as in a browser; in the player, back closes it.</summary>
    private void OnMouseSideButton(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed) || Vm is not { } vm)
            return;
        e.Handled = true;
        _sideButtonAt = DateTime.UtcNow;
        Travel(vm, back: kind == PointerUpdateKind.XButton1Pressed);
    }

    private static void Travel(MainWindowViewModel vm, bool back)
    {
        if (vm.Player is { } player)
        {
            if (back)
                player.Close();
        }
        else if (back)
            vm.GoBack();
        else
            vm.GoForward();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Before the window is shown, so it opens at the size it was closed with
        if (Vm is not { } vm)
            return;
        var settings = vm.Services.Settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        // Where it was closed, if that is still on a screen (not on a monitor unplugged since);
        // maximized, it opens maximized on that screen
        if (settings is { WindowX: { } x, WindowY: { } y } && OnAScreen(new PixelPoint(x, y)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            _normalPosition = Position;
        }
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Enough of the title bar on a screen to grab it.</summary>
    private bool OnAScreen(PixelPoint corner)
    {
        var screens = Screens.All;
        if (screens.Count == 0)
            return true;   // can't tell: as it was
        return screens.Any(s => s.WorkingArea.Contains(new PixelPoint(corner.X + 120, corner.Y + 16))
                                || s.WorkingArea.Contains(new PixelPoint(corner.X + 40, corner.Y + 16)));
    }

    // The window's place when not maximized: kept while it moves, since maximized or in full
    // screen it is somewhere else. Windows moves it to the corner just before it says maximized:
    // a move right before that doesn't count.
    private PixelPoint? _normalPosition;
    private PixelPoint? _positionBefore;
    private DateTime _movedAt;

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState != WindowState.Normal)
            return;
        _positionBefore = _normalPosition;
        _normalPosition = e.Point;
        _movedAt = DateTime.UtcNow;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is not { } vm)
            return;
        vm.Dialogs = this;
        vm.ScrollToTopRequested += () => GridScroller.Offset = new Vector(0, 0);
        vm.ShutdownRequested += Close;
        vm.FullScreenToggleRequested += ToggleFullScreen;
        UpdateViewport();
        vm.Initialize();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.Player?.Close();   // keeps the position, stops VLC before its window goes
            var settings = vm.Services.Settings;
            settings.WindowMaximized = WindowState == WindowState.Maximized
                                       || WindowState == WindowState.FullScreen && _beforeFullScreen == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
                _normalPosition = Position;
            }
            if (_normalPosition is { } p)
            {
                settings.WindowX = p.X;
                settings.WindowY = p.Y;
            }
        }
        base.OnClosing(e);
    }

    // ---- full screen ----

    private WindowState _beforeFullScreen = WindowState.Normal;

    /// <summary>In and out of full screen, back to maximized or not as it was (the button at the top right, F11, the player's F).</summary>
    public void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _beforeFullScreen;
        }
        else
        {
            _beforeFullScreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState != WindowState.Normal
            && DateTime.UtcNow - _movedAt < TimeSpan.FromMilliseconds(300))
            _normalPosition = _positionBefore ?? _normalPosition;
        if (change.Property == WindowStateProperty && Vm is { } vm)
            vm.IsFullScreen = WindowState == WindowState.FullScreen;
    }

    private void UpdateViewport()
    {
        // The rows sit inside the ItemsControl's margin; scrollbars float over the content
        var margin = GridItems.Margin;
        var width = GridScroller.Bounds.Width - margin.Left - margin.Right;
        if (width > 0)
            Vm?.SetViewport(width, RenderScaling);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        // A keyboard's back and forward keys; the mouse's buttons may send one too, after their click
        if (e.Key is Key.BrowserBack or Key.BrowserForward)
        {
            if (DateTime.UtcNow - _sideButtonAt > TimeSpan.FromMilliseconds(600))
                Travel(vm, back: e.Key == Key.BrowserBack);
            e.Handled = true;
            return;
        }
        // Alt+Left and Alt+Right, as in a browser (before the player, whose Left and Right seek)
        if (e.Key is Key.Left or Key.Right && e.KeyModifiers == KeyModifiers.Alt)
        {
            Travel(vm, back: e.Key == Key.Left);
            e.Handled = true;
            return;
        }
        // While a video plays, the keys are the player's
        if (vm.Player is not null)
        {
            if (this.GetVisualDescendants().OfType<PlayerView>().FirstOrDefault() is { } player)
                e.Handled = player.HandleKey(e);
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Home && e.KeyModifiers == KeyModifiers.Alt && !vm.ShowLibrary)
        {
            vm.GoToLibrary();   // as a browser's Home
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !vm.ShowLibrary)
        {
            vm.GoBack();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.ShowLibrary)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsFocused && vm.SearchText.Length > 0)
        {
            vm.SearchText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.HasSelection)
        {
            vm.ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.ShowLibrary && !SearchBox.IsFocused)
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ToggleFullScreen();   // nothing else for Esc to do in the library: out of full screen
            e.Handled = true;
        }
    }

    /// <summary>The "•••" on a poster opens the same menu as a right click.</summary>
    private void OnCardMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control control)
            return;
        var card = control.GetVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("card"));
        card?.ContextMenu?.Open(card);
        e.Handled = true;
    }

    // ---- IDialogs ----

    public async Task ShowSettingsAsync(SettingsViewModel settings)
    {
        var window = new SettingsWindow { DataContext = settings };
        await window.ShowDialog(this);
    }

    public async Task<string?> ShowIdentifyAsync(IdentifyViewModel identify)
    {
        var window = new IdentifyWindow { DataContext = identify };
        await window.ShowDialog(this);
        return identify.ChosenId;
    }

    public async Task ShowSubtitlesAsync(SubtitlesViewModel subtitles)
    {
        var window = new SubtitlesWindow { DataContext = subtitles };
        await window.ShowDialog(this);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) =>
        ConfirmDialog.ShowAsync(GetTopmostWindow(), title, message, confirmText, destructive);

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await GetTopmostWindow().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>A dialog opened from the settings window belongs to the settings window.</summary>
    private Window GetTopmostWindow() =>
        OwnedWindows.LastOrDefault(w => w.IsVisible) ?? this;
}
