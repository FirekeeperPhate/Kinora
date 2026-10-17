using System.Text.RegularExpressions;

namespace Kinora.Core.Scanning;

public sealed record ParsedName(string Title, int? Year, string? ImdbId, string? TmdbId);

/// <summary>
/// Guesses title and year from a folder or file name: "Title (2010)", "Title.2010.1080p.BluRay",
/// with the ids Emby, Jellyfin and Radarr put in names ("[imdbid-tt…]", "{tmdb-123}").
/// </summary>
public static partial class FileNameParser
{
    public static ParsedName Parse(string name)
    {
        var imdb = ImdbTag().Match(name) is { Success: true } i ? i.Groups[1].Value
                 : BareImdb().Match(name) is { Success: true } b ? b.Value
                 : null;
        var tmdb = TmdbTag().Match(name) is { Success: true } t ? t.Groups[1].Value : null;

        var text = IdTags().Replace(name, " ");

        int? year = null;
        var paren = ParenYear().Match(text);
        if (paren.Success)
        {
            year = int.Parse(paren.Groups[1].Value);
            text = text[..paren.Index];
        }
        else
        {
            // The last year-like token with something in front of it: "2001 A Space Odyssey 1968",
            // "Blade.Runner.2049.2017.1080p" -> 2017, but "1917" alone stays a title.
            foreach (Match m in LooseYear().Matches(text).Reverse())
            {
                if (m.Index > 0 && text[..m.Index].Trim(' ', '.', '_', '-', '[', '(').Length > 0)
                {
                    year = int.Parse(m.Groups[1].Value);
                    text = text[..m.Index];
                    break;
                }
            }
        }

        var tag = ReleaseTag().Match(text);
        if (tag.Success && tag.Index > 0)
            text = text[..tag.Index];

        // "The.Movie.Name" -> "The Movie Name", but "I.T" or "S.W.A.T." with spaces elsewhere stay
        if (!text.Contains(' ') && text.Count(c => c is '.' or '_') >= 2)
            text = text.Replace('.', ' ').Replace('_', ' ');
        else
            text = text.Replace('_', ' ');

        var title = MultiSpace().Replace(text, " ").Trim(' ', '-', '.', '[', '(');
        return new ParsedName(title.Length > 0 ? title : name, year, imdb, tmdb);
    }

    [GeneratedRegex(@"[\[{](?:imdbid|imdb)[-=](tt\d{6,10})[\]}]", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbTag();

    [GeneratedRegex(@"\btt\d{7,10}\b")]
    private static partial Regex BareImdb();

    [GeneratedRegex(@"[\[{](?:tmdbid|tmdb)[-=](\d+)[\]}]", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbTag();

    [GeneratedRegex(@"[\[{](?:imdbid|imdb|tmdbid|tmdb|tvdbid|tvdb)[-=][^\]}]*[\]}]", RegexOptions.IgnoreCase)]
    private static partial Regex IdTags();

    [GeneratedRegex(@"\((\d{4})\)")]
    private static partial Regex ParenYear();

    [GeneratedRegex(@"(?<=^|[\s._\-\[(])((?:19|20)\d{2})(?=$|[\s._\-\])])")]
    private static partial Regex LooseYear();

    [GeneratedRegex(@"(?<=[\s._\-\[(])(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|bdremux|remux|webrip|web-dl|webdl|web|hdtv|hdrip|dvdrip|dvdscr|dvd|x264|x265|h\.?264|h\.?265|hevc|avc|xvid|divx|10bit|hdr10?|dolby|atmos|dts|ac3|aac|proper|repack|extended|unrated|uncut|remastered|imax|multi|ita|eng)(?=$|[\s._\-\])])", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
