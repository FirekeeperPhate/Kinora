using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kinora.Core.Moving;

public sealed record MoveProgress(long BytesDone, long BytesTotal, string? CurrentFile);

public enum MoveOutcome
{
    Moved,
    /// <summary>Something with that name is already at the destination: nothing was touched.</summary>
    Conflict,
    /// <summary>Stopped by the user or by an error before the end: the source is as it was.</summary>
    Failed,
    Cancelled,
    /// <summary>Copied and checked, but the source could not be deleted completely.</summary>
    MovedSourceLeft,
}

public sealed record MoveResult(MoveOutcome Outcome, string? Error = null, bool Renamed = false);

/// <summary>
/// Moves files or a folder from one place to another, possibly on another share or disk.
/// On the same volume it is a rename. Otherwise:
/// 1. copy everything into a hidden "…kinora-moving" folder next to the destination;
/// 2. check every size;
/// 3. rename the folder into place (instant, same share);
/// 4. only then delete the source.
/// Anything going wrong before step 3 removes the partial copy and leaves the source as it was.
/// On Windows the copy goes through CopyFileEx, which lets an SMB server copy between its own
/// shares without the data crossing the network.
/// </summary>
public static class FolderMover
{
    public const string WorkSuffix = ".kinora-moving";

    /// <summary>Moves a whole folder into <paramref name="destinationParent"/>, keeping its name.</summary>
    public static Task<MoveResult> MoveFolderAsync(string sourceFolder, string destinationParent, IProgress<MoveProgress>? progress, CancellationToken ct) =>
        Task.Run(() => MoveFolder(sourceFolder, destinationParent, progress, ct), CancellationToken.None);

    /// <summary>Moves loose files (a video and its companions) into <paramref name="destinationFolder"/>.</summary>
    public static Task<MoveResult> MoveFilesAsync(IReadOnlyList<string> files, string destinationFolder, IProgress<MoveProgress>? progress, CancellationToken ct) =>
        Task.Run(() => MoveFiles(files, destinationFolder, progress, ct), CancellationToken.None);

    private static MoveResult MoveFolder(string source, string destinationParent, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        source = Path.TrimEndingDirectorySeparator(source);
        var name = Path.GetFileName(source);
        var target = Path.Combine(destinationParent, name);
        if (Directory.Exists(target) || File.Exists(target))
            return new MoveResult(MoveOutcome.Conflict, $"“{name}” already exists in {destinationParent}.");
        if (!Directory.Exists(source))
            return new MoveResult(MoveOutcome.Failed, $"{source} is not there any more.");

        if (SameVolume(source, destinationParent))
        {
            try
            {
                Directory.Move(source, target);
                return new MoveResult(MoveOutcome.Moved, Renamed: true);
            }
            catch (IOException)
            {
                // Same letter but not the same file system after all (a mount point): copy instead
            }
        }

        // A dot in front: Kinora, Emby and Jellyfin all skip it while the copy is half done
        var work = Path.Combine(destinationParent, "." + name + WorkSuffix);
        try
        {
            if (Directory.Exists(work))
                DeleteTree(work);   // left over from an interrupted move
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            var total = files.Sum(f => f.Length);
            long done = 0;
            Directory.CreateDirectory(work);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file.FullName);
                var copy = Path.Combine(work, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                var before = done;
                CopyFile(file.FullName, copy, bytes => progress?.Report(new MoveProgress(before + bytes, total, relative)), ct);
                done += file.Length;
            }
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(work, Path.GetRelativePath(source, dir)));
            // Folder attributes too: a read-only or system flag is what lets Desktop.ini set an icon
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
                new DirectoryInfo(Path.Combine(work, Path.GetRelativePath(source, dir))).Attributes = new DirectoryInfo(dir).Attributes;

            // Every byte there before anything is deleted
            foreach (var file in files)
            {
                var copy = new FileInfo(Path.Combine(work, Path.GetRelativePath(source, file.FullName)));
                if (!copy.Exists || copy.Length != file.Length)
                    throw new IOException($"The copy of {file.Name} is incomplete.");
            }
            Directory.Move(work, target);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTree(work);
            return new MoveResult(MoveOutcome.Cancelled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            TryDeleteTree(work);
            return new MoveResult(MoveOutcome.Failed, e.Message);
        }

        try
        {
            DeleteTree(source);
            return new MoveResult(MoveOutcome.Moved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MoveResult(MoveOutcome.MovedSourceLeft, e.Message);
        }
    }

    private static MoveResult MoveFiles(IReadOnlyList<string> files, string destination, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        var conflicts = files.Select(f => Path.Combine(destination, Path.GetFileName(f))).Where(File.Exists).ToList();
        if (conflicts.Count > 0)
            return new MoveResult(MoveOutcome.Conflict, $"“{Path.GetFileName(conflicts[0])}” already exists in {destination}.");

        if (SameVolume(files[0], destination))
        {
            var moved = new List<(string From, string To)>();
            try
            {
                foreach (var f in files)
                {
                    var to = Path.Combine(destination, Path.GetFileName(f));
                    File.Move(f, to);
                    moved.Add((f, to));
                }
                return new MoveResult(MoveOutcome.Moved, Renamed: true);
            }
            catch (IOException) when (moved.Count == 0)
            {
                // Not really the same file system: copy instead
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                foreach (var (from, to) in moved)
                    try { File.Move(to, from); } catch (IOException) { }
                return new MoveResult(MoveOutcome.Failed, e.Message);
            }
        }

        var infos = files.Select(f => new FileInfo(f)).ToList();
        var total = infos.Sum(f => f.Length);
        var copies = new List<string>();
        long done = 0;
        try
        {
            foreach (var file in infos)
            {
                ct.ThrowIfCancellationRequested();
                var work = Path.Combine(destination, "." + file.Name + WorkSuffix);
                copies.Add(work);
                var before = done;
                CopyFile(file.FullName, work, bytes => progress?.Report(new MoveProgress(before + bytes, total, file.Name)), ct);
                done += file.Length;
                if (new FileInfo(work).Length != file.Length)
                    throw new IOException($"The copy of {file.Name} is incomplete.");
            }
            for (var i = 0; i < infos.Count; i++)
            {
                File.Move(copies[i], Path.Combine(destination, infos[i].Name));
                copies[i] = Path.Combine(destination, infos[i].Name);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            foreach (var c in copies.Where(c => c.EndsWith(WorkSuffix, StringComparison.Ordinal)))
                try { File.Delete(c); } catch (IOException) { }
            return e is OperationCanceledException ? new MoveResult(MoveOutcome.Cancelled) : new MoveResult(MoveOutcome.Failed, e.Message);
        }

        try
        {
            foreach (var file in infos)
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
            }
            return new MoveResult(MoveOutcome.Moved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MoveResult(MoveOutcome.MovedSourceLeft, e.Message);
        }
    }

    /// <summary>For tests: take the copy path even on one disk.</summary>
    internal static bool ForceCopy { get; set; }

    private static bool SameVolume(string a, string b) => !ForceCopy &&
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // ---- copying ----

    private static void CopyFile(string from, string to, Action<long> progress, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            WindowsCopy(from, to, progress, ct);
        else
            StreamCopy(from, to, progress, ct);

    }

    private static void StreamCopy(string from, string to, Action<long> progress, CancellationToken ct)
    {
        using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[4 << 20];
            long done = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
                done += n;
                progress(done);
            }
        }
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    private static void WindowsCopy(string from, string to, Action<long> progress, CancellationToken ct)
    {
        var cancel = 0;
        CopyProgressRoutine routine = (_, transferred, _, _, _, _, _, _, _) =>
        {
            progress(transferred);
            return ct.IsCancellationRequested ? ProgressCancel : ProgressContinue;
        };
        var ok = CopyFileEx(from, to, routine, IntPtr.Zero, ref cancel, CopyFileFailIfExists);
        GC.KeepAlive(routine);
        if (ok)
            return;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorRequestAborted || ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        throw new Win32Exception(error);
    }

    private const uint ProgressContinue = 0;
    private const uint ProgressCancel = 1;
    private const uint CopyFileFailIfExists = 0x1;
    private const int ErrorRequestAborted = 1235;

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
                                              uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine progressRoutine,
                                          IntPtr data, ref int cancel, uint copyFlags);

    // ---- deleting ----

    /// <summary>Deletes a folder even with read-only files in it (Desktop.ini).</summary>
    internal static void DeleteTree(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder))
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        Directory.Delete(folder, recursive: true);
    }

    private static void TryDeleteTree(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                DeleteTree(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
