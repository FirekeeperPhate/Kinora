using Kinora.Core;
using Kinora.Core.Models;

namespace Kinora.Tests;

public sealed class MovieCollectionsTests
{
    [Fact]
    public void Movies_go_by_collection_in_the_order_they_came_out()
    {
        Movie[] movies =
        [
            new() { Title = "Aliens", Year = 1986, Collection = "Alien Collection" },
            new() { Title = "Alien", Year = 1979, Collection = "alien collection " },   // another case, a space: the same
            new() { Title = "The Matrix", Year = 1999, Collection = "The Matrix Collection" },
            new() { Title = "Heat", Year = 1995 },
            new() { Title = "Up", Year = 2009, Collection = "  " },
            new() { Title = "Alien 3", Collection = "Alien Collection" },   // no year: last
        ];

        var groups = MovieCollections.Group(movies, m => m);

        Assert.Equal(["Alien Collection", "The Matrix Collection"], groups.Select(g => g.Name.Equals("alien collection", StringComparison.OrdinalIgnoreCase) ? "Alien Collection" : g.Name));
        Assert.Equal(["Alien", "Aliens", "Alien 3"], groups[0].Movies.Select(m => m.Title));
        Assert.Single(groups[1].Movies);
    }

    [Fact]
    public void A_leading_article_doesnt_count_in_the_order()
    {
        Movie[] movies =
        [
            new() { Title = "The Matrix", Collection = "The Matrix Collection" },
            new() { Title = "Batman Begins", Collection = "The Dark Knight Collection" },
            new() { Title = "Mad Max", Collection = "Mad Max Collection" },
        ];
        Assert.Equal(["The Dark Knight Collection", "Mad Max Collection", "The Matrix Collection"],
                     MovieCollections.Group(movies, m => m).Select(g => g.Name));
        Assert.True(MovieCollections.Same(" Alien Collection", "alien collection"));
        Assert.False(MovieCollections.Same(null, "Alien Collection"));
    }
}
