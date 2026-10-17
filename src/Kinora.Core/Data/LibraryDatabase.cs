using Dapper;
using Kinora.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kinora.Core.Data;

/// <summary>
/// The library in SQLite. One connection per call (they are pooled); writes that belong
/// together go in one transaction.
/// </summary>
public sealed class LibraryDatabase
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;

    static LibraryDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public LibraryDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode = WAL;");
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        if (version >= SchemaVersion)
            return;

        using var tx = c.BeginTransaction();
        if (version < 1)
        {
            c.Execute("""
                CREATE TABLE roots (
                    id INTEGER PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    path TEXT NOT NULL,
                    last_scan INTEGER
                );
                CREATE TABLE movies (
                    id INTEGER PRIMARY KEY,
                    root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                    folder TEXT NOT NULL,
                    video_file TEXT NOT NULL,
                    video_size INTEGER NOT NULL DEFAULT 0,
                    stamp INTEGER NOT NULL DEFAULT 0,
                    title TEXT NOT NULL,
                    original_title TEXT,
                    sort_title TEXT,
                    year INTEGER,
                    plot TEXT,
                    tagline TEXT,
                    runtime INTEGER,
                    rating REAL,
                    critic_rating INTEGER,
                    mpaa TEXT,
                    premiered TEXT,
                    imdb_id TEXT,
                    tmdb_id TEXT,
                    collection TEXT,
                    genres TEXT NOT NULL DEFAULT '',
                    studios TEXT NOT NULL DEFAULT '',
                    directors TEXT NOT NULL DEFAULT '',
                    countries TEXT NOT NULL DEFAULT '',
                    poster_file TEXT,
                    fanart_file TEXT,
                    logo_file TEXT,
                    thumb_key TEXT,
                    resolution TEXT,
                    video_codec TEXT,
                    audio_codec TEXT,
                    audio_channels INTEGER,
                    subtitles TEXT NOT NULL DEFAULT '',
                    other_videos TEXT NOT NULL DEFAULT '',
                    watched INTEGER NOT NULL DEFAULT 0,
                    date_added INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX ix_movies_video ON movies(root_id, video_file);
                CREATE INDEX ix_movies_imdb ON movies(imdb_id);
                CREATE INDEX ix_movies_tmdb ON movies(tmdb_id);
                CREATE TABLE cast_members (
                    movie_id INTEGER NOT NULL REFERENCES movies(id) ON DELETE CASCADE,
                    ord INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    role TEXT,
                    PRIMARY KEY (movie_id, ord)
                );
                -- What the user did with a title, kept apart from the file: it survives when the
                -- file disappears and comes back somewhere else (moved outside Kinora).
                CREATE TABLE user_state (
                    key TEXT PRIMARY KEY,
                    watched INTEGER NOT NULL DEFAULT 0,
                    date_added INTEGER
                );
                """, transaction: tx);
        }
        c.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: tx);
        tx.Commit();
    }

    // ---- roots ----

    public List<LibraryRoot> GetRoots()
    {
        using var c = Open();
        return c.Query<LibraryRoot>("SELECT * FROM roots ORDER BY kind, name").ToList();
    }

    public LibraryRoot AddRoot(LibraryKind kind, string name, string path)
    {
        using var c = Open();
        var id = c.ExecuteScalar<long>("INSERT INTO roots (kind, name, path) VALUES (@kind, @name, @path); SELECT last_insert_rowid();",
                                       new { kind = (int)kind, name, path });
        return new LibraryRoot { Id = id, Kind = kind, Name = name, Path = path };
    }

    public void UpdateRoot(LibraryRoot root)
    {
        using var c = Open();
        c.Execute("UPDATE roots SET name = @Name, path = @Path, last_scan = @LastScan WHERE id = @Id", root);
    }

    public void RemoveRoot(long rootId)
    {
        using var c = Open();
        c.Execute("DELETE FROM roots WHERE id = @rootId", new { rootId });
    }

    // ---- movies ----

    public List<Movie> GetMovies(long? rootId = null)
    {
        using var c = Open();
        return rootId is null
            ? c.Query<Movie>("SELECT * FROM movies").ToList()
            : c.Query<Movie>("SELECT * FROM movies WHERE root_id = @rootId", new { rootId }).ToList();
    }

    public Movie? GetMovie(long id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<Movie>("SELECT * FROM movies WHERE id = @id", new { id });
    }

    /// <summary>The cast of every movie in one string each, for the search.</summary>
    public Dictionary<long, string> GetCastNames()
    {
        using var c = Open();
        return c.Query<(long MovieId, string Names)>("SELECT movie_id, group_concat(name, ' ') FROM cast_members GROUP BY movie_id")
                .ToDictionary(r => r.MovieId, r => r.Names);
    }

    public List<CastMember> GetCast(long movieId)
    {
        using var c = Open();
        return c.Query<CastMember>("SELECT * FROM cast_members WHERE movie_id = @movieId ORDER BY ord", new { movieId }).ToList();
    }

    /// <summary>Inserts or updates movies with their cast, all in one transaction.</summary>
    public void SaveMovies(IEnumerable<(Movie Movie, IReadOnlyList<CastMember> Cast)> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (movie, cast) in items)
        {
            if (movie.Id == 0)
            {
                ApplyUserState(c, tx, movie);
                movie.Id = c.ExecuteScalar<long>($"""
                    INSERT INTO movies ({Columns}) VALUES ({Parameters});
                    SELECT last_insert_rowid();
                    """, movie, tx);
            }
            else
            {
                c.Execute($"UPDATE movies SET {Assignments} WHERE id = @Id", movie, tx);
                c.Execute("DELETE FROM cast_members WHERE movie_id = @Id", movie, tx);
            }
            for (var i = 0; i < cast.Count; i++)
            {
                cast[i].MovieId = movie.Id;
                cast[i].Ord = i;
            }
            c.Execute("INSERT INTO cast_members (movie_id, ord, name, role) VALUES (@MovieId, @Ord, @Name, @Role)", cast, tx);
        }
        tx.Commit();
    }

    public void DeleteMovies(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM movies WHERE id = @id", ids.Select(id => new { id }), tx);
        tx.Commit();
    }

    public void SetWatched(Movie movie, bool watched)
    {
        movie.Watched = watched;
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE movies SET watched = @watched WHERE id = @id", new { watched, id = movie.Id }, tx);
        foreach (var key in UserStateKeys(movie))
            c.Execute("""
                INSERT INTO user_state (key, watched, date_added) VALUES (@key, @watched, @added)
                ON CONFLICT(key) DO UPDATE SET watched = excluded.watched
                """, new { key, watched, added = movie.DateAdded }, tx);
        tx.Commit();
    }

    /// <summary>
    /// A title that comes back (same IMDb or TMDB id) keeps its watched flag and the date it was
    /// first added, even if it was deleted from the library in the meantime.
    /// </summary>
    private static void ApplyUserState(SqliteConnection c, SqliteTransaction tx, Movie movie)
    {
        foreach (var key in UserStateKeys(movie))
        {
            var s = c.QuerySingleOrDefault<UserStateRow>(
                "SELECT watched, date_added FROM user_state WHERE key = @key", new { key }, tx);
            if (s is null)
                continue;
            movie.Watched |= s.Watched != 0;
            if (s.DateAdded is { } added && added < movie.DateAdded)
                movie.DateAdded = added;
            return;
        }
    }

    private sealed class UserStateRow
    {
        public long Watched { get; set; }
        public long? DateAdded { get; set; }
    }

    private static IEnumerable<string> UserStateKeys(Movie movie)
    {
        if (!string.IsNullOrEmpty(movie.ImdbId)) yield return "imdb:" + movie.ImdbId;
        if (!string.IsNullOrEmpty(movie.TmdbId)) yield return "tmdb:" + movie.TmdbId;
    }

    private static readonly string[] MovieColumns =
    [
        "root_id", "folder", "video_file", "video_size", "stamp", "title", "original_title", "sort_title", "year",
        "plot", "tagline", "runtime", "rating", "critic_rating", "mpaa", "premiered", "imdb_id", "tmdb_id",
        "collection", "genres", "studios", "directors", "countries", "poster_file", "fanart_file", "logo_file",
        "thumb_key", "resolution", "video_codec", "audio_codec", "audio_channels", "subtitles", "other_videos", "watched", "date_added",
    ];

    private static string ToProperty(string column) =>
        string.Concat(column.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static readonly string Columns = string.Join(", ", MovieColumns);
    private static readonly string Parameters = string.Join(", ", MovieColumns.Select(c => "@" + ToProperty(c)));
    private static readonly string Assignments = string.Join(", ", MovieColumns.Select(c => $"{c} = @{ToProperty(c)}"));
}
