using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Relay.Core.Performance;

namespace Relay.Browser;

/// <summary>One line in the task manager: a process and the tabs it is drawing.</summary>
public sealed record ProcessRow(int ProcessId, string Name, string Detail, long MemoryBytes, double? CpuPercent, IReadOnlyList<BrowserTab> Tabs, bool IsTab);

/// <summary>Everything RELAY is using right now: its own process plus every WebView2 engine process.</summary>
public sealed record UsageSnapshot(IReadOnlyList<ProcessRow> Rows, long TotalMemoryBytes, double? TotalCpuPercent, int UnloadedTabs);

/// <summary>
/// Measures real memory and CPU per process, the same way Windows Task Manager does (private working set),
/// and maps WebView2 renderer processes back to the tabs they belong to.
/// </summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<int, (TimeSpan Cpu, long Ticks)> _last = [];

    public async Task<UsageSnapshot> SampleAsync(CoreWebView2Environment? env, IReadOnlyList<BrowserTab> tabs)
    {
        var rows = new List<ProcessRow>();
        var self = Environment.ProcessId;
        rows.Add(Measure(self, "RELAY", "Windows, tab strip and address bar", [], false));

        var byFrame = new Dictionary<uint, BrowserTab>();
        foreach (var t in tabs)
        {
            try { if (t.Core is { } c) byFrame[c.FrameId] = t; } catch { /* tab closing */ }
        }

        IReadOnlyList<CoreWebView2ProcessExtendedInfo> infos = [];
        if (env is not null)
        {
            try { infos = await env.GetProcessExtendedInfosAsync(); } catch { infos = []; }
        }

        foreach (var info in infos)
        {
            int pid;
            CoreWebView2ProcessKind kind;
            IReadOnlyList<CoreWebView2FrameInfo> frames;
            try
            {
                pid = info.ProcessInfo.ProcessId;
                kind = info.ProcessInfo.Kind;
                frames = info.AssociatedFrameInfos ?? [];
            }
            catch { continue; }

            if (kind == CoreWebView2ProcessKind.Renderer)
            {
                var owned = new List<BrowserTab>();
                var subframeHosts = new List<string>();
                foreach (var f in frames)
                {
                    try
                    {
                        if (byFrame.TryGetValue(f.FrameId, out var tab)) { if (!owned.Contains(tab)) owned.Add(tab); }
                        else if (Uri.TryCreate(f.Source, UriKind.Absolute, out var u) && u.Host.Length > 0 && !subframeHosts.Contains(u.Host)) subframeHosts.Add(u.Host);
                    }
                    catch { }
                }
                if (owned.Count > 0)
                {
                    var name = string.Join(", ", owned.Take(3).Select(TabLabel)) + (owned.Count > 3 ? $" (+{owned.Count - 3})" : string.Empty);
                    var detail = owned.Count == 1 ? Relay.Core.Navigation.UrlDisplay.Host(owned[0].Url) : $"{owned.Count} tabs share this process";
                    if (owned.All(t => t.IsSleeping)) detail += " · sleeping";
                    rows.Add(Measure(pid, name, detail, owned, true));
                }
                else if (subframeHosts.Count > 0)
                    rows.Add(Measure(pid, "Subframe: " + string.Join(", ", subframeHosts.Take(2)), "Embedded content (ads, videos, widgets)", [], false));
                else
                    rows.Add(Measure(pid, "Page renderer", "Starting up or shutting down", [], false));
                continue;
            }

            var (title, sub) = kind switch
            {
                CoreWebView2ProcessKind.Browser => ("Browser engine", "Coordinates tabs, network and storage"),
                CoreWebView2ProcessKind.Gpu => ("GPU process", "Draws pages and video"),
                CoreWebView2ProcessKind.Utility => ("Utility process", "Network, audio or storage service"),
                _ => ("Engine process", kind.ToString()),
            };
            rows.Add(Measure(pid, title, sub, [], false));
        }

        var alive = rows.Select(r => r.ProcessId).ToHashSet();
        foreach (var gone in _last.Keys.Where(k => !alive.Contains(k)).ToList()) _last.Remove(gone);

        var unloaded = tabs.Count(t => !t.IsCreated);
        var total = rows.Sum(r => r.MemoryBytes);
        double? cpu = rows.All(r => r.CpuPercent is null) ? null : Math.Min(100, rows.Sum(r => r.CpuPercent ?? 0));
        return new UsageSnapshot(rows, total, cpu, unloaded);
    }

    private static string TabLabel(BrowserTab t) => (t.IsPrivate ? "Private: " : string.Empty) + (string.IsNullOrWhiteSpace(t.Title) ? "Tab" : t.Title);

    private ProcessRow Measure(int pid, string name, string detail, IReadOnlyList<BrowserTab> tabs, bool isTab)
    {
        long memory = 0;
        double? cpu = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            memory = PrivateWorkingSet(p) ?? p.WorkingSet64;
            var now = p.TotalProcessorTime;
            var ticks = Stopwatch.GetTimestamp();
            if (_last.TryGetValue(pid, out var prev))
                cpu = UsageFormat.CpuPercent(prev.Cpu, now, Stopwatch.GetElapsedTime(prev.Ticks, ticks), Environment.ProcessorCount);
            _last[pid] = (now, ticks);
        }
        catch { /* the process just exited or can't be queried */ }
        return new ProcessRow(pid, name, detail, memory, cpu, tabs, isTab);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    /// <summary>Private working set: the "Memory" column of Windows Task Manager (Windows 10 1809 and later).</summary>
    private static long? PrivateWorkingSet(Process p)
    {
        try
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            if (!GetProcessMemoryInfo(p.Handle, ref c, c.cb) || c.PrivateWorkingSetSize == 0) return null;
            return (long)c.PrivateWorkingSetSize;
        }
        catch { return null; }
    }
}
