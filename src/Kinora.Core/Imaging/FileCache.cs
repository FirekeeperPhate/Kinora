namespace Kinora.Core.Imaging;

/// <summary>
/// Downloaded files kept as they came (logos: PNG with transparency, which the JPEG cache would
/// flatten).
/// </summary>
public sealed class FileCache(string directory, string extension)
{
    public string PathFor(string key) => Path.Combine(directory, key + extension);

    public bool Has(string key) => File.Exists(PathFor(key));

    public void Save(string key, byte[] data)
    {
        Directory.CreateDirectory(directory);
        var target = PathFor(key);
        var temp = target + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, target, overwrite: true);
    }
}
