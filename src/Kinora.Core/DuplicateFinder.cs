namespace Kinora.Core;

/// <summary>What tells two titles apart.</summary>
public sealed record TitleIds(string? ImdbId, string? TmdbId, string? TvdbId, string Title, int? Year)
{
    public IEnumerable<string> Keys
    {
        get
        {
            if (!string.IsNullOrEmpty(ImdbId)) yield return "imdb:" + ImdbId;
            if (!string.IsNullOrEmpty(TmdbId)) yield return "tmdb:" + TmdbId;
            if (!string.IsNullOrEmpty(TvdbId)) yield return "tvdb:" + TvdbId;
        }
    }
}

/// <summary>
/// The same movie or show twice in the library, in the same folder or in two. An id in common
/// is enough; without ids, the same name and year.
/// </summary>
public static class DuplicateFinder
{
    public static List<List<T>> Groups<T>(IReadOnlyList<T> items, Func<T, TitleIds> ids)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        var all = items.Select(ids).ToList();
        var byKey = new Dictionary<string, int>();
        var byName = new Dictionary<string, List<int>>();
        for (var i = 0; i < all.Count; i++)
        {
            foreach (var key in all[i].Keys)
            {
                if (byKey.TryGetValue(key, out var other))
                    parent[Find(i)] = Find(other);
                else
                    byKey[key] = i;
            }
            if (all[i].Year is { } year && SearchText.Normalize(all[i].Title) is { Length: > 0 } name)
            {
                var key = $"{name}|{year}";
                if (!byName.TryGetValue(key, out var list))
                    byName[key] = list = [];
                list.Add(i);
            }
        }

        // Same name and year: two different ids are two different titles (remakes, namesakes);
        // one without ids goes with the first that has them
        foreach (var list in byName.Values.Where(l => l.Count > 1))
        {
            var bare = list.Where(i => !all[i].Keys.Any()).ToList();
            if (bare.Count == 0)
                continue;
            var with = list.FirstOrDefault(i => all[i].Keys.Any(), bare[0]);
            foreach (var i in bare)
                parent[Find(i)] = Find(with);
        }

        return Enumerable.Range(0, items.Count)
                         .GroupBy(Find)
                         .Where(g => g.Count() > 1)
                         .Select(g => g.Select(i => items[i]).ToList())
                         .ToList();
    }
}
