using Kinora.Core.Models;

namespace Kinora.Core;

/// <summary>A movie of the library related to another, and why.</summary>
public sealed record RelatedMovie(Movie Movie, string Reason);

/// <summary>
/// The movies of the library related to one: the rest of its collection in order, then the other
/// movies of its directors, then those sharing at least two of its leading actors. Other copies of
/// the same movie are not related, they are the same.
/// </summary>
public static class RelatedTitles
{
    public const int Most = 24;

    /// <param name="leads">The first actors of each movie, by movie id (see LibraryDatabase.GetLeadingCast).</param>
    public static List<RelatedMovie> Find(Movie movie, IEnumerable<Movie> library, IReadOnlyDictionary<long, List<string>> leads)
    {
        var others = library.Where(m => m.Id != movie.Id && !Same(m, movie)).ToList();
        var result = new List<RelatedMovie>();
        var taken = new HashSet<long>();

        void Add(IEnumerable<Movie> movies, Func<Movie, string> reason)
        {
            foreach (var m in movies)
            {
                // One copy of each: the others are in Duplicates
                if (result.Count >= Most || result.Any(r => Same(r.Movie, m)))
                    continue;
                if (taken.Add(m.Id))
                    result.Add(new RelatedMovie(m, reason(m)));
            }
        }

        if (!string.IsNullOrWhiteSpace(movie.Collection))
            Add(others.Where(m => string.Equals(m.Collection, movie.Collection, StringComparison.CurrentCultureIgnoreCase))
                      .OrderBy(m => m.Year ?? int.MaxValue), _ => movie.Collection!);

        var directors = movie.DirectorList.Select(SearchText.Normalize).Where(d => d.Length > 0).ToHashSet();
        if (directors.Count > 0)
            Add(others.Where(m => m.DirectorList.Any(d => directors.Contains(SearchText.Normalize(d))))
                      .OrderByDescending(m => m.Year ?? 0),
                m => "Directed by " + m.DirectorList.First(d => directors.Contains(SearchText.Normalize(d))));

        if (leads.TryGetValue(movie.Id, out var mine) && mine.Count >= 2)
        {
            var names = mine.Select(SearchText.Normalize).ToHashSet();
            Add(others.Select(m => (Movie: m, Shared: leads.TryGetValue(m.Id, out var theirs)
                                       ? theirs.Where(n => names.Contains(SearchText.Normalize(n))).ToList() : []))
                      .Where(x => x.Shared.Count >= 2)
                      .OrderByDescending(x => x.Shared.Count).ThenByDescending(x => x.Movie.Year ?? 0)
                      .Select(x => x.Movie),
                m => "With " + string.Join(" and ", leads[m.Id].Where(n => names.Contains(SearchText.Normalize(n))).Take(2)));
        }
        return result;
    }

    /// <summary>Two copies of one movie: an id in common, or no ids and the same name and year.</summary>
    public static bool Same(Movie a, Movie b) =>
        (!string.IsNullOrEmpty(a.ImdbId) && string.Equals(a.ImdbId, b.ImdbId, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(a.TmdbId) && a.TmdbId == b.TmdbId)
        || (a.NeedsMatch && b.NeedsMatch && a.Year == b.Year && SearchText.Normalize(a.Title) == SearchText.Normalize(b.Title));
}
