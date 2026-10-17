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
