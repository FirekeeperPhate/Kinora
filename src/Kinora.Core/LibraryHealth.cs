using Kinora.Core.Models;

namespace Kinora.Core;

/// <summary>
/// What is worth fixing in a title's folder: no poster, no plot, files the scanner couldn't place.
/// A title not identified has its own view and isn't repeated here.
/// </summary>
public static class LibraryHealth
{
    public static List<string> Problems(Movie movie)
    {
        var problems = new List<string>();
        if (movie.NeedsMatch)
            return problems;
        if (movie.ThumbKey is null)
            problems.Add("no poster");
        if (string.IsNullOrWhiteSpace(movie.Plot))
            problems.Add("no plot");
        if (movie.Folder.Length == 0)
            problems.Add("not in a folder of its own");
        return problems;
    }

    public static List<string> Problems(Series show, IReadOnlyCollection<(int? Season, int? Number, int? NumberEnd)> episodes)
    {
        var problems = new List<string>();
        if (show.NeedsMatch)
            return problems;
        if (episodes.Count == 0)
            problems.Add("no episodes in the folder");
        if (show.ThumbKey is null)
            problems.Add("no poster");
        if (string.IsNullOrWhiteSpace(show.Plot))
            problems.Add("no plot");
        var unplaced = episodes.Count(e => e.Season is null || e.Number is null);
        if (unplaced > 0)
            problems.Add(unplaced == 1 ? "1 file without season and episode number" : $"{unplaced} files without season and episode number");
        var twice = episodes.Where(e => e.Season is not null && e.Number is not null)
                            .SelectMany(e => Enumerable.Range(e.Number!.Value, Math.Max(1, (e.NumberEnd ?? e.Number!.Value) - e.Number!.Value + 1))
                                                       .Select(n => (Season: e.Season!.Value, Number: n)))
                            .GroupBy(k => k)
                            .Where(g => g.Count() > 1)
                            .Select(g => $"S{g.Key.Season:00}E{g.Key.Number:00}")
                            .ToList();
        if (twice.Count > 0)
            problems.Add(twice.Count <= 2 ? $"{string.Join(" and ", twice)} twice" : $"{twice.Count} episodes twice");
        return problems;
    }

    /// <summary>"No poster, no plot".</summary>
    public static string Describe(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? "" : char.ToUpperInvariant(problems[0][0]) + string.Join(", ", problems)[1..];
}
