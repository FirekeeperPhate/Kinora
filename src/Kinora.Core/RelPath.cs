namespace Kinora.Core;

/// <summary>
/// Paths inside a root are stored with "/" whatever the OS, so the same database works when the
/// share is opened from Windows, Linux or macOS.
/// </summary>
public static class RelPath
{
    public static string Combine(string rel, string name) => rel.Length == 0 ? name : rel + "/" + name;

    public static string LastSegment(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? rel : rel[(i + 1)..];
    }

    public static string ToFull(string rootPath, string rel) =>
        rel.Length == 0 ? rootPath : Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar));
}
