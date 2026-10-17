namespace Kinora.Core.Models;

public enum LibraryKind
{
    Movies = 0,
    Series = 1,
}

/// <summary>
/// One of the folders the library is built from. Items store their paths relative to it, so
/// when a share is mounted somewhere else (another drive letter, another OS) only this path
/// changes and nothing has to be scanned again.
/// </summary>
public sealed class LibraryRoot
{
    public long Id { get; set; }
    public LibraryKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long? LastScan { get; set; }

    public override string ToString() => Name;
}
