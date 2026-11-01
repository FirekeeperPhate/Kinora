using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kinora.Core.Data;
using Kinora.Core.MediaServers;
using Kinora.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kinora.Tests;

/// <summary>A server in a handler: what it was asked is in <see cref="Requests"/>, what it was told in <see cref="Written"/>.</summary>
internal sealed class FakeMediaServer(string product = "") : HttpMessageHandler
{
    public const string Key = "test-key";

    public List<JsonObject> Items { get; } = [];
    public List<string> Requests { get; } = [];
    public List<(string Method, string Path)> Written { get; } = [];
    public Dictionary<string, string> Bodies { get; } = [];
    public string Prefix { get; init; } = "";
    public int PageLimit { get; init; } = int.MaxValue;
    public HashSet<string> Refused { get; } = [];
    public HashSet<string> Gone { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add($"{request.Method} {path}");
        if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal))
            return Answer(HttpStatusCode.NotFound, "<html>not here</html>");
        path = path[Prefix.Length..];
        if (path == "/System/Info/Public")
            return Answer(HttpStatusCode.OK, new JsonObject { ["ServerName"] = "Home", ["Version"] = "4.8.0", ["ProductName"] = product }.ToJsonString());
        if (!request.Headers.TryGetValues("X-Emby-Token", out var key) || key.Single() != Key)
            return Answer(HttpStatusCode.Unauthorized, "");
        if (Gone.Any(path.StartsWith))
            return Answer(HttpStatusCode.NotFound, "");
        if (Refused.Any(path.Contains))
            return Answer(HttpStatusCode.InternalServerError, "");

        if (path == "/Users")
            return Answer(HttpStatusCode.OK, """[{"Id":"u2","Name":"Zoe"},{"Id":"u1","Name":"Ann"}]""");
        if (request.Method == HttpMethod.Get && path.Contains("/Items?", StringComparison.Ordinal))
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var start = int.Parse(query["StartIndex"]!);
            var limit = Math.Min(int.Parse(query["Limit"]!), PageLimit);
            var page = new JsonArray(Items.Skip(start).Take(limit).Select(i => (JsonNode)i.DeepClone()).ToArray());
            return Answer(HttpStatusCode.OK, new JsonObject { ["Items"] = page, ["TotalRecordCount"] = Items.Count }.ToJsonString());
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/Users/u1/Items/", StringComparison.Ordinal))
            return Answer(HttpStatusCode.OK, """{"Id":"x","UserData":{"IsFavorite":true,"PlayCount":3,"PlaybackPositionTicks":0}}""");
        if (request.Method == HttpMethod.Get && path.StartsWith("/UserItems/", StringComparison.Ordinal))
            return Answer(HttpStatusCode.OK, """{"IsFavorite":true,"PlaybackPositionTicks":0}""");

        Written.Add((request.Method.Method, path));
        if (request.Content is not null)
            Bodies[path] = await request.Content.ReadAsStringAsync(cancellationToken);
        return Answer(HttpStatusCode.OK, "{}");
    }

    private static HttpResponseMessage Answer(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static JsonObject Movie(string id, string path, string name = "M", int year = 2000, bool played = false, long minutes = 0,
                                   string? imdb = null, string? tmdb = null, string? lastPlayed = null) =>
        new()
        {
            ["Id"] = id, ["Type"] = "Movie", ["Name"] = name, ["Path"] = path, ["ProductionYear"] = year,
            ["RunTimeTicks"] = 120 * 60_000 * 10_000L,
            ["ProviderIds"] = new JsonObject { ["Imdb"] = imdb, ["Tmdb"] = tmdb },
            ["UserData"] = new JsonObject { ["Played"] = played, ["PlaybackPositionTicks"] = minutes * 60_000 * 10_000L, ["LastPlayedDate"] = lastPlayed },
        };

    public static JsonObject Episode(string id, string path, string show, int season, int number, bool played = false, string? seriesId = null) =>
        new()
        {
            ["Id"] = id, ["Type"] = "Episode", ["Name"] = "E", ["Path"] = path, ["SeriesName"] = show, ["SeriesId"] = seriesId,
            ["ParentIndexNumber"] = season, ["IndexNumber"] = number, ["RunTimeTicks"] = 40 * 60_000 * 10_000L,
            ["UserData"] = new JsonObject { ["Played"] = played, ["PlaybackPositionTicks"] = 0 },
        };

    public static JsonObject Show(string id, string name, string? tvdb) =>
        new() { ["Id"] = id, ["Type"] = "Series", ["Name"] = name, ["ProviderIds"] = new JsonObject { ["Tvdb"] = tvdb } };
}

public sealed class MediaServerTests : IDisposable
{
    private const long Minute = 60_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-server-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly LibraryRoot _root;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public MediaServerTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
        _root = _db.AddRoot(LibraryKind.Movies, "Movies", "M:/Movies");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Movie AddMovie(string file, string title = "M", int? year = 2000, bool watched = false, string? imdb = null)
    {
        var movie = new Movie { RootId = _root.Id, VideoFile = file, Folder = Path.GetDirectoryName(file) ?? "", Title = title, Year = year, Watched = watched, ImdbId = imdb };
        _db.SaveMovies([(movie, [])]);
        return _db.GetMovies().Single(m => m.Id == movie.Id);
    }

    private static MediaServerClient Client(FakeMediaServer server, string key = FakeMediaServer.Key) => new("http://nas:8096", key, server);

    private async Task<(SyncPlan Plan, MediaServerSync Sync)> PlanAsync(FakeMediaServer server)
    {
        var client = Client(server);
        await client.ConnectAsync(_ct);
        var sync = new MediaServerSync(_db, client, "u1");
        return (await sync.PlanAsync(_ct), sync);
    }

    private async Task<SyncResult> RunAsync(FakeMediaServer server)
    {
        var (plan, sync) = await PlanAsync(server);
        return await sync.ApplyAsync(plan, null, _ct);
    }

    // ---- the server ----

    [Theory]
    [InlineData("http://nas:8096", "http://nas:8096")]
    [InlineData(" nas:8096/ ", "http://nas:8096")]
    [InlineData("https://media.example.com/emby", "https://media.example.com/emby")]
    [InlineData("http://nas:8096/web/index.html#!/home", "http://nas:8096")]
    public void The_address_as_people_paste_it(string url, string clean) =>
        Assert.Equal(clean, MediaServerClient.CleanUrl(url));

    [Fact]
    public void Something_that_is_no_address_is_said()
    {
        Assert.Throws<MediaServerException>(() => MediaServerClient.CleanUrl("ftp://nas"));
        Assert.Throws<MediaServerException>(() => MediaServerClient.CleanUrl("http://"));
    }

    [Theory]
    [InlineData("", MediaServerKind.Emby)]
    [InlineData("Jellyfin Server", MediaServerKind.Jellyfin)]
    public async Task It_tells_Emby_from_Jellyfin(string product, MediaServerKind kind)
    {
        using var client = Client(new FakeMediaServer(product));
        var info = await client.ConnectAsync(_ct);
        Assert.Equal(kind, info.Kind);
        Assert.Equal("Home", info.Name);
        Assert.Equal("4.8.0", info.Version);
    }

    [Fact]
    public async Task Emby_found_under_its_prefix()
    {
        var server = new FakeMediaServer { Prefix = "/emby" };
        using var client = Client(server);
        await client.ConnectAsync(_ct);
        Assert.Equal(["u1", "u2"], (await client.GetUsersAsync(_ct)).Select(u => u.Id));   // by name
        Assert.StartsWith("GET /emby/Users", server.Requests[^1]);
    }

    [Fact]
    public async Task No_server_and_a_wrong_key_are_said()
    {
        using (var nothing = new MediaServerClient("http://nas", "k", new FakeMediaServer { Prefix = "/other" }))
            await Assert.ThrowsAsync<MediaServerException>(() => nothing.ConnectAsync(_ct));

        using var client = Client(new FakeMediaServer(), "wrong");
        await client.ConnectAsync(_ct);
        var e = await Assert.ThrowsAsync<MediaServerException>(() => client.GetUsersAsync(_ct));
        Assert.Contains("refused", e.Message);
    }

    [Fact]
    public async Task Everything_comes_a_page_at_a_time()
    {
        var server = new FakeMediaServer { PageLimit = 2 };
        for (var n = 0; n < 5; n++)
            server.Items.Add(FakeMediaServer.Movie($"m{n}", $"/x/{n}.mkv", minutes: 10, lastPlayed: "2026-10-01T10:00:00.0000000Z", imdb: "tt" + n));
        using var client = Client(server);
        await client.ConnectAsync(_ct);

        var items = await client.GetItemsAsync("u1", _ct);

        Assert.Equal(5, items.Count);
        Assert.Equal(3, server.Requests.Count(r => r.Contains("/Items?")));
        Assert.Equal(10 * Minute, items[0].PositionMs);
        Assert.Equal(120 * Minute, items[0].RunTimeMs);
        Assert.Equal("tt0", items[0].ImdbId);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), items[0].LastPlayed);
    }

    [Fact]
    public async Task A_position_keeps_the_rest_of_what_the_user_has_on_Emby()
    {
        var server = new FakeMediaServer();
        using var client = Client(server);
        await client.ConnectAsync(_ct);

        await client.SetPositionAsync("u1", "m1", 5 * Minute, _ct);

        var body = JsonNode.Parse(server.Bodies["/Users/u1/Items/m1/UserData"])!;
        Assert.Equal(5 * Minute * 10_000, body["PlaybackPositionTicks"]!.GetValue<long>());
        Assert.True(body["IsFavorite"]!.GetValue<bool>());
        Assert.Equal(3, body["PlayCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Jellyfin_uses_its_new_routes_and_the_old_ones_where_those_are_missing()
    {
        var server = new FakeMediaServer("Jellyfin Server");
        using var client = Client(server);
        await client.ConnectAsync(_ct);
        await client.SetPlayedAsync("u1", "m1", true, _ct);
        await client.SetPositionAsync("u1", "m1", Minute, _ct);
        Assert.Contains(("POST", "/UserPlayedItems/m1?userId=u1"), server.Written);
        Assert.Contains(("POST", "/UserItems/m1/UserData?userId=u1"), server.Written);

        var old = new FakeMediaServer("Jellyfin Server");
        old.Gone.Add("/UserPlayedItems");
        using var oldClient = Client(old);
        await oldClient.ConnectAsync(_ct);
        await oldClient.SetPlayedAsync("u1", "m1", false, _ct);
        Assert.Equal(("DELETE", "/Users/u1/PlayedItems/m1"), old.Written.Single());
    }

    // ---- who wins ----

    private static readonly SyncBase Seen = new(true, 0);
    private static readonly SyncBase NotSeen = new(false, 0);

    private static LocalState Here(bool watched = false, long position = 0, long updated = 0) => new(watched, position, 120 * Minute, updated);

    private static RemoteState There(bool played = false, long position = 0, long updated = 0) => new(played, position, 120 * Minute, updated);

    [Fact]
    public void The_first_time_watched_wins_on_both_sides()
    {
        Assert.Equal(new SyncDecision(true, true, false, PositionMove.None, 0), SyncMerge.Decide(Here(), There(played: true), null));
        Assert.Equal(new SyncDecision(true, false, true, PositionMove.None, 0), SyncMerge.Decide(Here(watched: true), There(), null));
        Assert.Equal(new SyncDecision(false, false, false, PositionMove.None, 0), SyncMerge.Decide(Here(), There(), null));
    }

    [Fact]
    public void After_that_the_side_that_changed_wins()
    {
        Assert.Equal(new SyncDecision(false, false, true, PositionMove.None, 0), SyncMerge.Decide(Here(), There(played: true), Seen));      // unmarked here
        Assert.Equal(new SyncDecision(false, true, false, PositionMove.None, 0), SyncMerge.Decide(Here(watched: true), There(), Seen));     // unmarked there
        Assert.Equal(new SyncDecision(true, false, true, PositionMove.None, 0), SyncMerge.Decide(Here(watched: true), There(), NotSeen));   // seen here
        Assert.Equal(new SyncDecision(true, true, false, PositionMove.None, 0), SyncMerge.Decide(Here(), There(played: true), NotSeen));    // seen there
    }

    [Fact]
    public void Seen_on_the_server_clears_the_place_kept_here()
    {
        var d = SyncMerge.Decide(Here(position: 30 * Minute), There(played: true), NotSeen);
        Assert.True(d.WatchedToKinora);
        Assert.Equal(PositionMove.ClearKinora, d.Position);
        Assert.Equal(0, d.FinalPositionMs);
    }

    [Fact]
    public void A_place_goes_where_it_is_missing()
    {
        Assert.Equal(PositionMove.ToKinora, SyncMerge.Decide(Here(), There(position: 30 * Minute), null).Position);
        Assert.Equal(PositionMove.ToServer, SyncMerge.Decide(Here(position: 30 * Minute), There(), null).Position);
        Assert.Equal(PositionMove.None, SyncMerge.Decide(Here(position: 30 * Minute), There(position: 30 * Minute + 3_000), null).Position);
    }

    [Fact]
    public void The_place_that_moved_wins_and_otherwise_the_latest()
    {
        var last = new SyncBase(false, 10 * Minute);
        Assert.Equal(PositionMove.ToServer, SyncMerge.Decide(Here(position: 20 * Minute), There(position: 10 * Minute), last).Position);
        Assert.Equal(PositionMove.ToKinora, SyncMerge.Decide(Here(position: 10 * Minute), There(position: 20 * Minute), last).Position);
        Assert.Equal(PositionMove.ClearKinora, SyncMerge.Decide(Here(position: 10 * Minute), There(), last).Position);   // gone there

        Assert.Equal(PositionMove.ToKinora, SyncMerge.Decide(Here(position: 20 * Minute, updated: 100), There(position: 30 * Minute, updated: 200), last).Position);
        Assert.Equal(PositionMove.ToServer, SyncMerge.Decide(Here(position: 20 * Minute, updated: 300), There(position: 30 * Minute, updated: 200), last).Position);
    }

    [Fact]
    public void A_place_in_the_first_minute_or_the_credits_is_no_place()
    {
        Assert.Equal(PositionMove.None, SyncMerge.Decide(Here(), There(position: 30_000), null).Position);
        Assert.Equal(PositionMove.None, SyncMerge.Decide(Here(), There(position: 115 * Minute), null).Position);
    }

    // ---- matching ----

    [Fact]
    public async Task By_the_end_of_the_path_whatever_the_server_calls_the_share()
    {
        AddMovie("Alien (1979)/Alien.mkv");
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", @"\\172.16.0.50\Movies\ALIEN (1979)\alien.mkv", played: true));
        server.Items.Add(FakeMediaServer.Movie("b", "/mnt/other/Elsewhere.mkv", name: "Elsewhere"));

        var (plan, _) = await PlanAsync(server);

        Assert.Equal("a", plan.Pairs.Single().Server.Id);
        Assert.Equal(1, plan.NotFound);
    }

    [Fact]
    public async Task By_ids_and_then_by_name_and_year_when_the_path_says_nothing()
    {
        var byId = AddMovie("One/One.mkv", "One", imdb: "tt1");
        var byName = AddMovie("Two/Two.mkv", "Two: The Sequel", 2001);
        AddMovie("Three/Three.mkv", "Three", 2001);
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/renamed/A.mkv", name: "Other name", imdb: "tt1"));
        server.Items.Add(FakeMediaServer.Movie("b", "/renamed/B.mkv", name: "two the sequel", year: 2001));
        server.Items.Add(FakeMediaServer.Movie("c", "/renamed/C.mkv", name: "Three", year: 1999));

        var (plan, _) = await PlanAsync(server);

        Assert.Equal("a", plan.Pairs.Single(p => p.LocalId == byId.Id).Server.Id);
        Assert.Equal("b", plan.Pairs.Single(p => p.LocalId == byName.Id).Server.Id);
        Assert.Equal(2, plan.Pairs.Count);
        Assert.Equal(1, plan.NotFound);
    }

    [Fact]
    public async Task Each_copy_goes_with_its_own_path()
    {
        var first = AddMovie("A/Movie 1080p.mkv", imdb: "tt1");
        var second = AddMovie("B/Movie 4K.mkv", imdb: "tt1");
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("hd", "/m/A/Movie 1080p.mkv", imdb: "tt1"));
        server.Items.Add(FakeMediaServer.Movie("uhd", "/m/B/Movie 4K.mkv", imdb: "tt1"));

        var (plan, _) = await PlanAsync(server);

        Assert.Equal("hd", plan.Pairs.Single(p => p.LocalId == first.Id).Server.Id);
        Assert.Equal("uhd", plan.Pairs.Single(p => p.LocalId == second.Id).Server.Id);
    }

    [Fact]
    public async Task Episodes_by_the_show_season_and_number()
    {
        var root = _db.AddRoot(LibraryKind.Series, "TV", "I:/");
        var show = new Series { RootId = root.Id, Folder = "Lost", Title = "Lost", TvdbId = "73739" };
        var episode = new Episode { Season = 1, Number = 2, Title = "Tabula", VideoFile = "Lost/Season 1/Lost 1x02.mkv" };
        _db.SaveSeries(show, [], [], [episode]);
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Show("s", "LOST (2004)", "73739"));
        server.Items.Add(FakeMediaServer.Episode("e", "/tv/renamed/s01e02.mkv", "LOST (2004)", 1, 2, played: true, seriesId: "s"));
        server.Items.Add(FakeMediaServer.Episode("f", "/tv/renamed/s01e03.mkv", "LOST (2004)", 1, 3, seriesId: "s"));
        server.Items.Add(new JsonObject { ["Id"] = "v", ["Type"] = "Episode", ["Name"] = "Missing on disk" });

        var (plan, sync) = await PlanAsync(server);

        Assert.Equal("e", plan.Pairs.Single().Server.Id);
        Assert.Equal(1, plan.NotFound);   // the one with no file on the server doesn't count
        Assert.Equal(1, plan.WatchedHere);

        await sync.ApplyAsync(plan, null, _ct);
        Assert.True(_db.GetEpisodes(_db.GetSeries().Single().Id).Single().Watched);
    }

    [Fact]
    public async Task Two_shows_with_the_same_name_keep_their_marks()
    {
        var root = _db.AddRoot(LibraryKind.Series, "TV", "I:/");
        var show = new Series { RootId = root.Id, Folder = "Doctor Who", Title = "Doctor Who", Year = 2005, TvdbId = "78804" };
        _db.SaveSeries(show, [], [], [new Episode { Season = 1, Number = 1, Title = "Rose", VideoFile = "Doctor Who/S01E01.mkv" }]);
        var server = new FakeMediaServer();
        var classic = FakeMediaServer.Show("old", "Doctor Who", "76107");
        classic["ProductionYear"] = 2005;   // the same year too, and still another show
        server.Items.Add(classic);
        server.Items.Add(FakeMediaServer.Episode("e", "/tv/classic/An Unearthly Child.mkv", "Doctor Who", 1, 1, played: true, seriesId: "old"));

        var (plan, _) = await PlanAsync(server);

        Assert.Empty(plan.Pairs);
        Assert.Equal(1, plan.NotFound);
    }

    [Fact]
    public async Task A_show_by_name_needs_the_year_too()
    {
        var root = _db.AddRoot(LibraryKind.Series, "TV", "I:/");
        var show = new Series { RootId = root.Id, Folder = "Lost", Title = "Lost", Year = 2004 };
        _db.SaveSeries(show, [], [], [new Episode { Season = 1, Number = 1, Title = "Pilot", VideoFile = "Lost/S01E01.mkv" }]);
        var same = FakeMediaServer.Show("a", "Lost", null);
        same["ProductionYear"] = 2004;
        var other = FakeMediaServer.Show("b", "Lost", null);
        other["ProductionYear"] = 1999;
        var server = new FakeMediaServer();
        server.Items.Add(same);
        server.Items.Add(other);
        server.Items.Add(FakeMediaServer.Episode("x", "/tv/b/one.mkv", "Lost", 1, 1, seriesId: "b"));
        server.Items.Add(FakeMediaServer.Episode("y", "/tv/a/two.mkv", "Lost", 1, 1, seriesId: "a"));

        var (plan, _) = await PlanAsync(server);

        Assert.Equal("y", plan.Pairs.Single().Server.Id);
    }

    [Fact]
    public async Task Movies_with_the_same_name_and_different_ids_are_two_movies()
    {
        AddMovie("Local/Dune.mkv", "Dune", 2021, imdb: "tt1");
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/other.mkv", "Dune", 2021, imdb: "tt2", played: true));

        Assert.Empty((await PlanAsync(server)).Plan.Pairs);
    }

    [Fact]
    public async Task A_show_listed_twice_by_the_paging_is_no_problem()
    {
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Show("s", "Lost", null));
        server.Items.Add(FakeMediaServer.Show("s", "Lost", null));

        Assert.Empty((await PlanAsync(server)).Plan.Pairs);
    }

    // ---- both ways ----

    [Fact]
    public async Task The_plan_changes_nothing_and_the_sync_does_both_sides()
    {
        AddMovie("Seen here/M.mkv", watched: true);
        var seenThere = AddMovie("Seen there/M.mkv");
        var startedThere = AddMovie("Started there/M.mkv");
        var startedHere = AddMovie("Started here/M.mkv");
        _db.SavePlayback(PlaybackKind.Movie, startedHere.Id, 30 * Minute, 120 * Minute);
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/Seen here/M.mkv"));
        server.Items.Add(FakeMediaServer.Movie("b", "/m/Seen there/M.mkv", played: true));
        server.Items.Add(FakeMediaServer.Movie("c", "/m/Started there/M.mkv", minutes: 40, lastPlayed: "2026-10-01T10:00:00Z"));
        server.Items.Add(FakeMediaServer.Movie("d", "/m/Started here/M.mkv"));

        var (plan, sync) = await PlanAsync(server);

        Assert.True(plan.FirstTime);
        Assert.Equal((1, 1, 1, 1), (plan.WatchedHere, plan.WatchedThere, plan.PositionsHere, plan.PositionsThere));
        Assert.False(_db.GetMovies().Single(m => m.Id == seenThere.Id).Watched);
        Assert.Empty(server.Written);

        var result = await sync.ApplyAsync(plan, null, _ct);

        Assert.Equal(0, result.Failed);
        Assert.True(_db.GetMovies().Single(m => m.Id == seenThere.Id).Watched);
        Assert.Equal(40 * Minute, _db.GetPlayback(PlaybackKind.Movie, startedThere.Id)!.PositionMs);
        Assert.Contains(("POST", "/Users/u1/PlayedItems/a"), server.Written);
        Assert.Contains(("POST", "/Users/u1/Items/d/UserData"), server.Written);
        Assert.Equal(30 * Minute * 10_000, JsonNode.Parse(server.Bodies["/Users/u1/Items/d/UserData"])!["PlaybackPositionTicks"]!.GetValue<long>());
    }

    [Fact]
    public async Task The_second_sync_finds_nothing_to_do_and_an_unmark_travels()
    {
        var movie = AddMovie("A/M.mkv", watched: true);
        var server = new FakeMediaServer();
        var item = FakeMediaServer.Movie("a", "/m/A/M.mkv", played: true);
        server.Items.Add(item);
        await RunAsync(server);

        var (again, _) = await PlanAsync(server);
        Assert.False(again.HasChanges);
        Assert.False(again.FirstTime);

        _db.SetWatched(_db.GetMovies().Single(m => m.Id == movie.Id), false);
        await RunAsync(server);
        Assert.Contains(("DELETE", "/Users/u1/PlayedItems/a"), server.Written);

        // And the other way
        item["UserData"]!["Played"] = false;
        _db.SetWatched(_db.GetMovies().Single(m => m.Id == movie.Id), true);
        await RunAsync(server);
        item["UserData"]!["Played"] = true;
        await RunAsync(server);
        item["UserData"]!["Played"] = false;
        await RunAsync(server);
        Assert.False(_db.GetMovies().Single(m => m.Id == movie.Id).Watched);
    }

    [Fact]
    public async Task What_the_server_refused_is_tried_again_next_time()
    {
        var movie = AddMovie("A/M.mkv", watched: true);
        AddMovie("B/M.mkv", watched: true);
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/A/M.mkv"));
        server.Items.Add(FakeMediaServer.Movie("b", "/m/B/M.mkv"));
        server.Refused.Add("/PlayedItems/a");

        var result = await RunAsync(server);

        Assert.Equal(1, result.Failed);
        Assert.NotNull(result.FirstError);
        Assert.False(_db.GetSyncStates().ContainsKey((PlaybackKind.Movie, movie.Id)));   // no agreement: still to do

        server.Refused.Clear();
        server.Written.Clear();
        var (plan, sync) = await PlanAsync(server);
        Assert.Equal(1, plan.WatchedThere);
        await sync.ApplyAsync(plan, null, _ct);
        Assert.Contains(("POST", "/Users/u1/PlayedItems/a"), server.Written);
    }

    [Fact]
    public async Task What_is_agreed_goes_with_the_title()
    {
        var movie = AddMovie("A/M.mkv", watched: true);
        var root = _db.AddRoot(LibraryKind.Series, "TV", "I:/");
        var show = new Series { RootId = root.Id, Folder = "Lost", Title = "Lost" };
        _db.SaveSeries(show, [], [], [new Episode { Season = 1, Number = 1, Title = "Pilot", VideoFile = "Lost/S01E01.mkv" }]);
        var episode = _db.GetEpisodes(show.Id).Single();
        _db.SaveSyncStates([(PlaybackKind.Movie, movie.Id, true, 0), (PlaybackKind.Episode, episode.Id, false, 0)]);
        _db.SavePlayback(PlaybackKind.Movie, movie.Id, 5 * Minute, 90 * Minute);
        _db.SavePlayback(PlaybackKind.Episode, episode.Id, 5 * Minute, 40 * Minute);

        _db.DeleteMovies([movie.Id]);
        _db.DeleteSeries([show.Id]);   // the episodes go with it

        Assert.Empty(_db.GetSyncStates());
        Assert.Empty(_db.GetPlaybackPositions(PlaybackKind.Movie));
        Assert.Empty(_db.GetPlaybackPositions(PlaybackKind.Episode));

        // Nor does a folder taken out leave anything for the next title that gets its id
        var other = AddMovie("B/M.mkv", watched: true);
        _db.SaveSyncStates([(PlaybackKind.Movie, other.Id, true, 0)]);
        _db.RemoveRoot(_root.Id);
        Assert.Empty(_db.GetSyncStates());
    }

    [Fact]
    public async Task A_title_added_later_is_agreed_on_even_if_nothing_changes()
    {
        var movie = AddMovie("A/M.mkv", watched: true);
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/A/M.mkv", played: true));

        var (plan, sync) = await PlanAsync(server);
        Assert.False(plan.HasChanges);
        await sync.ApplyAsync(plan, null, _ct);

        Assert.True(_db.GetSyncStates().ContainsKey((PlaybackKind.Movie, movie.Id)));
    }

    [Fact]
    public async Task Jellyfin_positions_work_on_the_old_routes_too()
    {
        var server = new FakeMediaServer("Jellyfin Server");
        server.Gone.Add("/UserItems");
        using var client = Client(server);
        await client.ConnectAsync(_ct);

        await client.SetPositionAsync("u1", "m1", 5 * Minute, _ct);

        var body = JsonNode.Parse(server.Bodies["/Users/u1/Items/m1/UserData"])!;
        Assert.Equal(5 * Minute * 10_000, body["PlaybackPositionTicks"]!.GetValue<long>());
        Assert.True(body["IsFavorite"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_new_user_or_server_starts_from_nothing()
    {
        AddMovie("A/M.mkv", watched: true);
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/A/M.mkv", played: true));
        await RunAsync(server);
        Assert.NotEmpty(_db.GetSyncStates());

        _db.ClearSyncStates();

        Assert.True((await PlanAsync(server)).Plan.FirstTime);
    }
}
