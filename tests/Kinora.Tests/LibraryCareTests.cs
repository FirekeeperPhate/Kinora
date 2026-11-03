using System.Net;
using System.Text;
using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Playback;
using Kinora.Core.Scanning;
using Kinora.Core.Subtitles;

namespace Kinora.Tests;

/// <summary>Missing episodes, what needs attention, subtitles for a season, chapters and the intro.</summary>
public sealed class LibraryCareTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 3);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-care-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public LibraryCareTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- missing episodes ----

    private static TmdbSeries Show(string status, params TmdbSeasonInfo[] seasons) => new() { Id = "1", Name = "Show", Status = status, Seasons = [.. seasons] };

    private static TmdbSeason Season(int number, params (int Number, string? Aired)[] episodes) =>
        new() { Number = number, Episodes = episodes.Select(e => new TmdbEpisode(e.Number, $"Episode {e.Number}", null, e.Aired, null, null, null)).ToList() };

    private static (int?, int?, int?) E(int season, int number, int? end = null) => (season, number, end);

    [Fact]
    public void An_ended_show_lacks_the_holes_and_the_seasons_not_there()
    {
        var show = Show("Ended", new TmdbSeasonInfo(1, 3, "2008-01-20"), new TmdbSeasonInfo(2, 2, "2009-03-08"), new TmdbSeasonInfo(0, 4, "2008-01-01"));
        var gaps = EpisodeGaps.Find(show, new Dictionary<int, TmdbSeason>(), [E(1, 1), E(1, 3)], Today)!;

        Assert.Equal(["S01E02", "S02E01", "S02E02"], gaps.Episodes.Select(e => e.Code));   // specials don't count
        Assert.Equal([2], gaps.WholeSeasons);
        Assert.False(gaps.Episodes[0].IsNew);   // a hole
        Assert.True(gaps.Episodes[1].IsNew);    // after the last one on disk
        Assert.Equal("S01E02, Season 2", gaps.Describe());
        Assert.Equal("3 missing: S01E02, Season 2", gaps.Note);
    }

    [Fact]
    public void A_show_still_going_counts_only_what_has_aired()
    {
        var show = Show("Returning Series", new TmdbSeasonInfo(1, 2, "2025-01-01"), new TmdbSeasonInfo(2, 4, "2026-10-01"));
        var seasons = new Dictionary<int, TmdbSeason>
        {
            [2] = Season(2, (1, "2026-10-01"), (2, "2026-10-08"), (3, "2026-11-10"), (4, null)),
        };
        var gaps = EpisodeGaps.Find(show, seasons, [E(1, 1), E(1, 2), E(2, 1)], Today)!;

        Assert.Equal(["S02E02"], gaps.Episodes.Select(e => e.Code));
        Assert.Equal("Episode 2", gaps.Episodes[0].Name);
        Assert.Equal("New: S02E02", gaps.Note);
    }

    [Fact]
    public void The_last_season_known_only_by_its_count_says_nothing_yet()
    {
        // It may be airing: what has aired is known only once its episodes are asked
        var show = Show("Returning Series", new TmdbSeasonInfo(1, 2, "2025-01-01"), new TmdbSeasonInfo(2, 8, "2026-10-01"));
        Assert.Null(EpisodeGaps.Find(show, new Dictionary<int, TmdbSeason>(), [E(1, 1), E(1, 2)], Today));
    }

    [Fact]
    public void A_season_only_announced_doesnt_make_the_one_airing_complete()
    {
        // Season 3 announced with a count and no date: season 2 may still be airing
        var show = Show("Returning Series", new TmdbSeasonInfo(1, 2, "2025-01-01"), new TmdbSeasonInfo(2, 8, "2026-10-01"), new TmdbSeasonInfo(3, 10, null));
        Assert.Null(EpisodeGaps.Find(show, new Dictionary<int, TmdbSeason>(), [E(1, 1), E(1, 2), E(2, 1)], Today));
        Assert.Equal(2, EpisodeGaps.LatestAired(show, Today));
    }

    [Fact]
    public void Double_episodes_count_for_both_and_a_complete_show_lacks_nothing()
    {
        var show = Show("Ended", new TmdbSeasonInfo(1, 3, "2008-01-20"));
        Assert.Null(EpisodeGaps.Find(show, new Dictionary<int, TmdbSeason>(), [E(1, 1, 2), E(1, 3)], Today));
    }

    [Fact]
    public void A_folder_numbered_another_way_is_not_taken_for_holes()
    {
        // 30 episodes in one season on disk (absolute numbering), three seasons of 10 on TMDB
        var show = Show("Ended", new TmdbSeasonInfo(1, 10, "2010-01-01"), new TmdbSeasonInfo(2, 10, "2011-01-01"), new TmdbSeasonInfo(3, 10, "2012-01-01"));
        var have = Enumerable.Range(1, 30).Select(n => E(1, n)).ToList();
        Assert.Null(EpisodeGaps.Find(show, new Dictionary<int, TmdbSeason>(), have, Today));
    }

    [Fact]
    public void Nothing_understood_on_disk_compares_with_nothing()
    {
        var show = Show("Ended", new TmdbSeasonInfo(1, 3, "2008-01-20"));
        Assert.Null(EpisodeGaps.Find(show, new Dictionary<int, TmdbSeason>(), [(null, null, null)], Today));
    }

    [Fact]
    public async Task The_check_asks_only_the_seasons_with_holes_and_an_ended_show_only_once()
    {
        var root = _db.AddRoot(LibraryKind.Series, "TV", Path.Combine(_dir, "tv"));
        var show = new Series { RootId = root.Id, Folder = "Breaking Bad", Title = "Breaking Bad", TmdbId = "1396" };
        _db.SaveSeries(show, [], [],
        [
            new Episode { Season = 1, Number = 1, Title = "A", VideoFile = "Breaking Bad/S01E01.mkv" },
            new Episode { Season = 1, Number = 3, Title = "C", VideoFile = "Breaking Bad/S01E03.mkv" },
            new Episode { Season = 2, Number = 1, Title = "D", VideoFile = "Breaking Bad/S02E01.mkv" },
            new Episode { Season = 2, Number = 2, Title = "E", VideoFile = "Breaking Bad/S02E02.mkv" },
            new Episode { Season = 2, Number = 3, Title = "F", VideoFile = "Breaking Bad/S02E03.mkv" },
        ]);
        var fake = new FakeTmdb();
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", fake);
        var checker = new EpisodeChecker(_db);
        var shows = checker.Identified(_db.GetSeries());

        var result = await checker.CheckAsync(shows, client, null, _ct);

        Assert.Null(result.Error);
        Assert.Equal(["/3/tv/1396", "/3/tv/1396/season/1"], fake.Requests.Select(r => r.Split('?')[0]));
        var gaps = checker.Gaps(shows, Today)[show.Id];
        Assert.Equal(["S01E02"], gaps.Episodes.Select(e => e.Code));
        Assert.Equal("Episode 1.2", gaps.Episodes[0].Name);

        fake.Requests.Clear();
        await checker.CheckAsync(shows, client, null, _ct);
        Assert.Empty(fake.Requests);   // ended, and the season with the hole is known
    }

    [Fact]
    public async Task A_show_kept_in_two_folders_is_one_show()
    {
        var root = _db.AddRoot(LibraryKind.Series, "TV", Path.Combine(_dir, "tv"));
        var other = _db.AddRoot(LibraryKind.Series, "TV 2", Path.Combine(_dir, "tv2"));
        var first = new Series { RootId = root.Id, Folder = "Breaking Bad", Title = "Breaking Bad", TmdbId = "1396" };
        var second = new Series { RootId = other.Id, Folder = "Breaking Bad", Title = "Breaking Bad", TmdbId = "1396" };
        _db.SaveSeries(first, [], [], Enumerable.Range(1, 3).Select(n => new Episode { Season = 1, Number = n, Title = "A", VideoFile = $"Breaking Bad/S01E0{n}.mkv" }).ToList());
        _db.SaveSeries(second, [], [], Enumerable.Range(1, 3).Select(n => new Episode { Season = 2, Number = n, Title = "B", VideoFile = $"Breaking Bad/S02E0{n}.mkv" }).ToList());
        var fake = new FakeTmdb();
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", fake);
        var checker = new EpisodeChecker(_db);
        var shows = checker.Identified(_db.GetSeries());

        await checker.CheckAsync(shows, client, null, _ct);

        Assert.Equal(["/3/tv/1396"], fake.Requests.Select(r => r.Split('?')[0]));   // all there, together
        Assert.Empty(checker.Gaps(shows, Today));
    }

    [Fact]
    public async Task A_refused_key_stops_the_check_and_says_why()
    {
        var root = _db.AddRoot(LibraryKind.Series, "TV", Path.Combine(_dir, "tv"));
        _db.SaveSeries(new Series { RootId = root.Id, Folder = "Breaking Bad", Title = "Breaking Bad", TmdbId = "1396" }, [], [],
                       [new Episode { Season = 1, Number = 1, Title = "A", VideoFile = "Breaking Bad/S01E01.mkv" }]);
        using var client = new TmdbClient("bad", "en-US", new FakeTmdb { RefuseKey = true });
        var checker = new EpisodeChecker(_db);

        var result = await checker.CheckAsync(checker.Identified(_db.GetSeries()), client, null, _ct);

        Assert.NotNull(result.Error);
        Assert.False(result.Offline);   // said even at startup
    }

    // ---- needs attention ----

    [Fact]
    public void A_movie_needs_attention_for_what_its_folder_lacks()
    {
        var movie = new Movie { Title = "M", ImdbId = "tt1", Folder = "", VideoFile = "M.mkv" };
        Assert.Equal(["no poster", "no plot", "not in a folder of its own"], LibraryHealth.Problems(movie));
        Assert.Equal("No poster, no plot, not in a folder of its own", LibraryHealth.Describe(LibraryHealth.Problems(movie)));

        var fine = new Movie { Title = "M", ImdbId = "tt1", Folder = "M", VideoFile = "M/M.mkv", Plot = "P", ThumbKey = "k" };
        Assert.Empty(LibraryHealth.Problems(fine));
        Assert.Empty(LibraryHealth.Problems(new Movie { Title = "N" }));   // not identified: it has its own view
    }

    [Fact]
    public void A_show_needs_attention_for_files_without_numbers_and_episodes_twice()
    {
        var show = new Series { Title = "S", TmdbId = "1", Plot = "P", ThumbKey = "k" };
        var problems = LibraryHealth.Problems(show, [E(1, 1), E(1, 2), E(1, 2), (null, null, null), E(2, 4, 5), E(2, 5)]);
        Assert.Equal(["1 file without season and episode number", "S01E02 and S02E05 twice"], problems);
        Assert.Equal(["no episodes in the folder"], LibraryHealth.Problems(show, []));
    }

    [Fact]
    public async Task The_scan_names_the_folders_without_a_video()
    {
        var media = Path.Combine(_dir, "movies");
        Directory.CreateDirectory(Path.Combine(media, "Heat (1995)"));
        File.WriteAllBytes(Path.Combine(media, "Heat (1995)", "Heat.mkv"), new byte[1024]);
        Directory.CreateDirectory(Path.Combine(media, "Alien Collection", "Alien (1979)"));
        File.WriteAllBytes(Path.Combine(media, "Alien Collection", "Alien (1979)", "Alien.mkv"), new byte[1024]);
        Directory.CreateDirectory(Path.Combine(media, "Moved away"));
        File.WriteAllText(Path.Combine(media, "Moved away", "movie.nfo"), "<movie/>");
        Directory.CreateDirectory(Path.Combine(media, "Empty"));
        var root = _db.AddRoot(LibraryKind.Movies, "Movies", media);
        var scanner = new MovieScanner(_db, new ImageCache(Path.Combine(_dir, "posters"), 320));

        var result = await scanner.ScanAsync(root, full: false, null, _ct);
        Assert.Equal(["Empty", "Moved away"], result.EmptyFolders);

        // Also when the scan looks only at what changed
        result = await scanner.ScanAsync(root, full: false, null, _ct);
        Assert.Equal(["Empty", "Moved away"], result.EmptyFolders);
    }

    // ---- subtitles for a season ----

    private sealed class FakeOpenSubtitles : HttpMessageHandler
    {
        public List<string> Searches { get; } = [];
        public int Downloads { get; private set; }
        public int Remaining { get; set; } = 10;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            string body;
            if (uri.AbsolutePath.EndsWith("/subtitles"))
            {
                Searches.Add(uri.Query);
                body = """
                    {"data":[
                      {"id":"1","attributes":{"language":"en","download_count":900,"foreign_parts_only":true,"files":[{"file_id":1,"file_name":"forced.srt"}]}},
                      {"id":"2","attributes":{"language":"de","download_count":800,"files":[{"file_id":2,"file_name":"de.srt"}]}},
                      {"id":"3","attributes":{"language":"en","download_count":50,"machine_translated":true,"files":[{"file_id":3,"file_name":"mt.srt"}]}},
                      {"id":"4","attributes":{"language":"en","download_count":40,"files":[{"file_id":4,"file_name":"good.srt"}]}}
                    ]}
                    """;
            }
            else if (uri.AbsolutePath.EndsWith("/download"))
            {
                if (Remaining <= 0)
                    return Answer(HttpStatusCode.NotAcceptable, """{"message":"You have downloaded your allowed 10 subtitles for 24h."}""");
                Downloads++;
                Remaining--;
                body = $$"""{"link":"https://files.example/s.srt","file_name":"good.srt","remaining":{{Remaining}}}""";
            }
            else if (uri.Host == "files.example")
            {
                body = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";
            }
            else
            {
                return Answer(HttpStatusCode.NotFound, "{}");
            }
            return Answer(HttpStatusCode.OK, body);
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private SubtitleJob Job(string name, params string[] have)
    {
        var video = Path.Combine(_dir, "show", name + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        File.WriteAllBytes(video, new byte[200 * 1024]);
        return new SubtitleJob(video, name, new SubtitleQuery(ParentImdbId: "tt0903747", Season: 1, Episode: 1), have);
    }

    [Fact]
    public void An_episode_with_a_subtitle_in_the_languages_is_left_alone_also_inside_the_file()
    {
        var withFile = Job("S01E01", "en");
        var inside = Job("S01E02");
        File.WriteAllText(Path.ChangeExtension(inside.VideoPath, ".nfo"), """
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <episodedetails><fileinfo><streamdetails>
              <video><codec>h264</codec></video>
              <subtitle><language>eng</language></subtitle>
            </streamdetails></fileinfo></episodedetails>
            """);
        var forced = Job("S01E03", "en forced");
        var none = Job("S01E04", "de");

        Assert.False(SeasonSubtitles.Lacks(withFile, ["en", "it"]));
        Assert.False(SeasonSubtitles.Lacks(inside, ["en", "it"]));
        Assert.True(SeasonSubtitles.Lacks(forced, ["en"]));   // only the foreign lines
        Assert.True(SeasonSubtitles.Lacks(none, ["en", "it"]));
        Assert.Equal(["en"], SeasonSubtitles.EmbeddedLanguages(inside.VideoPath));
    }

    [Fact]
    public void The_pick_is_in_the_languages_by_a_person_and_never_forced_only()
    {
        var results = new[]
        {
            new SubtitleResult("1", "1", "f.srt", "en", null, 900, false, false, false, Forced: true, null, null),
            new SubtitleResult("2", "2", "d.srt", "de", null, 800, false, false, false, false, null, null),
            new SubtitleResult("3", "3", "m.srt", "en", null, 50, false, false, MachineTranslated: true, false, null, null),
            new SubtitleResult("4", "4", "g.srt", "en", null, 40, false, false, false, false, null, null),
            new SubtitleResult("5", "5", "p.srt", "pt-BR", null, 30, false, false, false, false, null, null),
        };
        Assert.Equal("4", SeasonSubtitles.Pick(results, ["en"], skipMachineTranslated: true)!.FileId);
        Assert.Equal("5", SeasonSubtitles.Pick(results, ["pt"], skipMachineTranslated: true)!.FileId);
        Assert.Null(SeasonSubtitles.Pick(results, ["it"], skipMachineTranslated: true));
    }

    [Fact]
    public async Task Each_episode_gets_the_best_one_next_to_it_until_the_quota_ends()
    {
        var fake = new FakeOpenSubtitles { Remaining = 2 };
        using var client = new OpenSubtitlesClient("KEY", fake);
        var jobs = new[] { Job("S01E01"), Job("S01E02"), Job("S01E03") };
        var batch = new SeasonSubtitles(client) { Pause = TimeSpan.Zero };

        var result = await batch.RunAsync(jobs, ["en"], skipMachineTranslated: true, null, null, _ct);

        Assert.Equal(2, result.Saved);
        Assert.Equal(0, result.Remaining);
        Assert.Equal("No downloads left today on OpenSubtitles.", result.Stopped);
        Assert.Equal(2, fake.Searches.Count);   // the third wasn't even looked for
        Assert.True(File.Exists(Path.Combine(_dir, "show", "S01E01.en.srt")));
        Assert.True(File.Exists(Path.Combine(_dir, "show", "S01E02.en.srt")));
        Assert.Contains("moviehash=", fake.Searches[0]);
    }

    [Fact]
    public async Task A_quota_already_over_stops_at_the_first()
    {
        var fake = new FakeOpenSubtitles { Remaining = 0 };
        using var client = new OpenSubtitlesClient("KEY", fake);
        var batch = new SeasonSubtitles(client) { Pause = TimeSpan.Zero };

        var result = await batch.RunAsync([Job("S01E01"), Job("S01E02")], ["en"], true, null, null, _ct);

        Assert.Equal(0, result.Saved);
        Assert.Equal(1, result.Failed);
        Assert.Contains("allowed 10", result.Stopped);
    }

    // ---- chapters and the intro ----

    [Fact]
    public void A_chapter_called_intro_is_the_intro_when_it_is_short_and_early()
    {
        var chapters = PlaybackRules.Chapters([("Cold open", 0, 95_000), ("Opening Credits", 95_000, 0), ("", 150_000, 600_000), ("Part 2", 750_000, 0)]);
        Assert.Equal(["Cold open", "Opening Credits", "Chapter 3", "Part 2"], chapters.Select(c => c.Name));
        Assert.Equal((95_000L, 150_000L), PlaybackRules.IntroChapter(chapters));   // to the next chapter

        Assert.Null(PlaybackRules.IntroChapter(PlaybackRules.Chapters([("Intro", 0, 20 * 60_000)])));        // a story part
        Assert.Null(PlaybackRules.IntroChapter(PlaybackRules.Chapters([("Introduction to Evil", 0, 60_000)])));   // only the whole name
        Assert.Null(PlaybackRules.IntroChapter(PlaybackRules.Chapters([("Chapter 1", 0, 60_000), ("Intro", 40 * 60_000, 60_000)])));
    }
}
