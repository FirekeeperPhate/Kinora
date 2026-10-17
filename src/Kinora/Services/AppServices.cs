using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Online;
using Kinora.Core.Scanning;
using Kinora.Core.Settings;

namespace Kinora.Services;

/// <summary>Everything the view models share, built once at startup.</summary>
public sealed class AppServices
{
    public const int PosterWidth = 320;
    public const int BackdropWidth = 1920;

    public AppSettings Settings { get; } = AppSettings.Load(AppPaths.Settings);
    public LibraryDatabase Database { get; } = new(AppPaths.Database);
    public ImageCache Posters { get; } = new(AppPaths.PosterCache, PosterWidth);
    public ImageCache Backdrops { get; } = new(AppPaths.BackdropCache, BackdropWidth, quality: 85);
    public FileCache Logos { get; } = new(AppPaths.LogoCache, ".png");
    public PosterLoader PosterLoader { get; }
    public MovieScanner Scanner { get; }
    public MetadataFetcher Fetcher { get; }

    public AppServices()
    {
        PosterLoader = new PosterLoader(Posters);
        Scanner = new MovieScanner(Database, Posters);
        Fetcher = new MetadataFetcher(Database, Scanner, Posters);
    }

    /// <summary>Null without a key: Kinora then works with the folders alone.</summary>
    public TmdbClient? CreateTmdbClient() =>
        Settings.HasTmdbKey ? new TmdbClient(Settings.TmdbKey, Settings.MetadataLanguage) : null;

    public void SaveSettings()
    {
        try
        {
            Settings.Save(AppPaths.Settings);
        }
        catch (IOException)
        {
        }
    }
}
