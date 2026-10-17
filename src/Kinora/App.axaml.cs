using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Kinora.Core.Settings;
using Kinora.Services;
using Kinora.ViewModels;
using Kinora.Views;

namespace Kinora;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            ApplyTheme(services.Settings.Theme);
            var window = new MainWindow { DataContext = new MainWindowViewModel(services) };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => services.SaveSettings();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(ThemeChoice theme)
    {
        if (Current is null)
            return;
        Current.RequestedThemeVariant = theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
