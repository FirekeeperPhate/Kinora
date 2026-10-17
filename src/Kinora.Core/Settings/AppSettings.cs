using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kinora.Core.Settings;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public enum SortOrder
{
    Title,
    Year,
    DateAdded,
    Rating,
}

/// <summary>Preferences, kept as JSON next to the database. The library itself is in the database.</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>Off by default: Kinora reads the media folders and leaves them as they are.</summary>
    public bool WriteToMediaFolders { get; set; }

    public bool ScanOnStartup { get; set; } = true;

    /// <summary>The TMDB key, as <see cref="SecretText"/> keeps it. Use <see cref="TmdbKey"/>.</summary>
    public string? TmdbKeyStored { get; set; }

    [JsonIgnore]
    public string TmdbKey
    {
        get => SecretText.Unprotect(TmdbKeyStored);
        set => TmdbKeyStored = SecretText.Protect(value.Trim());
    }

    [JsonIgnore]
    public bool HasTmdbKey => !string.IsNullOrEmpty(TmdbKeyStored) && TmdbKey.Length > 0;

    /// <summary>Language of the plots, titles and pictures asked of TMDB.</summary>
    public string MetadataLanguage { get; set; } = "en-US";

    /// <summary>After a scan, ask TMDB about what is missing.</summary>
    public bool FetchAfterScan { get; set; } = true;
    public SortOrder Sort { get; set; } = SortOrder.Title;
    public bool SortDescending { get; set; }
    public double CardWidth { get; set; } = 170;

    /// <summary>The library opens on TV shows rather than movies.</summary>
    public bool ShowTvShows { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // the key is in there
        File.Move(temp, path, overwrite: true);
    }
}
