using Dapper;

namespace Kinora.Core.Data;

/// <summary>IMDb's ratings, from its daily file, for the titles in the library.</summary>
public sealed partial class LibraryDatabase
{
    private const string MovieSelect = """
        SELECT m.*, ir.rating AS imdb_latest_rating, ir.votes AS imdb_latest_votes
        FROM movies m LEFT JOIN imdb_ratings ir ON ir.imdb_id = m.imdb_id
        """;

    /// <summary>The IMDb ids of the movies and shows in the library.</summary>
    public HashSet<string> GetLibraryImdbIds()
    {
        using var c = Open();
        return c.Query<string>("""
            SELECT imdb_id FROM movies WHERE imdb_id IS NOT NULL AND imdb_id <> ''
            UNION SELECT imdb_id FROM series WHERE imdb_id IS NOT NULL AND imdb_id <> ''
            """).ToHashSet(StringComparer.Ordinal);
    }

    public Dictionary<string, (double Rating, int Votes)> GetImdbRatings()
    {
        using var c = Open();
        return c.Query<(string Id, double Rating, int Votes)>("SELECT imdb_id, rating, votes FROM imdb_ratings")
                .ToDictionary(r => r.Id, r => (r.Rating, r.Votes), StringComparer.Ordinal);
    }

    /// <summary>Puts these in place of the ones kept; says how many titles got a different rating.</summary>
    public int ReplaceImdbRatings(IReadOnlyDictionary<string, (double Rating, int Votes)> ratings)
    {
        var old = GetImdbRatings();
        var changed = ratings.Count(r => !old.TryGetValue(r.Key, out var o) || o != r.Value)
                      + old.Keys.Count(id => !ratings.ContainsKey(id));
        if (changed == 0)
            return 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM imdb_ratings", transaction: tx);
        c.Execute("INSERT INTO imdb_ratings (imdb_id, rating, votes) VALUES (@Key, @Rating, @Votes)",
                  ratings.Select(r => new { r.Key, r.Value.Rating, r.Value.Votes }), tx);
        tx.Commit();
        return changed;
    }
}
