using System.Diagnostics;

namespace Kinora.Core.Platform;

/// <summary>Opens files with the player the OS associates with them, and shows them in the file manager.</summary>
public static class Launcher
{
    public static void Open(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        else if (OperatingSystem.IsMacOS())
            Run("open", path);
        else
            Run("xdg-open", path);
    }

    public static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(ExactPath.ForIo(path)))
                psi.Arguments = $"/select,\"{path}\"";
            else
                psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(ExactPath.ForIo(path)))
                Run("open", "-R", path);
            else
                Run("open", path);
        }
        else
        {
            Run("xdg-open", File.Exists(ExactPath.ForIo(path)) ? Path.GetDirectoryName(path)! : path);
        }
    }

    public static void OpenUrl(string url) => Open(url);

    private static void Run(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}
