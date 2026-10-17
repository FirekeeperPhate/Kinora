namespace Kinora.Core.Models;

/// <summary>A TV show: its folder in a TV root, with the seasons and episodes below it.</summary>
public sealed class Series
{
    public long Id { get; set; }
    public long RootId { get; set; }

    /// <summary>The show's folder, relative to the root.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Changes when the show folder or any season folder changes.</summary>
    public long Stamp { get; set; }

    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? SortTitle { get; set; }
    public int? Year { get; set; }
    public int? EndYear { get; set; }
    public string? Plot { get; set; }
    public double? Rating { get; set; }
    public string? Mpaa { get; set; }
    public string? Premiered { get; set; }
    public string? Status { get; set; }
    public string? ImdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? TvdbId { get; set; }
    public string Genres { get; set; } = "";
    public string Studios { get; set; } = "";

    public string? PosterFile { get; set; }
    public string? FanartFile { get; set; }
    public string? LogoFile { get; set; }
    public string? ThumbKey { get; set; }
    public string? OnlinePoster { get; set; }
    public string? OnlineFanart { get; set; }
    public string? OnlineLogo { get; set; }

    public long DateAdded { get; set; }

    /// <summary>When its newest episode arrived: "recently added" is about episodes.</summary>
    public long LastEpisodeAdded { get; set; }

    public int EpisodeCount { get; set; }
    public int WatchedCount { get; set; }

    public bool NeedsMatch => string.IsNullOrEmpty(ImdbId) && string.IsNullOrEmpty(TmdbId) && string.IsNullOrEmpty(TvdbId);
    public IReadOnlyList<string> GenreList => Movie.Split(Genres);
    public IReadOnlyList<string> StudioList => Movie.Split(Studios);
}

public sealed class Season
{
    public long SeriesId { get; set; }
    public int Number { get; set; }
    public string? PosterFile { get; set; }
    public string? ThumbKey { get; set; }
    public string? OnlinePoster { get; set; }
}

public sealed class Episode
{
    public long Id { get; set; }
    public long SeriesId { get; set; }

    /// <summary>Null when neither the name nor an nfo says it (shown under "Other").</summary>
    public int? Season { get; set; }
    public int? Number { get; set; }

    /// <summary>For a file holding several episodes (S01E01-E02).</summary>
    public int? NumberEnd { get; set; }

    public string Title { get; set; } = "";
    public string? Plot { get; set; }
    public string? Aired { get; set; }
    public int? Runtime { get; set; }
    public double? Rating { get; set; }
    public string Directors { get; set; } = "";
    public string? ImdbId { get; set; }

    public string VideoFile { get; set; } = "";
    public long VideoSize { get; set; }
    public string? ThumbFile { get; set; }
    public string? OnlineStill { get; set; }

    public string? Resolution { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public string Subtitles { get; set; } = "";

    public bool Watched { get; set; }
    public long DateAdded { get; set; }

    public string Code => Season is { } s && Number is { } n
        ? NumberEnd is { } e && e != n ? $"S{s:00}E{n:00}-E{e:00}" : $"S{s:00}E{n:00}"
        : "";
}
