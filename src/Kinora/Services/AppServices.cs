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

    /// <summary>Episode pictures and season posters, made when a show page asks for them.</summary>
    public ImageCache Stills { get; } = new(AppPaths.StillCache, 400);

    /// <summary>Photos for the person pages, as TMDB gives them.</summary>
    public FileCache People { get; } = new(AppPaths.PeopleCache, ".jpg");
    public PosterLoader PosterLoader { get; }
    public MovieScanner Scanner { get; }
    public MetadataFetcher Fetcher { get; }
    public SeriesScanner SeriesScanner { get; }
    public SeriesFetcher SeriesFetcher { get; }
    public Kinora.Core.Moving.LibraryMover Mover { get; }
    public Kinora.Core.Moving.LibraryDeleter Deleter { get; }

    public AppServices()
    {
        PosterLoader = new PosterLoader(Posters);
        Scanner = new MovieScanner(Database, Posters);
        Fetcher = new MetadataFetcher(Database, Scanner, Posters);
        SeriesScanner = new SeriesScanner(Database, Posters);
        SeriesFetcher = new SeriesFetcher(Database, SeriesScanner, Posters);
        Mover = new Kinora.Core.Moving.LibraryMover(Database, Scanner, SeriesScanner);
        Deleter = new Kinora.Core.Moving.LibraryDeleter(Database, SeriesScanner);
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
