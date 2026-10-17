namespace Kinora.Core.Models;

/// <summary>
/// A movie as the library keeps it. Paths are relative to the root; list fields are stored
/// joined with <see cref="ListSeparator"/> and read through the <c>…List</c> helpers.
/// </summary>
public sealed class Movie
{
    public const char ListSeparator = '|';

    public long Id { get; set; }
    public long RootId { get; set; }

    /// <summary>The movie's own folder, or "" when the video sits loose in the root.</summary>
    public string Folder { get; set; } = "";
    public string VideoFile { get; set; } = "";
    public long VideoSize { get; set; }

    /// <summary>What the scanner compares to decide whether to look inside again.</summary>
    public long Stamp { get; set; }

    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? SortTitle { get; set; }
    public int? Year { get; set; }
    public string? Plot { get; set; }
    public string? Tagline { get; set; }
    public int? Runtime { get; set; }
    public double? Rating { get; set; }
    public int? CriticRating { get; set; }
    public string? Mpaa { get; set; }
    public string? Premiered { get; set; }
    public string? ImdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? Collection { get; set; }
    public string Genres { get; set; } = "";
    public string Studios { get; set; } = "";
    public string Directors { get; set; } = "";
    public string Countries { get; set; } = "";

    public string? PosterFile { get; set; }
    public string? FanartFile { get; set; }
    public string? LogoFile { get; set; }
    public string? ThumbKey { get; set; }

    /// <summary>TMDB picture paths, filled only where the folder has no picture of that kind.</summary>
    public string? OnlinePoster { get; set; }
    public string? OnlineFanart { get; set; }
    public string? OnlineLogo { get; set; }

    public string? Resolution { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public string Subtitles { get; set; } = "";

    /// <summary>Other copies of the same movie in its folder (another cut, another quality).</summary>
    public string OtherVideos { get; set; } = "";

    public bool Watched { get; set; }
    public long DateAdded { get; set; }

    public bool NeedsMatch => string.IsNullOrEmpty(ImdbId) && string.IsNullOrEmpty(TmdbId);

    public IReadOnlyList<string> GenreList => Split(Genres);
    public IReadOnlyList<string> StudioList => Split(Studios);
    public IReadOnlyList<string> DirectorList => Split(Directors);
    public IReadOnlyList<string> CountryList => Split(Countries);
    public IReadOnlyList<string> SubtitleList => Split(Subtitles);
    public IReadOnlyList<string> OtherVideoList => Split(OtherVideos);

    public static string Join(IEnumerable<string> values) =>
        string.Join(ListSeparator, values.Select(v => v.Replace(ListSeparator, '/').Trim())
                                         .Where(v => v.Length > 0)
                                         .Distinct(StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Split(string joined) =>
        joined.Length == 0 ? [] : joined.Split(ListSeparator);
}

public sealed class CastMember
{
    public long MovieId { get; set; }
    public int Ord { get; set; }
    public string Name { get; set; } = "";
    public string? Role { get; set; }
}
