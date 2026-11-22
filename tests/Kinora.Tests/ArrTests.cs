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
        public bool Down { get; set; }

        private string What => app == "Radarr" ? "movie" : "series";

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
