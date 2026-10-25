using Dapper;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Microsoft.Data.Sqlite;

namespace Kinora.Core.Data;

/// <summary>
/// The library in SQLite. One connection per call (they are pooled); writes that belong
/// together go in one transaction.
/// </summary>
public sealed partial class LibraryDatabase
{
    private const int SchemaVersion = 5;

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
        if (version < 2)
        {
            c.Execute("""
                -- Pictures from TMDB, used only where the folder has none (paths on image.tmdb.org)
                ALTER TABLE movies ADD COLUMN online_poster TEXT;
                ALTER TABLE movies ADD COLUMN online_fanart TEXT;
                ALTER TABLE movies ADD COLUMN online_logo TEXT;
                -- What TMDB said about a movie, kept apart from the movies: a scan rebuilds the
                -- movies from the folders and fills their gaps from here.
                CREATE TABLE tmdb_movies (
                    tmdb_id TEXT PRIMARY KEY,
                    imdb_id TEXT,
                    language TEXT NOT NULL,
                    data TEXT NOT NULL,
                    fetched INTEGER NOT NULL
                );
                CREATE INDEX ix_tmdb_movies_imdb ON tmdb_movies(imdb_id);
                -- Which TMDB movie a video without ids is: found by the search (manual = 0), chosen
                -- by the user (manual = 1), or searched without result (tmdb_id NULL).
                CREATE TABLE matches (
                    root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                    video_file TEXT NOT NULL,
                    tmdb_id TEXT,
                    manual INTEGER NOT NULL DEFAULT 0,
                    tried INTEGER NOT NULL,
                    PRIMARY KEY (root_id, video_file)
                );
                """, transaction: tx);
        }
        if (version < 3)
        {
            c.Execute("""
                CREATE TABLE series (
                    id INTEGER PRIMARY KEY,
                    root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                    folder TEXT NOT NULL,
                    stamp INTEGER NOT NULL DEFAULT 0,
                    title TEXT NOT NULL,
                    original_title TEXT,
                    sort_title TEXT,
                    year INTEGER,
                    end_year INTEGER,
                    plot TEXT,
                    rating REAL,
                    mpaa TEXT,
                    premiered TEXT,
                    status TEXT,
                    imdb_id TEXT,
                    tmdb_id TEXT,
                    tvdb_id TEXT,
                    genres TEXT NOT NULL DEFAULT '',
                    studios TEXT NOT NULL DEFAULT '',
                    poster_file TEXT,
                    fanart_file TEXT,
                    logo_file TEXT,
                    thumb_key TEXT,
                    online_poster TEXT,
                    online_fanart TEXT,
                    online_logo TEXT,
                    date_added INTEGER NOT NULL,
                    last_episode_added INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX ix_series_folder ON series(root_id, folder);
                CREATE TABLE seasons (
                    series_id INTEGER NOT NULL REFERENCES series(id) ON DELETE CASCADE,
                    number INTEGER NOT NULL,
                    poster_file TEXT,
                    thumb_key TEXT,
                    online_poster TEXT,
                    PRIMARY KEY (series_id, number)
                );
                CREATE TABLE episodes (
                    id INTEGER PRIMARY KEY,
                    series_id INTEGER NOT NULL REFERENCES series(id) ON DELETE CASCADE,
                    season INTEGER,
                    number INTEGER,
                    number_end INTEGER,
                    title TEXT NOT NULL,
                    plot TEXT,
                    aired TEXT,
                    runtime INTEGER,
                    rating REAL,
                    directors TEXT NOT NULL DEFAULT '',
                    imdb_id TEXT,
                    video_file TEXT NOT NULL,
                    video_size INTEGER NOT NULL DEFAULT 0,
                    thumb_file TEXT,
                    online_still TEXT,
                    resolution TEXT,
                    video_codec TEXT,
                    audio_codec TEXT,
                    audio_channels INTEGER,
                    subtitles TEXT NOT NULL DEFAULT '',
                    watched INTEGER NOT NULL DEFAULT 0,
                    date_added INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX ix_episodes_video ON episodes(series_id, video_file);
                CREATE TABLE series_cast (
                    series_id INTEGER NOT NULL REFERENCES series(id) ON DELETE CASCADE,
                    ord INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    role TEXT,
                    PRIMARY KEY (series_id, ord)
                );
                CREATE TABLE tmdb_series (
                    tmdb_id TEXT PRIMARY KEY,
                    imdb_id TEXT,
                    tvdb_id TEXT,
                    language TEXT NOT NULL,
                    data TEXT NOT NULL,
                    fetched INTEGER NOT NULL
                );
                CREATE TABLE tmdb_seasons (
                    tmdb_id TEXT NOT NULL,
                    season INTEGER NOT NULL,
                    language TEXT NOT NULL,
                    data TEXT NOT NULL,
                    fetched INTEGER NOT NULL,
                    PRIMARY KEY (tmdb_id, season)
                );
                """, transaction: tx);
        }
        if (version < 4)
        {
            c.Execute("""
                ALTER TABLE cast_members ADD COLUMN tmdb_id TEXT;
                ALTER TABLE series_cast ADD COLUMN tmdb_id TEXT;
                CREATE INDEX ix_cast_members_name ON cast_members(name COLLATE NOCASE);
                CREATE INDEX ix_series_cast_name ON series_cast(name COLLATE NOCASE);
                -- People pages: what TMDB says about a person, asked once
                CREATE TABLE tmdb_people (
                    tmdb_id TEXT PRIMARY KEY,
                    language TEXT NOT NULL,
                    data TEXT NOT NULL,
                    fetched INTEGER NOT NULL
                );
                -- Read every folder again once, to pick up the actors' TMDB ids from the nfo files
                UPDATE movies SET stamp = 0;
                UPDATE series SET stamp = 0;
                """, transaction: tx);
        }
        if (version < 5)
        {
            c.Execute("""
                -- Where the built-in player stopped: a movie or an episode, by its id
                CREATE TABLE playback (
                    kind TEXT NOT NULL,
                    item_id INTEGER NOT NULL,
                    position_ms INTEGER NOT NULL,
                    length_ms INTEGER NOT NULL,
                    updated INTEGER NOT NULL,
                    PRIMARY KEY (kind, item_id)
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
            c.Execute("INSERT INTO cast_members (movie_id, ord, name, role, tmdb_id) VALUES (@MovieId, @Ord, @Name, @Role, @TmdbId)", cast, tx);
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

    // ---- online metadata ----

    public void SaveTmdbMovie(TmdbMovie movie)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO tmdb_movies (tmdb_id, imdb_id, language, data, fetched) VALUES (@id, @imdb, @language, @data, @fetched)
            ON CONFLICT(tmdb_id) DO UPDATE SET imdb_id = excluded.imdb_id, language = excluded.language,
                                               data = excluded.data, fetched = excluded.fetched
            """, new
        {
            id = movie.Id,
            imdb = movie.ImdbId,
            language = movie.Language,
            data = movie.ToJson(),
            fetched = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    public TmdbMovie? GetTmdbMovie(string tmdbId)
    {
        using var c = Open();
        var json = c.QuerySingleOrDefault<string>("SELECT data FROM tmdb_movies WHERE tmdb_id = @tmdbId", new { tmdbId });
        return json is null ? null : TmdbMovie.FromJson(json);
    }

    /// <summary>Every cached TMDB movie, still as JSON: the scanner decodes only the ones it needs.</summary>
    public List<(string TmdbId, string? ImdbId, string Data)> GetTmdbMoviesRaw()
    {
        using var c = Open();
        return c.Query<(string, string?, string)>("SELECT tmdb_id, imdb_id, data FROM tmdb_movies").ToList();
    }

    public Dictionary<string, MatchInfo> GetMatches(long rootId)
    {
        using var c = Open();
        return c.Query<MatchInfo>("SELECT video_file, tmdb_id, manual, tried FROM matches WHERE root_id = @rootId", new { rootId })
                .ToDictionary(m => m.VideoFile, StringComparer.OrdinalIgnoreCase);
    }

    public MatchInfo? GetMatch(long rootId, string videoFile)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<MatchInfo>("SELECT video_file, tmdb_id, manual, tried FROM matches WHERE root_id = @rootId AND video_file = @videoFile",
                                                 new { rootId, videoFile });
    }

    public void SaveMatch(long rootId, string videoFile, string? tmdbId, bool manual)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO matches (root_id, video_file, tmdb_id, manual, tried) VALUES (@rootId, @videoFile, @tmdbId, @manual, @tried)
            ON CONFLICT(root_id, video_file) DO UPDATE SET tmdb_id = excluded.tmdb_id, manual = excluded.manual, tried = excluded.tried
            """, new { rootId, videoFile, tmdbId, manual, tried = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
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
        "thumb_key", "resolution", "video_codec", "audio_codec", "audio_channels", "subtitles", "other_videos", "watched", "date_added", "online_poster", "online_fanart", "online_logo",
    ];

    private static string ToProperty(string column) =>
        string.Concat(column.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static readonly string Columns = string.Join(", ", MovieColumns);
    private static readonly string Parameters = string.Join(", ", MovieColumns.Select(c => "@" + ToProperty(c)));
    private static readonly string Assignments = string.Join(", ", MovieColumns.Select(c => $"{c} = @{ToProperty(c)}"));
}

public sealed class MatchInfo
{
    public string VideoFile { get; set; } = "";
    public string? TmdbId { get; set; }
    public bool Manual { get; set; }
    public long Tried { get; set; }
}
