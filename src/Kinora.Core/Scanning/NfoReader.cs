using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kinora.Core.Scanning;

/// <summary>
/// Reads Kodi / Emby / Jellyfin style .nfo files. It never throws on a bad file: Kodi accepts
/// nfo files that are only a link (an IMDb or TMDB URL), or XML followed by a link, so whatever
/// can't be parsed as XML is still searched for ids.
/// </summary>
public static partial class NfoReader
{
    public static NfoData? Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(text);
    }

    public static NfoData Parse(string text)
    {
        var data = TryParseXml(text) ?? new NfoData();
        // Ids from a bare link, or from a link after the XML
        data.ImdbId ??= ImdbIdInText().Match(text) is { Success: true } m ? m.Value : null;
        data.TmdbId ??= TmdbUrl().Match(text) is { Success: true } t ? t.Groups[1].Value : null;
        return data;
    }

    private static NfoData? TryParseXml(string text)
    {
        var start = text.IndexOf('<');
        if (start < 0)
            return null;
        XElement root;
        var xml = CutXml(text, start);
        try
        {
            root = XDocument.Parse(xml).Root!;
        }
        catch (System.Xml.XmlException)
        {
            // A file holding two episodes (S01E01-E02) has one <episodedetails> per episode: two
            // roots. The first one describes the file well enough.
            try
            {
                var body = XmlDeclaration().Replace(xml, "");
                root = XDocument.Parse("<kinora>" + body + "</kinora>").Root!.Elements().First();
            }
            catch (Exception e) when (e is System.Xml.XmlException or InvalidOperationException)
            {
                return null;
            }
        }

        var data = new NfoData
        {
            Title = Str(root, "title"),
            OriginalTitle = Str(root, "originaltitle"),
            SortTitle = Str(root, "sorttitle"),
            Year = Int(Str(root, "year")),
            Plot = Str(root, "plot") ?? Str(root, "outline"),
            Tagline = Str(root, "tagline"),
            Runtime = Int(Str(root, "runtime")),
            Rating = Rating(root),
            CriticRating = Int(Str(root, "criticrating")),
            Mpaa = Str(root, "mpaa"),
            Premiered = Date(Str(root, "premiered") ?? Str(root, "releasedate") ?? Str(root, "aired")),
            ImdbId = UniqueId(root, "imdb") ?? Imdb(Str(root, "imdbid")) ?? Imdb(Str(root, "imdb_id")) ?? Imdb(Str(root, "id")),
            TvdbId = UniqueId(root, "tvdb") ?? Digits(Str(root, "tvdbid")),
            Status = Str(root, "status"),
            Season = Int(Str(root, "season")),
            Episode = Int(Str(root, "episode")),
            TmdbId = UniqueId(root, "tmdb") ?? Digits(Str(root, "tmdbid")),
            Collection = Str(root.Element("set"), "name") ?? StrOnlyText(root.Element("set")),
            Watched = Watched(root),
            DateAdded = DateTime.TryParse(Str(root, "dateadded"), CultureInfo.InvariantCulture,
                                          DateTimeStyles.AssumeLocal, out var added) ? added : null,
            Genres = List(root, "genre"),
            Studios = List(root, "studio"),
            Directors = List(root, "director"),
            Countries = List(root, "country"),
        };

        var order = 0;
        foreach (var actor in root.Elements("actor"))
        {
            var name = Str(actor, "name");
            if (name is null)
                continue;
            data.Actors.Add(new NfoActor(name, Str(actor, "role"), Int(Str(actor, "order")) ?? order, Digits(Str(actor, "tmdbid"))));
            order++;
        }
        data.Actors.Sort((a, b) => a.Order.CompareTo(b.Order));

        var streams = root.Element("fileinfo")?.Element("streamdetails");
        if (streams?.Element("video") is { } video)
        {
            data.VideoWidth = Int(Str(video, "width"));
            data.VideoHeight = Int(Str(video, "height"));
            data.VideoCodec = Str(video, "codec");
        }
        if (streams?.Element("audio") is { } audio)
        {
            data.AudioCodec = Str(audio, "codec");
            data.AudioChannels = Int(Str(audio, "channels"));
        }
        return data;
    }

    /// <summary>Keeps the XML part only: Kodi allows a URL line after the closing tag.</summary>
    private static string CutXml(string text, int start)
    {
        var rootName = RootName().Match(text, start);
        if (!rootName.Success)
            return text[start..];
        var close = "</" + rootName.Groups[1].Value + ">";
        var end = text.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? text[start..] : text[start..(end + close.Length)];
    }

    private static string? Str(XElement? parent, string name)
    {
        var value = parent?.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? StrOnlyText(XElement? element)
    {
        if (element is null || element.HasElements)
            return null;
        var value = element.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static List<string> List(XElement root, string name) =>
        root.Elements(name)
            .SelectMany(e => e.Value.Split(" / "))   // some tools write "Drama / War" in one tag
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int? Int(string? value)
    {
        if (value is null)
            return null;
        var m = LeadingNumber().Match(value);
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static double? Dbl(string? value) =>
        double.TryParse(value?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? Rating(XElement root)
    {
        // Kodi v17+: <ratings><rating name="imdb" default="true"><value>6.5</value></rating></ratings>
        var ratings = root.Element("ratings")?.Elements("rating").ToList();
        if (ratings is { Count: > 0 })
        {
            var chosen = ratings.FirstOrDefault(r => (string?)r.Attribute("default") == "true") ?? ratings[0];
            if (Dbl(Str(chosen, "value")) is { } v)
                return v;
        }
        var direct = root.Element("rating");
        return direct is { HasElements: false } ? Dbl(direct.Value.Trim()) : null;
    }

    private static string? UniqueId(XElement root, string type)
    {
        var value = root.Elements("uniqueid")
                        .FirstOrDefault(e => string.Equals((string?)e.Attribute("type"), type, StringComparison.OrdinalIgnoreCase))
                        ?.Value.Trim();
        return type == "imdb" ? Imdb(value) : Digits(value);
    }

    private static string? Imdb(string? value) =>
        value is not null && ImdbIdExact().IsMatch(value) ? value : null;

    private static string? Digits(string? value) =>
        value is not null && value.Length > 0 && value.All(char.IsAsciiDigit) && value != "0" ? value : null;

    private static string? Date(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd") : null;

    private static bool? Watched(XElement root)
    {
        if (Str(root, "watched") is { } w && bool.TryParse(w, out var b))
            return b;
        return Int(Str(root, "playcount")) is { } count ? count > 0 : null;
    }

    [GeneratedRegex(@"<\?xml[^>]*\?>")]
    private static partial Regex XmlDeclaration();

    [GeneratedRegex(@"<\s*([A-Za-z_][\w.-]*)")]
    private static partial Regex RootName();

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\btt\d{6,10}\b")]
    private static partial Regex ImdbIdInText();

    [GeneratedRegex(@"^tt\d{6,10}$")]
    private static partial Regex ImdbIdExact();

    [GeneratedRegex(@"themoviedb\.org/movie/(\d+)")]
    private static partial Regex TmdbUrl();
}
