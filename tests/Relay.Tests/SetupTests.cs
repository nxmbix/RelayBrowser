using System.IO.Compression;
using Relay.Core.Setup;
using Xunit;

namespace Relay.Tests;

public sealed class SetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-setup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Extracts_reports_progress_and_removes_stale_files()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "old"));
        File.WriteAllText(Path.Combine(_dir, "old", "stale.dll"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "Data"));
        File.WriteAllText(Path.Combine(_dir, "Data", "keep.db"), "x");

        var reports = new List<ExtractProgress>();
        using var zip = Zip(("Relay.exe", "exe"), ("NewTab/index.html", "<html>"));
        Assert.Equal((2, 9L), PayloadExtractor.Measure(zip));
        zip.Position = 0;
        PayloadExtractor.Extract(zip, _dir, new SyncProgress(reports.Add));

        Assert.True(File.Exists(Path.Combine(_dir, "Relay.exe")));
        Assert.True(File.Exists(Path.Combine(_dir, "NewTab", "index.html")));
        Assert.False(File.Exists(Path.Combine(_dir, "old", "stale.dll")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "old")));
        Assert.True(File.Exists(Path.Combine(_dir, "Data", "keep.db")));
        Assert.Equal(2, reports[^1].Done);
    }

    [Fact]
    public void Rejects_zip_slip()
    {
        using var zip = Zip(("../evil.txt", "x"));
        Assert.Throws<InvalidDataException>(() => PayloadExtractor.Extract(zip, _dir));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
    }

    [Fact]
    public void Only_deletes_real_relay_folders()
    {
        Directory.CreateDirectory(_dir);
        Assert.False(PayloadExtractor.IsSafeToDelete(_dir, []));          // no Relay.exe
        File.WriteAllText(Path.Combine(_dir, "Relay.exe"), "x");
        Assert.True(PayloadExtractor.IsSafeToDelete(_dir, []));
        Assert.False(PayloadExtractor.IsSafeToDelete(_dir, [_dir]));      // protected folder
        Assert.False(PayloadExtractor.IsSafeToDelete(Path.GetPathRoot(_dir)!, []));
    }

    private sealed class SyncProgress(Action<ExtractProgress> a) : IProgress<ExtractProgress>
    {
        public void Report(ExtractProgress value) => a(value);
    }
}
