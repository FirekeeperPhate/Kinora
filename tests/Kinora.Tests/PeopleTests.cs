using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public sealed class PeopleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-people-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly MovieScanner _scanner;
    private readonly LibraryRoot _root;

    public PeopleTests()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        _scanner = new MovieScanner(_db, new ImageCache(Path.Combine(_dir, "posters"), 320));
        _root = _db.AddRoot(LibraryKind.Movies, "Movies", media);

        void Movie(string folder, string nfo)
        {
            Directory.CreateDirectory(Path.Combine(media, folder));
            File.WriteAllBytes(Path.Combine(media, folder, "movie.mkv"), new byte[10]);
            File.WriteAllText(Path.Combine(media, folder, "movie.nfo"), nfo);
        }
        Movie("Heat (1995)", """
            <movie><title>Heat</title><director>Michael Mann</director>
            <actor><name>Al Pacino</name><role>Vincent Hanna</role><tmdbid>1158</tmdbid></actor>
            <actor><name>Robert De Niro</name><role>Neil McCauley</role></actor></movie>
            """);
        Movie("Collateral (2004)", "<movie><title>Collateral</title><director>Michael Mann</director></movie>");
        Movie("The Irishman (2019)", """
            <movie><title>The Irishman</title><director>Martin Scorsese</director>
            <actor><name>Al Pacino</name><role>Jimmy Hoffa</role><tmdbid>1158</tmdbid></actor></movie>
            """);
        Movie("Michael Mannheim Story (2001)", "<movie><title>Not him</title><director>Michael Mannheim</director></movie>");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string TitleOf(PersonCredit c) => _db.GetMovie(c.Id)!.Title;

    [Fact]
    public async Task An_actor_by_tmdb_id_with_roles()
    {
        await _scanner.ScanAsync(_root, false, null, default);
        Assert.Equal("1158", _db.FindPersonId("al pacino"));

        var credits = _db.GetCredits("Al Pacino", "1158");
        Assert.Equal(["Heat: Vincent Hanna", "The Irishman: Jimmy Hoffa"],
                     credits.Select(c => $"{TitleOf(c)}: {c.Role}").Order());
    }

    [Fact]
    public async Task A_director_by_whole_name_only()
    {
        await _scanner.ScanAsync(_root, false, null, default);
        var credits = _db.GetCredits("Michael Mann", null);
        Assert.Equal(["Collateral", "Heat"], credits.Select(TitleOf).Order());
        Assert.All(credits, c => Assert.Equal("Director", c.Role));
    }

    [Fact]
    public async Task An_actor_without_id_is_found_by_name()
    {
        await _scanner.ScanAsync(_root, false, null, default);
        var credit = Assert.Single(_db.GetCredits("robert de niro", null));
        Assert.Equal("Neil McCauley", credit.Role);
    }
}
