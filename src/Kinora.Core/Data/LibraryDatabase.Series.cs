using Dapper;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Microsoft.Data.Sqlite;

namespace Kinora.Core.Data;

/// <summary>TV shows: series, their seasons and episodes.</summary>
public sealed partial class LibraryDatabase
{
    private const string SeriesSelect = """
        SELECT s.*,
               (SELECT count(*) FROM episodes e WHERE e.series_id = s.id) AS episode_count,
               (SELECT count(*) FROM episodes e WHERE e.series_id = s.id AND e.watched = 1) AS watched_count
        FROM series s
        """;

    public List<Series> GetSeries(long? rootId = null)
    {
        using var c = Open();
        return rootId is null
            ? c.Query<Series>(SeriesSelect).ToList()
            : c.Query<Series>(SeriesSelect + " WHERE s.root_id = @rootId", new { rootId }).ToList();
    }

    public Series? GetSeriesById(long id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<Series>(SeriesSelect + " WHERE s.id = @id", new { id });
    }

    public List<Season> GetSeasons(long seriesId)
    {
        using var c = Open();
        return c.Query<Season>("SELECT * FROM seasons WHERE series_id = @seriesId ORDER BY number", new { seriesId }).ToList();
    }

    public List<Episode> GetEpisodes(long seriesId)
    {
        using var c = Open();
        return c.Query<Episode>("SELECT * FROM episodes WHERE series_id = @seriesId", new { seriesId }).ToList();
    }

    public List<CastMember> GetSeriesCast(long seriesId)
    {
        using var c = Open();
        return c.Query<CastMember>("SELECT series_id AS movie_id, ord, name, role FROM series_cast WHERE series_id = @seriesId ORDER BY ord",
                                   new { seriesId }).ToList();
    }

    public Dictionary<long, string> GetSeriesCastNames()
    {
        using var c = Open();
        return c.Query<(long SeriesId, string Names)>("SELECT series_id, group_concat(name, ' ') FROM series_cast GROUP BY series_id")
                .ToDictionary(r => r.SeriesId, r => r.Names);
    }

    /// <summary>
    /// A show with everything below it, in one transaction. Episodes are matched by file: the ones
    /// with an id are updated, the new ones inserted, the ones no longer listed deleted.
    /// </summary>
    public void SaveSeries(Series series, IReadOnlyList<CastMember> cast, IReadOnlyList<Season> seasons, IReadOnlyList<Episode> episodes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (series.Id == 0)
            series.Id = c.ExecuteScalar<long>($"INSERT INTO series ({SeriesColumnList}) VALUES ({SeriesParameters}); SELECT last_insert_rowid();", series, tx);
        else
            c.Execute($"UPDATE series SET {SeriesAssignments} WHERE id = @Id", series, tx);

        c.Execute("DELETE FROM series_cast WHERE series_id = @Id", series, tx);
        c.Execute("INSERT INTO series_cast (series_id, ord, name, role) VALUES (@SeriesId, @Ord, @Name, @Role)",
                  cast.Select((m, i) => new { SeriesId = series.Id, Ord = i, m.Name, m.Role }), tx);

        c.Execute("DELETE FROM seasons WHERE series_id = @Id", series, tx);
        foreach (var s in seasons)
            s.SeriesId = series.Id;
        c.Execute("INSERT INTO seasons (series_id, number, poster_file, thumb_key, online_poster) VALUES (@SeriesId, @Number, @PosterFile, @ThumbKey, @OnlinePoster)",
                  seasons, tx);

        var keep = new List<long>();
        foreach (var e in episodes)
        {
            e.SeriesId = series.Id;
            if (e.Id == 0)
            {
                ApplyEpisodeState(c, tx, series, e);
                e.Id = c.ExecuteScalar<long>($"INSERT INTO episodes ({EpisodeColumnList}) VALUES ({EpisodeParameters}); SELECT last_insert_rowid();", e, tx);
            }
            else
            {
                c.Execute($"UPDATE episodes SET {EpisodeAssignments} WHERE id = @Id", e, tx);
            }
            keep.Add(e.Id);
        }
        var gone = c.Query<long>("SELECT id FROM episodes WHERE series_id = @Id", series, tx).Except(keep).ToList();
        c.Execute("DELETE FROM episodes WHERE id = @id", gone.Select(id => new { id }), tx);
        tx.Commit();
    }

    public void DeleteSeries(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM series WHERE id = @id", ids.Select(id => new { id }), tx);
        tx.Commit();
    }

    /// <summary>Marks episodes watched or not, remembering it by the show's ids for when files move.</summary>
    public void SetEpisodesWatched(Series series, IEnumerable<Episode> episodes, bool watched)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var e in episodes)
        {
            e.Watched = watched;
            c.Execute("UPDATE episodes SET watched = @watched WHERE id = @id", new { watched, id = e.Id }, tx);
            foreach (var key in EpisodeStateKeys(series, e))
                c.Execute("""
                    INSERT INTO user_state (key, watched, date_added) VALUES (@key, @watched, @added)
                    ON CONFLICT(key) DO UPDATE SET watched = excluded.watched
                    """, new { key, watched, added = e.DateAdded }, tx);
        }
        tx.Commit();
    }

    private static void ApplyEpisodeState(SqliteConnection c, SqliteTransaction tx, Series series, Episode e)
    {
        foreach (var key in EpisodeStateKeys(series, e))
        {
            var s = c.QuerySingleOrDefault<UserStateRow>("SELECT watched, date_added FROM user_state WHERE key = @key", new { key }, tx);
            if (s is null)
                continue;
            e.Watched |= s.Watched != 0;
            if (s.DateAdded is { } added && added < e.DateAdded)
                e.DateAdded = added;
            return;
        }
    }

    private static IEnumerable<string> EpisodeStateKeys(Series series, Episode e)
    {
        if (e.Season is not { } s || e.Number is not { } n)
            yield break;
        if (!string.IsNullOrEmpty(series.TmdbId)) yield return $"tmdbtv:{series.TmdbId}:{s}:{n}";
        if (!string.IsNullOrEmpty(series.TvdbId)) yield return $"tvdb:{series.TvdbId}:{s}:{n}";
        if (!string.IsNullOrEmpty(series.ImdbId)) yield return $"imdbtv:{series.ImdbId}:{s}:{n}";
    }

    // ---- TMDB cache for shows ----

    public void SaveTmdbSeries(TmdbSeries series)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO tmdb_series (tmdb_id, imdb_id, tvdb_id, language, data, fetched) VALUES (@id, @imdb, @tvdb, @language, @data, @fetched)
            ON CONFLICT(tmdb_id) DO UPDATE SET imdb_id = excluded.imdb_id, tvdb_id = excluded.tvdb_id, language = excluded.language,
                                               data = excluded.data, fetched = excluded.fetched
            """, new
        {
            id = series.Id,
            imdb = series.ImdbId,
            tvdb = series.TvdbId,
            language = series.Language,
            data = series.ToJson(),
            fetched = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    public TmdbSeries? GetTmdbSeries(string tmdbId)
    {
        using var c = Open();
        var json = c.QuerySingleOrDefault<string>("SELECT data FROM tmdb_series WHERE tmdb_id = @tmdbId", new { tmdbId });
        return json is null ? null : TmdbSeries.FromJson(json);
    }

    public List<(string TmdbId, string? ImdbId, string? TvdbId, string Data)> GetTmdbSeriesRaw()
    {
        using var c = Open();
        return c.Query<(string, string?, string?, string)>("SELECT tmdb_id, imdb_id, tvdb_id, data FROM tmdb_series").ToList();
    }

    public void SaveTmdbSeason(string tmdbId, TmdbSeason season)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO tmdb_seasons (tmdb_id, season, language, data, fetched) VALUES (@tmdbId, @number, @language, @data, @fetched)
            ON CONFLICT(tmdb_id, season) DO UPDATE SET language = excluded.language, data = excluded.data, fetched = excluded.fetched
            """, new { tmdbId, number = season.Number, language = season.Language, data = season.ToJson(), fetched = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public List<TmdbSeason> GetTmdbSeasons(string tmdbId)
    {
        using var c = Open();
        return c.Query<string>("SELECT data FROM tmdb_seasons WHERE tmdb_id = @tmdbId ORDER BY season", new { tmdbId })
                .Select(TmdbSeason.FromJson).OfType<TmdbSeason>().ToList();
    }

    private static readonly string[] SeriesColumns =
    [
        "root_id", "folder", "stamp", "title", "original_title", "sort_title", "year", "end_year", "plot", "rating", "mpaa",
        "premiered", "status", "imdb_id", "tmdb_id", "tvdb_id", "genres", "studios", "poster_file", "fanart_file", "logo_file",
        "thumb_key", "online_poster", "online_fanart", "online_logo", "date_added", "last_episode_added",
    ];

    private static readonly string[] EpisodeColumns =
    [
        "series_id", "season", "number", "number_end", "title", "plot", "aired", "runtime", "rating", "directors", "imdb_id",
        "video_file", "video_size", "thumb_file", "online_still", "resolution", "video_codec", "audio_codec", "audio_channels",
        "subtitles", "watched", "date_added",
    ];

    private static readonly string SeriesColumnList = string.Join(", ", SeriesColumns);
    private static readonly string SeriesParameters = string.Join(", ", SeriesColumns.Select(c => "@" + ToProperty(c)));
    private static readonly string SeriesAssignments = string.Join(", ", SeriesColumns.Select(c => $"{c} = @{ToProperty(c)}"));
    private static readonly string EpisodeColumnList = string.Join(", ", EpisodeColumns);
    private static readonly string EpisodeParameters = string.Join(", ", EpisodeColumns.Select(c => "@" + ToProperty(c)));
    private static readonly string EpisodeAssignments = string.Join(", ", EpisodeColumns.Select(c => $"{c} = @{ToProperty(c)}"));
}
