using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
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
    public PosterLoader PosterLoader { get; }
    public MovieScanner Scanner { get; }

    public AppServices()
    {
        PosterLoader = new PosterLoader(Posters);
        Scanner = new MovieScanner(Database, Posters);
    }

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
