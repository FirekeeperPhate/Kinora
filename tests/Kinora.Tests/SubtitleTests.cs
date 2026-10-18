using System.Net;
using System.Text;
using Kinora.Core.Scanning;
using Kinora.Core.Subtitles;

namespace Kinora.Tests;

public class MovieHashTests
{
    [Fact]
    public void Zeros_hash_to_the_size()
    {
        using var stream = new MemoryStream(new byte[131072]);
        Assert.Equal("0000000000020000", MovieHash.Compute(stream));
    }

    [Fact]
    public void First_and_last_chunks_are_summed_with_the_size()
    {
        var data = new byte[200_000];
        data[0] = 1;                       // first chunk: +1
        data[200_000 - 8] = 2;             // last chunk: +2
        using var stream = new MemoryStream(data);
        Assert.Equal((200_000UL + 3).ToString("x16"), MovieHash.Compute(stream));
    }

    [Fact]
    public void A_small_file_is_read_twice_whole()
    {
        var data = new byte[16];
        data[8] = 5;
        using var stream = new MemoryStream(data);
        Assert.Equal((16UL + 10).ToString("x16"), MovieHash.Compute(stream));
    }
}

public sealed class SubtitleFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-subs-" + Guid.NewGuid().ToString("N"));

    public SubtitleFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SubtitleResult R(string lang, int downloads, bool hash = false, bool mt = false, bool hi = false) =>
        new("1", "10", "x.srt", lang, "rel", downloads, hash, hi, mt, false, null, null);

    [Fact]
    public void Hash_then_language_order_then_humans_then_downloads()
    {
        var ranked = SubtitleFiles.Rank(
            [R("en", 900), R("it", 10), R("it", 50, mt: true), R("en", 5, hash: true), R("fr", 9999)],
            ["it", "en"]);
        Assert.Equal(["en:5", "it:10", "it:50", "en:900", "fr:9999"], ranked.Select(r => $"{r.Language}:{r.Downloads}"));
    }

    [Fact]
    public void Saved_next_to_the_video_with_the_names_the_scanner_reads()
    {
        var video = Path.Combine(_dir, "Heat.mkv");
        var path = SubtitleFiles.TargetPath(video, R("it", 1, hi: true), "whatever.SRT");
        Assert.Equal(Path.Combine(_dir, "Heat.it.sdh.srt"), path);
        Assert.Equal("it sdh", MediaFiles.SubtitleTag(Path.GetFileName(path), "Heat"));
    }

    [Fact]
    public void An_existing_subtitle_is_kept_as_old()
    {
        var target = Path.Combine(_dir, "Heat.en.srt");
        File.WriteAllText(target, "mine");
        SubtitleFiles.Save(target, Encoding.UTF8.GetBytes("new"));
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir, "Heat.en.old.srt")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Languages_typed_by_hand() =>
        Assert.Equal(["it", "en", "pt-br"], SubtitleFiles.ParseLanguages(" IT, en;pt-BR  en"));
}

public class OpenSubtitlesClientTests
{
    private sealed class FakeOpenSubtitles : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public bool QuotaUsedUp { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var uri = request.RequestUri!;
            string body;
            var status = HttpStatusCode.OK;
            if (uri.AbsolutePath.EndsWith("/login"))
                body = """{"token":"TOKEN","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":20,"remaining_downloads":17,"vip":false}}""";
            else if (uri.AbsolutePath.EndsWith("/subtitles"))
                body = """
                    {"data":[
                      {"id":"1","attributes":{"language":"en","download_count":500,"moviehash_match":true,"hearing_impaired":false,
                        "machine_translated":false,"release":"Heat.1995.1080p","files":[{"file_id":111,"file_name":"Heat.1995.srt"}]}},
                      {"id":"2","attributes":{"language":"it","download_count":20,"ai_translated":true,"files":[{"file_id":222,"file_name":"Heat.it.srt"}]}},
                      {"id":"3","attributes":{"language":"en","download_count":3,"files":[]}}
                    ]}
                    """;
            else if (uri.AbsolutePath.EndsWith("/download"))
            {
                if (QuotaUsedUp)
                {
                    status = HttpStatusCode.NotAcceptable;
                    body = """{"message":"You have downloaded your allowed 20 subtitles for 24h."}""";
                }
                else
                    body = """{"link":"https://files.example/abc.srt","file_name":"Heat.1995.srt","remaining":16}""";
            }
            else if (uri.Host == "files.example")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHello\n") };
            else
            {
                status = HttpStatusCode.NotFound;
                body = "{}";
            }
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Search_sends_sorted_lowercase_parameters_and_reads_the_results()
    {
        var fake = new FakeOpenSubtitles();
        using var client = new OpenSubtitlesClient("KEY", fake);
        var results = await client.SearchAsync(new SubtitleQuery(MovieHash: "8e245d9679d31e12", ImdbId: "tt0113277", Title: "Heat"), ["IT", "en"], default);

        var request = fake.Requests.Single();
        Assert.Equal("?imdb_id=113277&languages=en%2Cit&moviehash=8e245d9679d31e12", request.RequestUri!.Query);
        Assert.Equal("KEY", request.Headers.GetValues("Api-Key").Single());
        Assert.Contains("Kinora", request.Headers.UserAgent.ToString());

        Assert.Equal(2, results.Count);   // the one without files is skipped
        Assert.True(results[0].HashMatch);
        Assert.Equal("111", results[0].FileId);
        Assert.True(results[1].MachineTranslated);   // ai_translated counts as machine
    }

    [Fact]
    public async Task Episodes_are_searched_by_the_show_and_numbers()
    {
        var fake = new FakeOpenSubtitles();
        using var client = new OpenSubtitlesClient("KEY", fake);
        await client.SearchAsync(new SubtitleQuery(ParentImdbId: "tt0903747", ParentTmdbId: "1396", Season: 1, Episode: 2, Title: "Breaking Bad"), [], default);
        Assert.Equal("?episode_number=2&parent_imdb_id=903747&parent_tmdb_id=1396&season_number=1", fake.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task Login_then_download_goes_to_the_host_the_login_named()
    {
        var fake = new FakeOpenSubtitles();
        using var client = new OpenSubtitlesClient("KEY", fake);
        var account = await client.LoginAsync("me", "secret", default);
        Assert.Equal(17, account.RemainingDownloads);

        var file = await client.DownloadAsync("111", default);
        Assert.Contains("Hello", Encoding.UTF8.GetString(file.Content));
        Assert.Equal(16, file.Remaining);

        var download = fake.Requests[1];
        Assert.Equal("vip-api.opensubtitles.com", download.RequestUri!.Host);
        Assert.Equal("Bearer TOKEN", download.Headers.Authorization!.ToString());
        Assert.Equal("""{"file_id":111}""", fake.Bodies[1]);
    }

    [Fact]
    public async Task A_used_up_quota_says_so()
    {
        using var client = new OpenSubtitlesClient("KEY", new FakeOpenSubtitles { QuotaUsedUp = true });
        var e = await Assert.ThrowsAsync<OpenSubtitlesException>(() => client.DownloadAsync("111", default));
        Assert.Contains("allowed 20", e.Message);
    }

    [Theory]
    [InlineData("tt0113277", "113277")]
    [InlineData("tt1234567", "1234567")]
    [InlineData("113277", "113277")]
    [InlineData("nope", null)]
    public void Imdb_ids(string id, string? expected) => Assert.Equal(expected, OpenSubtitlesClient.Imdb(id));
}
