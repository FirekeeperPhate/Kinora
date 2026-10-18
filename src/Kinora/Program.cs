using Avalonia;

namespace Kinora;

internal sealed class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
        // Held while Kinora runs: the Windows installer asks to close it before replacing files
        using var running = OperatingSystem.IsWindows() ? new Mutex(false, "Kinora.Running") : null;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
