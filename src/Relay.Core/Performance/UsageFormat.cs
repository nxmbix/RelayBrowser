namespace Relay.Core.Performance;

/// <summary>Number formatting and maths for the task manager.</summary>
public static class UsageFormat
{
    /// <summary>"0 KB", "840 KB", "126 MB", "1.4 GB" (1 KB = 1024 bytes, like Windows Task Manager).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 KB";
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            < (long)mb => $"{Math.Max(1, Math.Round(bytes / kb)):0} KB",
            < (long)(10 * gb) when bytes >= (long)gb => $"{bytes / gb:0.0} GB",
            >= (long)(10 * gb) => $"{bytes / gb:0} GB",
            _ => $"{bytes / mb:0.#} MB",
        };
    }

    /// <summary>CPU use between two samples, as a share of the whole machine (0–100).</summary>
    public static double CpuPercent(TimeSpan previousCpu, TimeSpan currentCpu, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0 || currentCpu < previousCpu) return 0;
        var pct = (currentCpu - previousCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * processorCount) * 100;
        return Math.Clamp(pct, 0, 100);
    }

    public static string Percent(double value) => value < 0.05 ? "0" : value.ToString("0.0");
}
