using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.Models;
using Kinora.Core.Online;

namespace Kinora.Tests;

/// <summary>Related movies in the library, and TMDB's recommendations.</summary>
public sealed class RelatedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-related-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public RelatedTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Movie M(long id, string title, int year, string? collection = null, string directors = "", string? imdb = null) =>
        new() { Id = id, Title = title, Year = year, Collection = collection, Directors = directors, ImdbId = imdb ?? $"tt{id:0000000}" };

    [Fact]
    public void Collection_first_in_order_then_the_director_then_the_actors()
    {
        var alien = M(1, "Alien", 1979, "Alien Collection", "Ridley Scott");
        var library = new[]
        {
            alien,
            M(2, "Aliens", 1986, "Alien Collection", "James Cameron"),
            M(3, "Alien³", 1992, "alien collection", "David Fincher"),
            M(4, "Blade Runner", 1982, directors: "Ridley Scott"),
            M(5, "Gladiator", 2000, directors: "Ridley Scott|Someone"),
            M(6, "The Village", 2004, directors: "M. Night Shyamalan"),   // shares two leads
            M(7, "Heat", 1995, directors: "Michael Mann"),                // shares one lead only
            M(8, "Alien", 1979, "Alien Collection", "Ridley Scott", imdb: "tt0000001"),   // another copy of this one
            M(9, "Aliens", 1986, "Alien Collection", "James Cameron", imdb: "tt0000002"),   // another copy of a related one
        };
        var leads = new Dictionary<long, List<string>>
        {
            [1] = ["Sigourney Weaver", "Tom Skerritt", "Veronica Cartwright", "Harry Dean Stanton", "John Hurt"],
            [6] = ["Joaquin Phoenix", "Sigourney Weaver", "John Hurt"],
            [7] = ["Al Pacino", "Tom Skerritt"],
        };

        var related = RelatedTitles.Find(alien, library, leads);

        Assert.Equal([2L, 3L, 5L, 4L, 6L], related.Select(r => r.Movie.Id));
        Assert.Equal("Alien Collection", related[0].Reason);
        Assert.Equal("Directed by Ridley Scott", related[2].Reason);
        Assert.Equal("With Sigourney Weaver and John Hurt", related[4].Reason);
    }

    [Fact]
    public void A_movie_alone_has_nothing_related()
    {
        var movie = M(1, "Alone", 2000);
        Assert.Empty(RelatedTitles.Find(movie, [movie, M(2, "Other", 2001, directors: "X")], new Dictionary<long, List<string>>()));
    }

    [Fact]
    public void The_leading_cast_is_the_first_names_in_order()
    {
        var root = _db.AddRoot(LibraryKind.Movies, "Movies", _dir);
        var movie = new Movie { RootId = root.Id, VideoFile = "A/A.mkv", Folder = "A", Title = "A" };
        _db.SaveMovies([(movie, Enumerable.Range(0, 8).Select(i => new CastMember { Ord = i, Name = $"Actor {i}" }).ToList())]);

        Assert.Equal(["Actor 0", "Actor 1", "Actor 2"], _db.GetLeadingCast(3)[movie.Id]);
    }

    [Fact]
    public async Task Recommendations_are_asked_once_and_kept()
    {
        var fake = new FakeTmdb();
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", fake);
        var similar = new SimilarTitles(_db);

        var first = await similar.GetAsync(client, series: false, "949", _ct);
        var again = await similar.GetAsync(client, series: false, "949", _ct);

        Assert.Equal(["Alien", "Collateral"], first.Select(t => t.Title));   // one without a title is skipped
        Assert.Equal(1979, first[0].Year);
        Assert.Equal("/coll.jpg", first[1].PosterPath);
        Assert.Equal(8.1, first[0].Rating);
        Assert.Equal(first, again);
        Assert.Single(fake.Requests, r => r.Contains("recommendations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_recommendations_the_similar_ones_and_offline_what_was_kept()
    {
        var fake = new FakeTmdb();
        using (var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", fake))
            Assert.Equal(["Aliens"], (await new SimilarTitles(_db).GetAsync(client, false, "348", _ct)).Select(t => t.Title));

        // No key now, or no network: what was kept, never an error
        Assert.Equal(["Aliens"], (await new SimilarTitles(_db).GetAsync(null, false, "348", _ct)).Select(t => t.Title));
        Assert.Empty(await new SimilarTitles(_db).GetAsync(null, false, "949", _ct));
        Assert.Empty(await new SimilarTitles(_db).GetAsync(null, false, null, _ct));
    }

    [Fact]
    public void Shows_are_related_by_their_actors_then_by_network_and_genre()
    {
        static Series S(long id, string title, int year, string studios = "", string genres = "") =>
            new() { Id = id, Title = title, Year = year, Studios = studios, Genres = genres, TvdbId = id.ToString() };
        var wire = S(1, "The Wire", 2002, "HBO", "Crime|Drama");
        var library = new[]
        {
            wire,
            S(2, "Treme", 2010, "HBO", "Drama"),
            S(3, "The Deuce", 2017, "HBO", "Drama"),        // same actors as well
            S(4, "Silicon Valley", 2014, "HBO", "Comedy"),  // same network, no genre in common
            S(5, "Bosch", 2014, "Amazon", "Crime"),
        };
        var leads = new Dictionary<long, List<string>>
        {
            [1] = ["Dominic West", "Idris Elba", "Wendell Pierce"],
            [3] = ["James Franco", "Wendell Pierce", "Idris Elba"],
        };

        var related = RelatedTitles.FindShows(wire, library, leads);

        Assert.Equal([3L, 2L], related.Select(r => r.Show.Id));
        Assert.Equal("With Wendell Pierce and Idris Elba", related[0].Reason);
        Assert.Equal("Also on HBO", related[1].Reason);
    }

    [Theory]
    [InlineData("en-US", "949", "https://www.youtube.com/watch?v=enTrailer")]   // a trailer before a teaser, English before Italian
    [InlineData("it-IT", "949", "https://www.youtube.com/watch?v=itTrailer")]   // the language of the metadata first
    [InlineData("en-US", "348", null)]                                          // no trailer, only extras
    [InlineData("en-US", "999999", null)]                                       // unknown to TMDB
    public async Task The_trailer_is_a_trailer_in_the_language_first(string language, string id, string? url)
    {
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", language, new FakeTmdb());
        Assert.Equal(url, await client.GetTrailerAsync(series: false, id, _ct));
    }
}
