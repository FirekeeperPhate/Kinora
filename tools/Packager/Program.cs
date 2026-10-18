// Packs a published Kinora for Linux and macOS, from Windows. Windows' own tar and zip can't
// mark a file as executable; here every entry gets its Unix permissions.
//
//   dotnet run --project tools/Packager -- linux <publish dir> <out .tar.gz> <version> <icon png>
//   dotnet run --project tools/Packager -- macos <publish dir> <out .zip>    <version> <icns>

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

if (args.Length != 5 || args[0] is not ("linux" or "macos"))
{
    Console.Error.WriteLine("usage: linux|macos <publish dir> <output> <version> <icon>");
    return 2;
}
var (kind, source, output, version, icon) = (args[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[4]));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
if (File.Exists(output))
    File.Delete(output);

if (kind == "linux")
    Linux();
else
    MacOs();
Console.WriteLine($"{Path.GetFileName(output)}: {new FileInfo(output).Length / 1024.0 / 1024.0:0.0} MB");
return 0;

// ---- Linux: Kinora/ with the program, its icon, a .desktop entry and install.sh ----

void Linux()
{
    using var file = File.Create(output);
    using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax);

    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(source, path).Replace('\\', '/');
        var executable = rel == "Kinora" || rel.EndsWith(".so", StringComparison.Ordinal) || rel == "createdump";
        AddFile(tar, "Kinora/" + rel, File.ReadAllBytes(path), executable);
    }
    AddFile(tar, "Kinora/kinora.png", File.ReadAllBytes(icon), false);
    AddFile(tar, "Kinora/kinora.desktop", Encoding.UTF8.GetBytes(DesktopEntry("@APPDIR@")), false);
    AddFile(tar, "Kinora/install.sh", Encoding.UTF8.GetBytes(InstallScript), true);
    AddFile(tar, "Kinora/README.txt", Encoding.UTF8.GetBytes(LinuxReadme), false);
}

static void AddFile(TarWriter tar, string name, byte[] data, bool executable)
{
    var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
    {
        DataStream = new MemoryStream(data),
        Mode = executable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        ModificationTime = DateTimeOffset.UtcNow,
    };
    tar.WriteEntry(entry);
}

string DesktopEntry(string dir) => $"""
    [Desktop Entry]
    Type=Application
    Name=Kinora
    Comment=Movie and TV show library
    Exec="{dir}/Kinora"
    Icon={dir}/kinora.png
    Terminal=false
    Categories=AudioVideo;Video;Player;
    StartupWMClass=Kinora
    Version=1.0
    X-Kinora-Version={version}

    """;

// ---- macOS: Kinora.app, zipped with the permissions the Finder honours ----

void MacOs()
{
    using var file = File.Create(output);
    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
    const string app = "Kinora.app/Contents/";

    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(source, path).Replace('\\', '/');
        var executable = rel == "Kinora" || rel.EndsWith(".dylib", StringComparison.Ordinal) || rel == "createdump";
        AddZip(zip, app + "MacOS/" + rel, File.ReadAllBytes(path), executable);
    }
    AddZip(zip, app + "Info.plist", Encoding.UTF8.GetBytes(InfoPlist()), false);
    AddZip(zip, app + "Resources/kinora.icns", File.ReadAllBytes(icon), false);
    zip.Dispose();
    file.Dispose();
    MarkMadeByUnix(output);
}

// .NET on Windows writes "made by MS-DOS" in every entry, and unzippers then ignore the Unix
// permissions (the app would not start). Rewrite that byte to "made by Unix" in the central
// directory.
static void MarkMadeByUnix(string path)
{
    var bytes = File.ReadAllBytes(path);
    var eocd = -1;
    for (var i = bytes.Length - 22; i >= 0; i--)
        if (BitConverter.ToUInt32(bytes, i) == 0x06054b50) { eocd = i; break; }
    if (eocd < 0)
        throw new InvalidDataException("End of central directory not found.");
    int entries = BitConverter.ToUInt16(bytes, eocd + 10);
    var offset = (int)BitConverter.ToUInt32(bytes, eocd + 16);
    for (var n = 0; n < entries; n++)
    {
        if (BitConverter.ToUInt32(bytes, offset) != 0x02014b50)
            throw new InvalidDataException("Bad central directory entry.");
        bytes[offset + 5] = 3;   // host system: Unix
        offset += 46 + BitConverter.ToUInt16(bytes, offset + 28) + BitConverter.ToUInt16(bytes, offset + 30) + BitConverter.ToUInt16(bytes, offset + 32);
    }
    File.WriteAllBytes(path, bytes);
}

static void AddZip(ZipArchive zip, string name, byte[] data, bool executable)
{
    var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
    // Unix mode in the high 16 bits, with the "regular file" type bits: what macOS unzip reads
    entry.ExternalAttributes = (executable ? 0x81ED : 0x81A4) << 16;   // 0100755 / 0100644
    using var stream = entry.Open();
    stream.Write(data);
}

string InfoPlist() => $"""
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
        <key>CFBundleName</key><string>Kinora</string>
        <key>CFBundleDisplayName</key><string>Kinora</string>
        <key>CFBundleIdentifier</key><string>com.phate.kinora</string>
        <key>CFBundleExecutable</key><string>Kinora</string>
        <key>CFBundleIconFile</key><string>kinora.icns</string>
        <key>CFBundlePackageType</key><string>APPL</string>
        <key>CFBundleShortVersionString</key><string>{version}</string>
        <key>CFBundleVersion</key><string>{version}</string>
        <key>LSMinimumSystemVersion</key><string>12.0</string>
        <key>LSApplicationCategoryType</key><string>public.app-category.entertainment</string>
        <key>NSHighResolutionCapable</key><true/>
        <key>NSHumanReadableCopyright</key><string>Copyright © 2026 Phate</string>
    </dict>
    </plist>

    """;

partial class Program
{
    const string InstallScript = """
        #!/bin/sh
        # Installs Kinora for the current user: the program in ~/.local/share/kinora,
        # a menu entry, and a "kinora" command in ~/.local/bin. Run it again to update.
        # Kinora's own data (library, settings) is in ~/.local/share/Kinora and is not touched.
        set -e
        here="$(cd "$(dirname "$0")" && pwd)"
        dest="${XDG_DATA_HOME:-$HOME/.local/share}/kinora"
        apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
        bin="$HOME/.local/bin"

        if [ "$1" = "--uninstall" ]; then
            rm -rf "$dest" "$apps/kinora.desktop" "$bin/kinora"
            echo "Kinora removed (the library in ~/.local/share/Kinora is kept)."
            exit 0
        fi

        rm -rf "$dest"
        mkdir -p "$dest" "$apps" "$bin"
        cp -R "$here/." "$dest/"
        rm -f "$dest/install.sh"
        sed "s|@APPDIR@|$dest|g" "$here/kinora.desktop" > "$apps/kinora.desktop"
        rm -f "$dest/kinora.desktop"
        ln -sf "$dest/Kinora" "$bin/kinora"
        command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" || true
        echo "Kinora installed in $dest. Start it from the menu, or with: kinora"
        echo "To remove it: $here/install.sh --uninstall"

        """;

    const string LinuxReadme = """
        Kinora for Linux (x64)

        Run ./install.sh to install it for your user (menu entry and the "kinora" command),
        or start ./Kinora straight from this folder.

        Needs a desktop with X11 or XWayland, and fontconfig (present on every desktop install).
        Videos open with the player your desktop associates with them (xdg-open).

        """;
}
