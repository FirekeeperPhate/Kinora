using Dapper;
using Kinora.Core.Models;
using Kinora.Core.Online;

namespace Kinora.Core.Data;

/// <summary>A movie or show a person is in, with what they did there.</summary>
public sealed record PersonCredit(bool IsSeries, long Id, string Role);

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// What a person did in the library: acted (by TMDB id when known, else by name) or directed
    /// (by name: directors come from the nfo as plain names).
    /// </summary>
    public List<PersonCredit> GetCredits(string name, string? tmdbId)
    {
        using var c = Open();
        var credits = new List<PersonCredit>();
        const string byPerson = "(tmdb_id = @tmdbId OR (name = @name COLLATE NOCASE AND (tmdb_id IS NULL OR @tmdbId IS NULL)))";
        foreach (var (id, role) in c.Query<(long, string?)>($"SELECT movie_id, role FROM cast_members WHERE {byPerson}", new { name, tmdbId }))
            credits.Add(new PersonCredit(false, id, string.IsNullOrWhiteSpace(role) ? "Actor" : role!));
        foreach (var (id, role) in c.Query<(long, string?)>($"SELECT series_id, role FROM series_cast WHERE {byPerson}", new { name, tmdbId }))
            credits.Add(new PersonCredit(true, id, string.IsNullOrWhiteSpace(role) ? "Actor" : role!));

        // Directors are stored joined with '|': match the whole name, not a part of it
        foreach (var (id, directors) in c.Query<(long, string)>("SELECT id, directors FROM movies WHERE directors LIKE @like",
                                                                  new { like = "%" + name + "%" }))
        {
            if (Movie.Split(directors).Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                var acted = credits.FindIndex(x => !x.IsSeries && x.Id == id);
                if (acted >= 0)
                    credits[acted] = credits[acted] with { Role = "Director · " + credits[acted].Role };
                else
                    credits.Add(new PersonCredit(false, id, "Director"));
            }
        }
        return credits.DistinctBy(x => (x.IsSeries, x.Id)).ToList();
    }

    public void SaveTmdbPerson(TmdbPerson person)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO tmdb_people (tmdb_id, language, data, fetched) VALUES (@id, @language, @data, @fetched)
            ON CONFLICT(tmdb_id) DO UPDATE SET language = excluded.language, data = excluded.data, fetched = excluded.fetched
            """, new { id = person.Id, language = person.Language, data = person.ToJson(), fetched = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public TmdbPerson? GetTmdbPerson(string tmdbId)
    {
        using var c = Open();
        var json = c.QuerySingleOrDefault<string>("SELECT data FROM tmdb_people WHERE tmdb_id = @tmdbId", new { tmdbId });
        return json is null ? null : TmdbPerson.FromJson(json);
    }

    /// <summary>A person's TMDB id, if any cast entry in the library knows it.</summary>
    public string? FindPersonId(string name)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<string?>("""
            SELECT tmdb_id FROM cast_members WHERE name = @name COLLATE NOCASE AND tmdb_id IS NOT NULL
            UNION ALL
            SELECT tmdb_id FROM series_cast WHERE name = @name COLLATE NOCASE AND tmdb_id IS NOT NULL
            LIMIT 1
            """, new { name });
    }
}
