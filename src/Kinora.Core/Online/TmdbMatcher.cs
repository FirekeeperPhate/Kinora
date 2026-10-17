namespace Kinora.Core.Online;

/// <summary>
/// Decides whether a search answer is the movie we have. Sure only when the title is the same
/// (accents, punctuation and a leading article aside) and the year matches within one (release
/// dates differ by country). Anything less is left to the user.
/// </summary>
public static class TmdbMatcher
{
    public static (TmdbSearchResult? Best, bool Confident) Choose(string title, int? year, IReadOnlyList<TmdbSearchResult> results)
    {
        if (results.Count == 0)
            return (null, false);

        var wanted = Key(title);
        var sameTitle = results.Where(r => Key(r.Title) == wanted || (r.OriginalTitle is { } o && Key(o) == wanted)).ToList();

        if (year is { } y)
        {
            var exact = sameTitle.Where(r => r.Year == y).OrderByDescending(r => r.Popularity).FirstOrDefault();
            if (exact is not null)
                return (exact, true);
            var near = sameTitle.Where(r => r.Year is { } ry && Math.Abs(ry - y) == 1).OrderByDescending(r => r.Popularity).FirstOrDefault();
            if (near is not null)
                return (near, true);
            return (results[0], false);
        }

        if (sameTitle.Count == 1)
            return (sameTitle[0], true);
        if (sameTitle.Count > 1)
        {
            var ordered = sameTitle.OrderByDescending(r => r.Popularity).ToList();
            // Without a year, a remake and its original share the title: sure only if one is
            // far better known than the others
            return (ordered[0], ordered[0].Popularity >= 5 * Math.Max(ordered[1].Popularity, 0.1));
        }
        return (results[0], false);
    }

    private static string Key(string title)
    {
        var t = SearchText.Normalize(title).Replace(" and ", " ");
        foreach (var article in new[] { "the ", "a ", "an " })
            if (t.StartsWith(article, StringComparison.Ordinal))
                return t[article.Length..];
        return t;
    }
}
