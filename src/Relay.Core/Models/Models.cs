namespace Relay.Core.Models;

public sealed class HistoryEntry
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; }
    public int VisitCount { get; set; } = 1;
}

/// <summary>A bookmark or a bookmark folder (when <see cref="IsFolder"/> is true).</summary>
public sealed class Bookmark
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public bool IsFolder { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public int Position { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class QuickLink
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Position { get; set; }
}

public sealed class ReadingListItem
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}

public enum DownloadStatus { InProgress, Paused, Completed, Failed, Cancelled }

public sealed class DownloadRecord
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public DownloadStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

public enum PermissionDecision { Ask = 0, Allow = 1, Block = 2 }

public sealed class SitePermission
{
    public string Origin { get; set; } = string.Empty;
    /// <summary>The CoreWebView2PermissionKind name, e.g. "Camera", "Microphone", "Geolocation".</summary>
    public string Kind { get; set; } = string.Empty;
    public PermissionDecision Decision { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SessionTab
{
    public int WindowIndex { get; set; }
    public int Position { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? GroupName { get; set; }
    public string? GroupColor { get; set; }
    public bool GroupCollapsed { get; set; }
}
