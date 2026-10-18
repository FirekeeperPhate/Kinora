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

    /// <summary>The path to show or launch.</summary>
    public static string ToFull(string rootPath, string rel) =>
        rel.Length == 0 ? rootPath : Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The path for the file functions: a name like "S.W.A.T." is kept as it is (see <see cref="ExactPath"/>).</summary>
    public static string ToIo(string rootPath, string rel) => ExactPath.ForIo(ToFull(rootPath, rel));
}
