using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kinora.Core.Arr;
using Kinora.Core.Models;
using Kinora.Core.Settings;

namespace Kinora.Tests;

public sealed class ArrTests
{
    [Theory]
    [InlineData("nas:7878", "http://nas:7878")]
    [InlineData("http://nas:8989/sonarr/", "http://nas:8989/sonarr")]
    [InlineData("https://arr.example.org/radarr/api/v3/movie", "https://arr.example.org/radarr")]
    public void Addresses_are_cleaned(string written, string used) => Assert.Equal(used, ArrClient.CleanUrl(written));

    [Fact]
    public async Task The_test_tells_a_wrong_key_and_the_wrong_program()
    {
        var radarr = new FakeArr("Radarr");
        using (var client = new ArrClient(ArrKind.Radarr, "http://nas:7878", "wrong", radarr))
            Assert.Equal("Radarr refused this key.", (await Assert.ThrowsAsync<ArrException>(() => client.TestAsync(CancellationToken.None))).Message);
        using (var client = new ArrClient(ArrKind.Sonarr, "http://nas:7878", FakeArr.Key, radarr))
            Assert.Equal("That is Radarr, not Sonarr.", (await Assert.ThrowsAsync<ArrException>(() => client.TestAsync(CancellationToken.None))).Message);

        // A page copied from the browser: the API is under host and port
        using (var client = new ArrClient(ArrKind.Radarr, "http://nas:7878/settings/general", FakeArr.Key, radarr))
        {
            var status = await client.TestAsync(CancellationToken.None);
            Assert.Equal(("Radarr", "5.14.0.9383"), (status.AppName, status.Version));
        }
    }

    [Fact]
    public async Task A_movie_is_found_by_its_ids_and_removed_without_its_files()
    {
        var radarr = new FakeArr("Radarr");
        radarr.Titles.Add(new JsonObject { ["id"] = 7, ["title"] = "Heat", ["tmdbId"] = 949, ["imdbId"] = "tt0113277" });
        radarr.Titles.Add(new JsonObject { ["id"] = 8, ["title"] = "Up", ["tmdbId"] = 14160, ["imdbId"] = "tt1049413" });
        using var client = new ArrClient(ArrKind.Radarr, "nas:7878", FakeArr.Key, radarr);

        Assert.Equal(new ArrTitle(7, "Heat"), await client.FindMovieAsync("949", null, CancellationToken.None));
        Assert.Equal(new ArrTitle(8, "Up"), await client.FindMovieAsync(null, "TT1049413", CancellationToken.None));   // no TMDB id: by IMDb's
        Assert.Equal(new ArrTitle(8, "Up"), await client.FindMovieAsync("1", "tt1049413", CancellationToken.None));     // a TMDB id Radarr hasn't got
        Assert.Null(await client.FindMovieAsync("603", "tt0133093", CancellationToken.None));
        Assert.Null(await client.FindMovieAsync(null, null, CancellationToken.None));

        await client.RemoveAsync(new ArrTitle(7, "Heat"), exclude: true, CancellationToken.None);
        Assert.Equal("DELETE /api/v3/movie/7?deleteFiles=false&addImportExclusion=true", radarr.Changes.Single());
        Assert.DoesNotContain(radarr.Titles, t => (int)t!["id"]! == 7);
    }

    [Fact]
    public async Task A_show_is_found_by_tvdb_and_an_episode_stops_being_monitored()
    {
        var sonarr = new FakeArr("Sonarr");
        sonarr.Titles.Add(new JsonObject { ["id"] = 3, ["title"] = "Severance", ["tvdbId"] = 371980, ["imdbId"] = "tt11280740", ["tmdbId"] = 95396 });
        for (var n = 1; n <= 4; n++)
            sonarr.Episodes.Add(new JsonObject { ["id"] = 100 + n, ["seriesId"] = 3, ["seasonNumber"] = 1, ["episodeNumber"] = n, ["monitored"] = true });
        sonarr.Episodes.Add(new JsonObject { ["id"] = 201, ["seriesId"] = 3, ["seasonNumber"] = 2, ["episodeNumber"] = 1, ["monitored"] = true });
        using var client = new ArrClient(ArrKind.Sonarr, "nas:8989", FakeArr.Key, sonarr);

        var show = await client.FindSeriesAsync("371980", null, null, CancellationToken.None);
        Assert.Equal(new ArrTitle(3, "Severance"), show);
        Assert.Equal(show, await client.FindSeriesAsync(null, null, "95396", CancellationToken.None));
        Assert.Null(await client.FindSeriesAsync("1", "tt0000001", null, CancellationToken.None));

        Assert.Equal(2, await client.UnmonitorEpisodesAsync(3, 1, [2, 3], CancellationToken.None));
        Assert.Equal("PUT /api/v3/episode/monitor {\"episodeIds\":[102,103],\"monitored\":false}", sonarr.Changes.Single());

        await client.RemoveAsync(show!, exclude: false, CancellationToken.None);
        Assert.Equal("DELETE /api/v3/series/3?deleteFiles=false&addImportListExclusion=false", sonarr.Changes[1]);
    }

    [Fact]
    public async Task The_link_asks_only_what_the_settings_have_and_tells_what_went_wrong()
    {
        var sonarr = new FakeArr("Sonarr");
        sonarr.Titles.Add(new JsonObject { ["id"] = 3, ["title"] = "Severance", ["tvdbId"] = 371980 });
        sonarr.Episodes.Add(new JsonObject { ["id"] = 101, ["seriesId"] = 3, ["seasonNumber"] = 1, ["episodeNumber"] = 1 });
        sonarr.Episodes.Add(new JsonObject { ["id"] = 102, ["seriesId"] = 3, ["seasonNumber"] = 1, ["episodeNumber"] = 2 });
        var settings = new AppSettings { SonarrUrl = "nas:8989", SonarrKey = FakeArr.Key };
        var link = new ArrLink(settings, sonarr);

        Assert.False(link.HasRadarr);
        Assert.Null(await link.FindAsync(new Movie { Title = "Heat", TmdbId = "949" }, CancellationToken.None));

        var found = await link.FindAsync(new Series { Title = "Severance", TvdbId = "371980" }, CancellationToken.None);
        Assert.True(found!.Has);
        // One file with two episodes: both stop
        Assert.Null(await link.UnmonitorAsync(found, new Episode { Season = 1, Number = 1, NumberEnd = 2 }, CancellationToken.None));
        Assert.Contains("[101,102]", sonarr.Changes.Single());
        Assert.Equal("Sonarr has no S03E01 in “Severance”",
                     await link.UnmonitorAsync(found, new Episode { Season = 3, Number = 1 }, CancellationToken.None));

        Assert.Null(await link.RemoveAsync(found, CancellationToken.None));
        Assert.EndsWith("addImportListExclusion=true", sonarr.Changes.Last());   // the setting's default
        Assert.False((await link.FindAsync(new Series { Title = "Severance", TvdbId = "371980" }, CancellationToken.None))!.Has);

        sonarr.Down = true;
        var down = await link.FindAsync(new Series { Title = "Severance", TvdbId = "371980" }, CancellationToken.None);
        Assert.False(down!.Has);
        Assert.NotNull(down.Error);
        Assert.Equal(down.Error, await link.RemoveAsync(new ArrFound(ArrKind.Sonarr, new ArrTitle(3, "Severance")), CancellationToken.None));
    }

    [Fact]
    public async Task A_movie_is_added_with_the_chosen_profile_and_folder_and_only_once()
    {
        var radarr = new FakeArr("Radarr");
        radarr.Lookups.Add(new JsonObject { ["title"] = "Heat", ["year"] = 1995, ["tmdbId"] = 949, ["images"] = new JsonArray(), ["id"] = 0 });
        var settings = new AppSettings { RadarrUrl = "nas:7878", RadarrKey = FakeArr.Key, RadarrProfileId = 6, RadarrRootFolder = "/more" };
        var link = new ArrLink(settings, radarr);

        var added = await link.AddMovieAsync("949", CancellationToken.None);
        Assert.False(added.Already);
        Assert.Equal("Heat", added.Title.Title);
        var post = radarr.Changes.Single();
        Assert.StartsWith("POST /api/v3/movie ", post);
        var body = JsonNode.Parse(post[(post.IndexOf('{'))..])!;
        Assert.Equal(6, (int)body["qualityProfileId"]!);
        Assert.Equal("/more", (string)body["rootFolderPath"]!);
        Assert.True((bool)body["monitored"]!);
        Assert.True((bool)body["addOptions"]!["searchForMovie"]!);
        Assert.Null(body["images"]);   // only what Radarr needs

        // Again: it has it, nothing is sent
        Assert.True((await link.AddMovieAsync("949", CancellationToken.None)).Already);
        Assert.Single(radarr.Changes);

        // A profile and a folder that are gone: the first ones
        settings.RadarrProfileId = 99;
        settings.RadarrRootFolder = "/gone";
        radarr.Lookups.Add(new JsonObject { ["title"] = "Up", ["year"] = 2009, ["tmdbId"] = 14160 });
        await link.AddMovieAsync("14160", CancellationToken.None);
        Assert.Contains("\"qualityProfileId\":4", radarr.Changes.Last());
        Assert.Contains("\"rootFolderPath\":\"/movie\"", radarr.Changes.Last());

        Assert.Equal("Radarr doesn't find this movie.",
                     (await Assert.ThrowsAsync<ArrException>(() => link.AddMovieAsync("1", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_show_is_added_by_tvdb_with_the_episodes_chosen()
    {
        var sonarr = new FakeArr("Sonarr");
        sonarr.Lookups.Add(new JsonObject { ["title"] = "Severance", ["tvdbId"] = 371980, ["seriesType"] = "standard", ["seasons"] = new JsonArray() });
        var settings = new AppSettings { SonarrUrl = "nas:8989", SonarrKey = FakeArr.Key, SonarrMonitor = "firstSeason" };
        var link = new ArrLink(settings, sonarr);

        var added = await link.AddSeriesAsync("371980", CancellationToken.None);
        Assert.Equal(("Severance", false), (added.Title.Title, added.Already));
        var post = sonarr.Changes.Single();
        var body = JsonNode.Parse(post[(post.IndexOf('{'))..])!;
        Assert.Equal(4, (int)body["qualityProfileId"]!);
        Assert.Equal("/series", (string)body["rootFolderPath"]!);
        Assert.Equal("firstSeason", (string)body["addOptions"]!["monitor"]!);
        Assert.True((bool)body["addOptions"]!["searchForMissingEpisodes"]!);
        Assert.Equal("standard", (string)body["seriesType"]!);   // the lookup goes back as it came
        Assert.Null(body["languageProfileId"]);                    // Sonarr 4 has none

        Assert.True((await link.AddSeriesAsync("371980", CancellationToken.None)).Already);
        await Assert.ThrowsAsync<ArrException>(() => link.AddSeriesAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task The_calendar_puts_episodes_and_movies_together_by_day()
    {
        var today = new DateTime(2026, 11, 22);
        var sonarr = new FakeArr("Sonarr")
        {
            Calendar =
            [
                new JsonObject
                {
                    ["seasonNumber"] = 2, ["episodeNumber"] = 5, ["title"] = "Trojan's Horse", ["airDateUtc"] = "2026-11-23T02:00:00Z", ["hasFile"] = false,
                    ["series"] = new JsonObject { ["title"] = "Severance", ["tvdbId"] = 371980, ["imdbId"] = "tt11280740", ["tmdbId"] = 95396 },
                },
                new JsonObject
                {
                    ["seasonNumber"] = 1, ["episodeNumber"] = 1, ["title"] = "TBA", ["airDateUtc"] = "2026-11-22T20:00:00Z", ["hasFile"] = true,
                    ["series"] = new JsonObject { ["title"] = "Andor", ["tvdbId"] = 393189 },
                },
            ],
        };
        var radarr = new FakeArr("Radarr")
        {
            Calendar =
            [
                // in cinemas before the days asked, digitally inside: only the digital one
                new JsonObject { ["title"] = "Heat 2", ["tmdbId"] = 1, ["inCinemas"] = "2026-10-01T00:00:00Z", ["digitalRelease"] = "2026-11-23T00:00:00Z", ["hasFile"] = false },
            ],
        };
        var settings = new AppSettings { SonarrUrl = "nas:8989", SonarrKey = FakeArr.Key, RadarrUrl = "nas:7878", RadarrKey = FakeArr.Key };
        var link = new ArrLink(settings, new ByPort(sonarr, radarr));

        var (releases, problems) = await link.CalendarAsync(today, 7, CancellationToken.None);
        Assert.Empty(problems);
        Assert.Equal(3, releases.Count);
        var andor = releases.Single(r => r.Title == "Andor");
        Assert.Equal(("S01E01", true, true), (andor.What, andor.HasTime, andor.HasFile));
        var movie = releases.Single(r => r.Kind == ArrKind.Radarr);
        Assert.Equal(("Digital release", false, new DateTime(2026, 11, 23)), (movie.What, movie.HasTime, movie.When.Date));
        var severance = releases.Single(r => r.Title == "Severance");
        Assert.Equal(("S02E05", "Trojan's Horse", "371980", "95396"), (severance.What, severance.EpisodeTitle, severance.TvdbId, severance.TmdbId));
        // By day, and on a day the hours first, the movies after
        Assert.True(releases.IndexOf(andor) < releases.IndexOf(severance) || andor.When.Date < severance.When.Date);
        Assert.Contains("unmonitored=false", sonarr.Asked.Single());
        Assert.Contains("includeSeries=true", sonarr.Asked.Single());

        // One of the two down: the other's are there, and what went wrong
        radarr.Down = true;
        (releases, problems) = await link.CalendarAsync(today, 7, CancellationToken.None);
        Assert.Equal(2, releases.Count);
        Assert.StartsWith("Radarr: ", problems.Single());
    }

    /// <summary>Two fakes behind one handler: Sonarr on 8989, Radarr on 7878.</summary>
    private sealed class ByPort(FakeArr sonarr, FakeArr radarr) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = request.RequestUri!.Port == 8989 ? sonarr : radarr;
            return target.Send(request, ct);
        }
    }

    /// <summary>
    /// Radarr or Sonarr as far as Kinora uses them, at any address: the API under the host,
    /// the web pages elsewhere. Changes are kept, in order.
    /// </summary>
    private sealed class FakeArr(string app) : HttpMessageHandler
    {
        public const string Key = "0123456789abcdef0123456789abcdef";

        public JsonArray Titles { get; } = [];
        public JsonArray Episodes { get; } = [];
        public List<string> Changes { get; } = [];
        public JsonArray Lookups { get; } = [];
        public JsonArray Calendar { get; set; } = [];
        public List<string> Asked { get; } = [];
        public bool Down { get; set; }

        private string What => app == "Radarr" ? "movie" : "series";

        public Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down)
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (!path.StartsWith("/api/v3/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Radarr</html>", Encoding.UTF8, "text/html") };
            if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != Key)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var method = request.Method.Method;

            if (method == "GET" && path == "/api/v3/system/status")
                return Json(new JsonObject { ["appName"] = app, ["instanceName"] = app, ["version"] = app == "Radarr" ? "5.14.0.9383" : "4.0.11.2680" });
            if (method == "GET" && path == $"/api/v3/{What}")
            {
                var by = app == "Radarr" ? "tmdbId" : "tvdbId";
                var items = Titles.OfType<JsonObject>().Where(t => query[by] is not { } id || t[by]?.ToString() == id);
                return Json(new JsonArray(items.Select(t => t.DeepClone()).ToArray()));
            }
            if (method == "GET" && path == "/api/v3/episode")
                return Json(new JsonArray(Episodes.OfType<JsonObject>().Where(e => e["seriesId"]!.ToString() == query["seriesId"]).Select(e => e.DeepClone()).ToArray()));
            if (method == "PUT" && path == "/api/v3/episode/monitor")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Changes.Add($"PUT {path} {body}");
                return Json(new JsonArray());
            }
            if (method == "GET" && path == "/api/v3/qualityprofile")
                return Json(new JsonArray(new JsonObject { ["id"] = 4, ["name"] = "HD-1080p" }, new JsonObject { ["id"] = 6, ["name"] = "Ultra-HD" }));
            if (method == "GET" && path == "/api/v3/rootfolder")
                return Json(new JsonArray(new JsonObject { ["id"] = 1, ["path"] = $"/{What}" }, new JsonObject { ["id"] = 2, ["path"] = "/more" }));
            if (method == "GET" && path == "/api/v3/movie/lookup/tmdb")
                return Lookups.OfType<JsonObject>().FirstOrDefault(l => l["tmdbId"]?.ToString() == query["tmdbId"]) is { } m
                    ? Json(m.DeepClone()) : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (method == "GET" && path == "/api/v3/series/lookup")
                return Json(new JsonArray(Lookups.OfType<JsonObject>().Where(l => "tvdb:" + l["tvdbId"] == query["term"]).Select(l => l.DeepClone()).ToArray()));
            if (method == "POST" && path == $"/api/v3/{What}")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                Changes.Add($"POST {path} {body.ToJsonString()}");
                body["id"] = 50 + Titles.Count;
                Titles.Add(body.DeepClone());
                return Json(body);
            }
            if (method == "GET" && path == "/api/v3/calendar")
            {
                Asked.Add(uri.Query);
                return Json(Calendar.DeepClone());
            }
            if (method == "DELETE" && path.StartsWith($"/api/v3/{What}/", StringComparison.Ordinal))
            {
                Changes.Add($"DELETE {uri.PathAndQuery}");
                var id = int.Parse(path[(path.LastIndexOf('/') + 1)..]);
                var title = Titles.OfType<JsonObject>().FirstOrDefault(t => (int)t["id"]! == id);
                if (title is null)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                Titles.Remove(title);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonNode node) =>
            new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
