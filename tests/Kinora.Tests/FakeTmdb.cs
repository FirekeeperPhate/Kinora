using System.Net;
using System.Text;
using SkiaSharp;

namespace Kinora.Tests;

/// <summary>A pretend api.themoviedb.org: answers from a few canned movies and counts the calls.</summary>
internal sealed class FakeTmdb : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public bool RefuseKey { get; set; }

    public sealed record FakeMovie(string Id, string Title, int Year, string? ImdbId = null, double Popularity = 10,
                                   string? OriginalTitle = null);

    public List<FakeMovie> Movies { get; } =
    [
        new("949", "Heat", 1995, "tt0113277", 40),
        new("1103", "Heat", 1986, null, 3),
        new("348", "Alien", 1979, "tt0078748", 50),
        new("679", "Aliens", 1986, "tt0090605", 45),
    ];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.PathAndQuery);
        if (RefuseKey)
            return Json(HttpStatusCode.Unauthorized, """{"status_code":7,"status_message":"Invalid API key"}""");

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var path = uri.AbsolutePath.Replace("/3/", "");

        if (path == "configuration")
            return Json(HttpStatusCode.OK, """{"images":{}}""");

        if (path == "search/movie")
        {
            var q = query["query"] ?? "";
            int? year = int.TryParse(query["year"], out var y) ? y : null;
            var hits = Movies.Where(m => m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || q.Contains(m.Title, StringComparison.OrdinalIgnoreCase))
                             .Where(m => year is null || m.Year == year)
                             .Select(m => $$"""{"id":{{m.Id}},"title":"{{m.Title}}","original_title":"{{m.OriginalTitle ?? m.Title}}","release_date":"{{m.Year}}-06-01","popularity":{{m.Popularity}},"poster_path":"/p{{m.Id}}.jpg","overview":"About {{m.Title}}"}""");
            return Json(HttpStatusCode.OK, $$"""{"results":[{{string.Join(",", hits)}}]}""");
        }

        if (path.StartsWith("find/"))
        {
            var imdb = path["find/".Length..];
            var hits = Movies.Where(m => m.ImdbId == imdb).Select(m => $$"""{"id":{{m.Id}},"title":"{{m.Title}}"}""");
            var tv = imdb == "81189" || imdb == "tt0903747" ? """{"id":1396,"name":"Breaking Bad"}""" : "";
            return Json(HttpStatusCode.OK, $$"""{"movie_results":[{{string.Join(",", hits)}}],"tv_results":[{{tv}}]}""");
        }

        if (path == "search/tv")
        {
            var q = query["query"] ?? "";
            var hits = q.Contains("Breaking", StringComparison.OrdinalIgnoreCase)
                ? """{"id":1396,"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","popularity":90,"poster_path":"/bb.jpg"}"""
                : "";
            return Json(HttpStatusCode.OK, $$"""{"results":[{{hits}}]}""");
        }

        if (path == "tv/1396")
            return Json(HttpStatusCode.OK, """
                {
                  "id": 1396, "name": "Breaking Bad", "original_name": "Breaking Bad", "overview": "A teacher turns to crime.",
                  "first_air_date": "2008-01-20", "last_air_date": "2013-09-29", "status": "Ended", "vote_average": 8.9,
                  "genres": [{"name":"Drama"},{"name":"Crime"}], "networks": [{"name":"AMC"}],
                  "poster_path": "/bb.jpg", "backdrop_path": "/bbb.jpg",
                  "seasons": [{"season_number":0,"poster_path":"/s0.jpg"},{"season_number":1,"poster_path":"/s1.jpg"},{"season_number":2,"poster_path":"/s2.jpg"}],
                  "external_ids": {"imdb_id":"tt0903747","tvdb_id":81189},
                  "aggregate_credits": {"cast":[{"name":"Bryan Cranston","roles":[{"character":"Walter White"}]}]},
                  "content_ratings": {"results":[{"iso_3166_1":"US","rating":"TV-MA"}]},
                  "images": {"logos":[{"file_path":"/bblogo.png","iso_639_1":"en"}]}
                }
                """);

        if (path.StartsWith("tv/1396/season/"))
        {
            var n = int.Parse(path["tv/1396/season/".Length..]);
            var eps = Enumerable.Range(1, 3).Select(e =>
                $$"""{"episode_number":{{e}},"name":"Episode {{n}}.{{e}}","overview":"Plot {{n}}.{{e}}","air_date":"2008-0{{e}}-01","runtime":47,"still_path":"/still{{n}}{{e}}.jpg"}""");
            return Json(HttpStatusCode.OK, $$"""{"season_number":{{n}},"poster_path":"/s{{n}}.jpg","episodes":[{{string.Join(",", eps)}}]}""");
        }

        if (path.StartsWith("movie/"))
        {
            var id = path["movie/".Length..];
            var m = Movies.FirstOrDefault(x => x.Id == id);
            if (m is null)
                return Json(HttpStatusCode.NotFound, """{"status_code":34}""");
            return Json(HttpStatusCode.OK, $$"""
                {
                  "id": {{m.Id}}, "imdb_id": {{(m.ImdbId is null ? "null" : $"\"{m.ImdbId}\"")}},
                  "title": "{{m.Title}}", "original_title": "{{m.Title}}",
                  "overview": "The plot of {{m.Title}}.", "tagline": "A tagline", "release_date": "{{m.Year}}-06-01",
                  "runtime": 120, "vote_average": 7.86,
                  "genres": [{"id":1,"name":"Crime"},{"id":2,"name":"Drama"}],
                  "production_companies": [{"name":"Warner Bros."}],
                  "production_countries": [{"iso_3166_1":"US","name":"United States of America"}],
                  "belongs_to_collection": null,
                  "poster_path": "/p{{m.Id}}.jpg", "backdrop_path": "/b{{m.Id}}.jpg",
                  "credits": {
                    "cast": [{"name":"Al Pacino","character":"Hanna","order":0},{"name":"Robert De Niro","character":"McCauley","order":1}],
                    "crew": [{"name":"Michael Mann","job":"Director"},{"name":"Someone","job":"Producer"}]
                  },
                  "release_dates": {"results":[{"iso_3166_1":"US","release_dates":[{"certification":""},{"certification":"R"}]}]},
                  "images": {"logos":[{"file_path":"/l{{m.Id}}.svg","iso_639_1":"en","vote_average":9},{"file_path":"/l{{m.Id}}.png","iso_639_1":"en","vote_average":5}]}
                }
                """);
        }
        return Json(HttpStatusCode.NotFound, "{}");
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    /// <summary>Every "downloaded" picture is a small JPEG (PNG for logos).</summary>
    public static Task<byte[]?> Image(string url, CancellationToken ct)
    {
        using var bmp = new SKBitmap(500, 750);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(SKColors.DarkRed);
        var format = url.EndsWith(".png") ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var data = SKImage.FromBitmap(bmp).Encode(format, 80);
        return Task.FromResult<byte[]?>(data.ToArray());
    }
}
