using System.IO.Compression;

namespace Relay.Core.Setup;

public readonly record struct ExtractProgress(int Done, int Total, long BytesDone, long BytesTotal, string Current);

/// <summary>
/// Unpacks the RELAY application payload (a ZIP) into an install folder.
/// Rejects entries that would land outside the folder ("zip-slip") and removes files
/// left over from an older version.
/// </summary>
public static class PayloadExtractor
{
    public static (int Files, long Bytes) Measure(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        return (files.Count, files.Sum(e => e.Length));
    }

    public static void Extract(Stream zip, string targetDir, IProgress<ExtractProgress>? progress = null, CancellationToken cancel = default)
    {
        var root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var total = entries.Count;
        var bytesTotal = entries.Sum(e => e.Length);
        long bytesDone = 0;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < entries.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = entries[i];
            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            var dest = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The installer package contains an unsafe path: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".relay-new";
            using (var src = entry.Open())
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                src.CopyTo(dst);
            File.Move(tmp, dest, overwrite: true);
            written.Add(dest);

            bytesDone += entry.Length;
            progress?.Report(new ExtractProgress(i + 1, total, bytesDone, bytesTotal, relative));
        }

        RemoveStaleFiles(root, written);
    }

    /// <summary>Deletes files from a previous version that aren't part of the new payload.</summary>
    private static void RemoveStaleFiles(string root, HashSet<string> keep)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (keep.Contains(file)) continue;
            var rel = Path.GetRelativePath(root, file);
            // Never touch a data folder or the portable marker if someone placed them here.
            if (rel.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch { /* in use or protected: harmless to leave */ }
        }
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }

    /// <summary>
    /// Guards uninstall: only folders that clearly contain RELAY (and aren't a drive root or a
    /// well-known user folder) may be deleted.
    /// </summary>
    public static bool IsSafeToDelete(string dir, IEnumerable<string> protectedFolders)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) == full) return false;
        foreach (var p in protectedFolders)
        {
            if (string.IsNullOrEmpty(p)) continue;
            var pf = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(pf, full, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return File.Exists(Path.Combine(full, "Relay.exe")) || File.Exists(Path.Combine(full, "Relay.dll"));
    }
}
