namespace Kinora.Core;

/// <summary>
/// Paths handed to the file functions exactly as they are. Windows tidies a path before it
/// reaches the disk and drops a final dot or space from every name: "S.W.A.T." becomes
/// "S.W.A.T", which may well be another folder next to it. A "\\?\" path goes through
/// untouched, and so do the paths of everything found inside a folder opened that way.
/// Elsewhere than Windows nothing changes.
/// </summary>
public static class ExactPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>@"H:\S.W.A.T." → @"\\?\H:\S.W.A.T."; @"\\nas\video\x" → @"\\?\UNC\nas\video\x".</summary>
    public static string ForIo(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(Prefix, StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return path;
        // Inside "\\?\" nothing is translated any more: the separators must already be the right ones
        path = path.Replace('/', '\\');
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? UncPrefix + path[2..] : Prefix + path;
    }

    /// <summary>The usual spelling, for showing, launching and picture keys: @"\\?\H:\x" → @"H:\x".</summary>
    public static string Display(string path) =>
        path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase) ? @"\\" + path[UncPrefix.Length..]
        : path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..]
        : path;
}
