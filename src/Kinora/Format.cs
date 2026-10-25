using System.Globalization;
using Avalonia.Data.Converters;

namespace Kinora;

public static class Format
{
    /// <summary>For things that fade in and out: true → 1, false → 0.</summary>
    public static IValueConverter VisibleOpacity { get; } = new FuncValueConverter<bool, double>(v => v ? 1 : 0);

    public static string Runtime(int minutes) =>
        minutes < 60 ? $"{minutes}m" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit >= 3 ? "0.00" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    /// <summary>"en" -> "English", "en forced" -> "English (forced)", "und" -> "Unknown".</summary>
    public static string Language(string tag)
    {
        var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return tag;
        var name = parts[0] switch
        {
            "und" => "Unknown",
            _ => LanguageName(parts[0]),
        };
        return parts.Length == 1 ? name : $"{name} ({string.Join(", ", parts.Skip(1))})";
    }

    private static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? code.ToUpperInvariant() : culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code.ToUpperInvariant();
        }
    }
}
