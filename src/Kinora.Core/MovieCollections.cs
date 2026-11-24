using Kinora.Core.Models;

namespace Kinora.Core;

/// <summary>A collection as the library has it: its name and its movies, the oldest first.</summary>
public sealed record MovieCollection<T>(string Name, IReadOnlyList<T> Movies);

/// <summary>
/// The library's movies by collection (the nfo's "set", or TMDB's): one entry per name, whatever
/// its case, the movies in the order they came out.
/// </summary>
public static class MovieCollections
{
    public static List<MovieCollection<T>> Group<T>(IEnumerable<T> items, Func<T, Movie> movie)
    {
        return items.Select(i => (Item: i, Movie: movie(i)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Movie.Collection))
                    .GroupBy(x => x.Movie.Collection!.Trim(), StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new MovieCollection<T>(g.Key,
                        g.OrderBy(x => x.Movie.Year ?? int.MaxValue)
                         .ThenBy(x => x.Movie.Title, StringComparer.CurrentCultureIgnoreCase)
                         .Select(x => x.Item).ToList()))
                    .OrderBy(c => SortName(c.Name), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
    }

    /// <summary>"The Matrix Collection" goes under M, as the titles do.</summary>
    public static string SortName(string name)
    {
        foreach (var article in new[] { "The ", "A ", "An " })
            if (name.StartsWith(article, StringComparison.OrdinalIgnoreCase) && name.Length > article.Length)
                return name[article.Length..];
        return name;
    }

    /// <summary>Is this the collection with that name? Case and spaces around don't count.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
