using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Kinora.ViewModels;

namespace Kinora.Views;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        GridScroller.SizeChanged += (_, _) => UpdateViewport();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is not { } vm)
            return;
        vm.Dialogs = this;
        vm.ScrollToTopRequested += () => GridScroller.Offset = new Vector(0, 0);
        UpdateViewport();
        vm.Initialize();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Vm is { } vm)
        {
            var settings = vm.Services.Settings;
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
        }
        base.OnClosing(e);
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
        if (e.Key == Key.Escape && vm.Detail is not null)
        {
            vm.CloseDetailCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.Detail is null)
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
    }

    // ---- IDialogs ----

    public async Task ShowSettingsAsync(SettingsViewModel settings)
    {
        var window = new SettingsWindow { DataContext = settings };
        await window.ShowDialog(this);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        ConfirmDialog.ShowAsync(GetTopmostWindow(), title, message, confirmText);

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
