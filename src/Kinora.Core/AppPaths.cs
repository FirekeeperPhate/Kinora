namespace Kinora.Core;

/// <summary>
/// Where Kinora keeps its own files: the database, the settings and the image cache. Always on
/// the local disk, never next to the media. KINORA_DATA moves everything elsewhere (handy to try
/// things without touching the real library).
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Resolve();

    public static string Database => Path.Combine(DataDir, "kinora.db");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string PosterCache => Path.Combine(DataDir, "cache", "posters");
    public static string BackdropCache => Path.Combine(DataDir, "cache", "backdrops");
    public static string LogoCache => Path.Combine(DataDir, "cache", "logos");
    public static string StillCache => Path.Combine(DataDir, "cache", "stills");

    private static string Resolve()
    {
        var custom = Environment.GetEnvironmentVariable("KINORA_DATA");
        var dir = !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                                     Environment.SpecialFolderOption.Create), "Kinora");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
