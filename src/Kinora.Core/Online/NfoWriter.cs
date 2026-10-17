using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Kinora.Core.Online;

/// <summary>A Kodi-style movie.nfo from TMDB data, readable by Kodi, Emby, Jellyfin and Kinora.</summary>
public static class NfoWriter
{
    public static string Write(TmdbMovie t)
    {
        var movie = new XElement("movie",
            Text("title", t.Title),
            Text("originaltitle", t.OriginalTitle),
            Text("year", t.Year?.ToString(CultureInfo.InvariantCulture)),
            Text("plot", t.Overview),
            Text("tagline", t.Tagline),
            Text("runtime", t.Runtime?.ToString(CultureInfo.InvariantCulture)),
            t.VoteAverage is { } rating
                ? new XElement("ratings", new XElement("rating", new XAttribute("name", "themoviedb"), new XAttribute("default", "true"),
                                                       new XElement("value", rating.ToString("0.0", CultureInfo.InvariantCulture))))
                : null,
            Text("mpaa", t.Certification),
            Text("premiered", t.ReleaseDate),
            new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), t.Id),
            t.ImdbId is null ? null : new XElement("uniqueid", new XAttribute("type", "imdb"), t.ImdbId),
            t.ImdbId is null ? null : new XElement("imdbid", t.ImdbId),
            new XElement("tmdbid", t.Id),
            t.Genres.Select(g => new XElement("genre", g)),
            t.Studios.Select(s => new XElement("studio", s)),
            t.Countries.Select(c => new XElement("country", c)),
            t.Directors.Select(d => new XElement("director", d)),
            t.Collection is null ? null : new XElement("set", new XElement("name", t.Collection)),
            t.Cast.Select((c, i) => new XElement("actor",
                new XElement("name", c.Name),
                Text("role", c.Character),
                new XElement("order", i))));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), movie);
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            doc.Save(writer);
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static XElement? Text(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);
}
