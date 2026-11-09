using System.Globalization;
using System.Text;

namespace Kinora.Core;

/// <summary>
/// One rule for every search: case, accents and punctuation don't matter, so "amelie" finds
/// "Amélie" and "spiderman" is not needed to find "Spider-Man" (both become "spider man").
/// </summary>
public static class SearchText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (c is '\'' or '’')
            {
                // "Assassin's" should match "assassins"
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// How well a title answers a search, best first, so that a short title isn't lost among
    /// everything with those letters in it: 0 the title itself ("her" → "Her", "thing" → "The Thing"),
    /// 1 a title that begins with it ("Hereditary"), 2 a title with a word that does ("Kill Her Goats"),
    /// 3 any other match (inside a word, as in "Other", or in the cast, the director, the year).
    /// </summary>
    public static int Rank(IEnumerable<string> normalizedTitles, string normalizedQuery)
    {
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return 0;
        var best = 3;
        foreach (var title in normalizedTitles)
        {
            var bare = WithoutArticle(title);
            if (title == normalizedQuery || bare == normalizedQuery)
                return 0;
            if (title.StartsWith(normalizedQuery, StringComparison.Ordinal) || bare.StartsWith(normalizedQuery, StringComparison.Ordinal))
                best = 1;
            else if (best > 2 && words.All(w => BeginsAWord(title, w)))
                best = 2;
        }
        return best;
    }

    private static readonly string[] Articles = ["the ", "a ", "an ", "il ", "lo ", "la ", "i ", "gli ", "le ", "un ", "uno ", "una "];

    private static string WithoutArticle(string title)
    {
        foreach (var article in Articles)
            if (title.StartsWith(article, StringComparison.Ordinal) && title.Length > article.Length)
                return title[article.Length..];
        return title;
    }

    private static bool BeginsAWord(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
            if (i == 0 || text[i - 1] == ' ')
                return true;
        return false;
    }

    /// <summary>True when every word of the query is found somewhere in the text.</summary>
    public static bool Matches(string normalizedText, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0)
            return true;
        foreach (var word in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!normalizedText.Contains(word, StringComparison.Ordinal))
                return false;
        return true;
    }
}
