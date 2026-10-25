namespace Kinora.Core.Scanning;

/// <summary>What a .nfo file says. Everything is optional: nfo files in the wild are partial.</summary>
public sealed class NfoData
{
    public string? Title { get; set; }
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
    public string? TvdbId { get; set; }
    public string? Status { get; set; }

    /// <summary>Episode nfo only.</summary>
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? Collection { get; set; }
    public bool? Watched { get; set; }
    public DateTime? DateAdded { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<string> Directors { get; set; } = [];
    public List<string> Countries { get; set; } = [];
    public List<NfoActor> Actors { get; set; } = [];

    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }

    /// <summary>
    /// Fills what this one lacks from <paramref name="other"/>. Used when a folder has both
    /// movie.nfo and &lt;video&gt;.nfo: the newer file wins, the older one fills the gaps
    /// (often the newer one has no cast).
    /// </summary>
    public void FillFrom(NfoData other)
    {
        Title ??= other.Title;
        OriginalTitle ??= other.OriginalTitle;
        SortTitle ??= other.SortTitle;
        Year ??= other.Year;
        Plot ??= other.Plot;
        Tagline ??= other.Tagline;
        Runtime ??= other.Runtime;
        Rating ??= other.Rating;
        CriticRating ??= other.CriticRating;
        Mpaa ??= other.Mpaa;
        Premiered ??= other.Premiered;
        ImdbId ??= other.ImdbId;
        TmdbId ??= other.TmdbId;
        TvdbId ??= other.TvdbId;
        Status ??= other.Status;
        Season ??= other.Season;
        Episode ??= other.Episode;
        Collection ??= other.Collection;
        Watched ??= other.Watched;
        DateAdded ??= other.DateAdded;
        if (Genres.Count == 0) Genres = other.Genres;
        if (Studios.Count == 0) Studios = other.Studios;
        if (Directors.Count == 0) Directors = other.Directors;
        if (Countries.Count == 0) Countries = other.Countries;
        if (Actors.Count == 0) Actors = other.Actors;
        if (VideoWidth is null)
        {
            VideoWidth = other.VideoWidth;
            VideoHeight = other.VideoHeight;
            VideoCodec = other.VideoCodec;
        }
        if (AudioCodec is null)
        {
            AudioCodec = other.AudioCodec;
            AudioChannels = other.AudioChannels;
        }
    }
}

/// <param name="Thumb">The photo the nfo names: a web address, or a file (Kodi puts them in .actors).</param>
public sealed record NfoActor(string Name, string? Role, int Order, string? TmdbId = null, string? Thumb = null);
