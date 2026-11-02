using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Relay.Core.Downloads;
using Relay.Core.Models;
using Relay.Core.Navigation;

namespace Relay.Browser;

/// <summary>One download shown in the downloads panel (live or from a previous session).</summary>
public sealed partial class DownloadItem : ObservableObject
{
    private CoreWebView2DownloadOperation? _op;
    private long _lastBytes;
    private DateTime _lastSample = DateTime.UtcNow;

    public DownloadItem(DownloadRecord record, bool isPrivate)
    {
        Record = record;
        IsPrivate = isPrivate;
        _filePath = record.FilePath;
        _totalBytes = record.TotalBytes;
        _receivedBytes = record.ReceivedBytes;
        _status = record.Status;
        _failureReason = record.FailureReason;
    }

    public DownloadRecord Record { get; }
    public bool IsPrivate { get; }
    public string Url => Record.Url;
    public string Host => UrlDisplay.Host(Record.Url);
    public string FileName => Path.GetFileName(FilePath);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileName))] private string _filePath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Progress), nameof(Detail), nameof(IsIndeterminate))] private long _totalBytes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Progress), nameof(Detail))] private long _receivedBytes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Detail), nameof(IsActive), nameof(IsPaused), nameof(IsCompleted), nameof(IsFailed), nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(CanRetry))]
    private DownloadStatus _status;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Detail))] private string? _failureReason;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Detail))] private double _bytesPerSecond;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanResume))] private bool _resumable;

    public double Progress => TotalBytes > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1) : 0;
    public bool IsIndeterminate => TotalBytes <= 0 && IsActive;
    public bool IsActive => Status is DownloadStatus.InProgress;
    public bool IsPaused => Status is DownloadStatus.Paused;
    public bool IsCompleted => Status is DownloadStatus.Completed;
    public bool IsFailed => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;
    public bool CanPause => _op is not null && Status == DownloadStatus.InProgress;
    public bool CanResume => _op is not null && Status == DownloadStatus.Paused && Resumable;
    public bool CanCancel => _op is not null && Status is DownloadStatus.InProgress or DownloadStatus.Paused;
    public bool CanRetry => IsFailed;

    public string Detail => Status switch
    {
        DownloadStatus.InProgress when TotalBytes > 0 =>
            $"{FileNameSafety.FormatBytes(ReceivedBytes)} of {FileNameSafety.FormatBytes(TotalBytes)}" + Speed() + Remaining(),
        DownloadStatus.InProgress => FileNameSafety.FormatBytes(ReceivedBytes) + Speed(),
        DownloadStatus.Paused => $"Paused · {FileNameSafety.FormatBytes(ReceivedBytes)}" + (TotalBytes > 0 ? $" of {FileNameSafety.FormatBytes(TotalBytes)}" : ""),
        DownloadStatus.Completed => $"{FileNameSafety.FormatBytes(Math.Max(TotalBytes, ReceivedBytes))} · {Host}",
        DownloadStatus.Cancelled => "Cancelled",
        _ => FailureReason is { Length: > 0 } r ? $"Failed · {r}" : "Failed",
    };

    private string Speed() => BytesPerSecond > 0 ? $" · {FileNameSafety.FormatBytes((long)BytesPerSecond)}/s" : string.Empty;

    private string Remaining()
    {
        if (BytesPerSecond <= 0 || TotalBytes <= 0) return string.Empty;
        var secs = (TotalBytes - ReceivedBytes) / BytesPerSecond;
        if (secs < 1) return string.Empty;
        var t = TimeSpan.FromSeconds(secs);
        return t.TotalHours >= 1 ? $" · {(int)t.TotalHours} h {t.Minutes} min left" : t.TotalMinutes >= 1 ? $" · {t.Minutes} min left" : $" · {t.Seconds} s left";
    }

    internal void Attach(CoreWebView2DownloadOperation op)
    {
        _op = op;
        TotalBytes = (long)(op.TotalBytesToReceive ?? 0);
        op.BytesReceivedChanged += (_, _) => Sample();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanCancel));
    }

    internal void Detach()
    {
        _op = null;
        BytesPerSecond = 0;
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
    }

    private void Sample()
    {
        if (_op is null) return;
        ReceivedBytes = _op.BytesReceived;
        if (_op.TotalBytesToReceive is ulong t && t > 0) TotalBytes = (long)t;
        var now = DateTime.UtcNow;
        var dt = (now - _lastSample).TotalSeconds;
        if (dt >= 0.75)
        {
            var instant = (ReceivedBytes - _lastBytes) / dt;
            BytesPerSecond = BytesPerSecond <= 0 ? instant : BytesPerSecond * 0.6 + instant * 0.4;
            _lastBytes = ReceivedBytes;
            _lastSample = now;
        }
    }

    public void Pause() { try { _op?.Pause(); } catch { } }
    public void Resume() { try { _op?.Resume(); } catch { } }
    public void Cancel() { try { _op?.Cancel(); } catch { } }
}

/// <summary>
/// Receives WebView2 downloads from every tab: chooses a safe destination, asks for confirmation
/// when needed, tracks progress, and records history (except for private windows).
/// </summary>
public sealed class DownloadManager
{
    private readonly BrowserServices _services;
    private bool _loaded;

    public DownloadManager(BrowserServices services) => _services = services;

    public ObservableCollection<DownloadItem> Items { get; } = [];

    public int ActiveCount => Items.Count(i => i.IsActive || i.IsPaused);

    public event EventHandler? ActiveCountChanged;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            await _services.DownloadsRepo.MarkInterruptedAsync();
            foreach (var r in await _services.DownloadsRepo.GetRecentAsync())
                Items.Add(new DownloadItem(r, isPrivate: false));
        }
        catch (Exception ex) { _services.Paths.LogError("Loading downloads", ex); }
    }

    public async void Handle(CoreWebView2DownloadStartingEventArgs e, bool isPrivate, Dispatcher dispatcher)
    {
        var deferral = e.GetDeferral();
        try
        {
            var op = e.DownloadOperation;
            var dir = _services.DownloadDirectory;
            var suggested = Path.GetFileName(e.ResultFilePath);
            var safeName = FileNameSafety.Sanitize(suggested);
            var host = UrlDisplay.Host(op.Uri);

            if (FileNameSafety.IsDangerous(safeName) &&
                !await _services.Ui.ConfirmDangerousDownloadAsync(safeName, host.Length > 0 ? host : op.Uri))
            {
                e.Cancel = true;
                return;
            }

            string target;
            if (_services.Settings.Current.AskWhereToSave)
            {
                var chosen = await _services.Ui.AskSavePathAsync(Path.Combine(dir, safeName));
                if (chosen is null) { e.Cancel = true; return; }
                // Re-sanitize the name part; the user picked the folder.
                var folder = Path.GetDirectoryName(chosen);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { e.Cancel = true; return; }
                target = Path.Combine(folder, FileNameSafety.Sanitize(Path.GetFileName(chosen)));
            }
            else
            {
                Directory.CreateDirectory(dir);
                target = FileNameSafety.UniquePath(dir, safeName);
                if (!FileNameSafety.IsInside(dir, target)) { e.Cancel = true; return; }
            }

            e.ResultFilePath = target;
            e.Handled = true; // use RELAY's download UI instead of the built-in flyout

            var record = new DownloadRecord
            {
                Url = op.Uri,
                FilePath = target,
                TotalBytes = (long)(op.TotalBytesToReceive ?? 0),
                Status = DownloadStatus.InProgress,
                StartedAt = DateTime.UtcNow,
            };
            if (!isPrivate)
            {
                try { record.Id = await _services.DownloadsRepo.InsertAsync(record); } catch (Exception ex) { _services.Paths.LogError("Saving download", ex); }
            }

            var item = new DownloadItem(record, isPrivate);
            item.Attach(op);
            Items.Insert(0, item);
            ActiveCountChanged?.Invoke(this, EventArgs.Empty);

            op.StateChanged += (_, _) => dispatcher.InvokeAsync(() => OnStateChanged(item, op));
            if (_services.Settings.Current.DownloadNotifications) _services.Ui.NotifyDownload(item, finished: false);
        }
        catch (Exception ex)
        {
            _services.Paths.LogError("Download starting", ex);
            try { e.Cancel = true; } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnStateChanged(DownloadItem item, CoreWebView2DownloadOperation op)
    {
        try
        {
            item.ReceivedBytes = op.BytesReceived;
            item.FilePath = op.ResultFilePath;
            switch (op.State)
            {
                case CoreWebView2DownloadState.InProgress:
                    item.Status = DownloadStatus.InProgress;
                    break;
                case CoreWebView2DownloadState.Completed:
                    item.Status = DownloadStatus.Completed;
                    item.TotalBytes = Math.Max(item.TotalBytes, op.BytesReceived);
                    item.Detach();
                    if (_services.Settings.Current.DownloadNotifications) _services.Ui.NotifyDownload(item, finished: true);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    if (op.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused)
                    {
                        item.Status = DownloadStatus.Paused;
                        item.Resumable = op.CanResume;
                        item.BytesPerSecond = 0;
                    }
                    else if (op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled)
                    {
                        item.Status = DownloadStatus.Cancelled;
                        item.Detach();
                    }
                    else
                    {
                        item.FailureReason = DescribeInterrupt(op.InterruptReason);
                        item.Resumable = op.CanResume;
                        item.Status = op.CanResume ? DownloadStatus.Paused : DownloadStatus.Failed;
                        if (!op.CanResume) item.Detach();
                    }
                    break;
            }
            ActiveCountChanged?.Invoke(this, EventArgs.Empty);

            if (!item.IsPrivate && item.Record.Id > 0)
            {
                item.Record.FilePath = item.FilePath;
                item.Record.TotalBytes = item.TotalBytes;
                item.Record.ReceivedBytes = item.ReceivedBytes;
                item.Record.Status = item.Status;
                item.Record.FailureReason = item.FailureReason;
                await _services.DownloadsRepo.UpdateAsync(item.Record);
            }
        }
        catch (Exception ex) { _services.Paths.LogError("Download state", ex); }
    }

    private static string DescribeInterrupt(CoreWebView2DownloadInterruptReason r) => r switch
    {
        CoreWebView2DownloadInterruptReason.FileNoSpace => "Disk full",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "Permission denied",
        CoreWebView2DownloadInterruptReason.FileNameTooLong => "File name too long",
        CoreWebView2DownloadInterruptReason.FileTooLarge => "File too large",
        CoreWebView2DownloadInterruptReason.FileMalicious => "Blocked: dangerous file",
        CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed => "Security check failed",
        CoreWebView2DownloadInterruptReason.FileHashMismatch => "File corrupted",
        CoreWebView2DownloadInterruptReason.NetworkFailed or CoreWebView2DownloadInterruptReason.NetworkDisconnected => "Network error",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "Network timeout",
        CoreWebView2DownloadInterruptReason.ServerUnauthorized or CoreWebView2DownloadInterruptReason.ServerForbidden => "Server denied access",
        CoreWebView2DownloadInterruptReason.ServerBadContent or CoreWebView2DownloadInterruptReason.ServerFailed => "Server error",
        CoreWebView2DownloadInterruptReason.DownloadProcessCrashed => "Download process stopped",
        _ => "Interrupted",
    };

    /// <summary>Opens a finished file with its default app — only ever on an explicit user click.</summary>
    public static bool Open(DownloadItem item)
    {
        if (!item.IsCompleted || !File.Exists(item.FilePath)) return false;
        Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        return true;
    }

    public static void ShowInFolder(DownloadItem item)
    {
        if (File.Exists(item.FilePath))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", item.FilePath }, UseShellExecute = false });
        else if (Path.GetDirectoryName(item.FilePath) is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    public async Task RemoveAsync(DownloadItem item)
    {
        if (item.CanCancel) item.Cancel();
        Items.Remove(item);
        if (!item.IsPrivate && item.Record.Id > 0) await _services.DownloadsRepo.DeleteAsync(item.Record.Id);
        ActiveCountChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearFinishedAsync()
    {
        foreach (var i in Items.Where(i => !i.IsActive && !i.IsPaused).ToList()) Items.Remove(i);
        await _services.DownloadsRepo.ClearFinishedAsync();
        ActiveCountChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes downloads that belong to private windows (when the last private window closes).</summary>
    public void ForgetPrivate()
    {
        foreach (var i in Items.Where(i => i.IsPrivate && !i.IsActive).ToList()) Items.Remove(i);
    }
}
