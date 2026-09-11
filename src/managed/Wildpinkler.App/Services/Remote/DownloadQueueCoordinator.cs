using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public enum DownloadJobState
{
    Queued,
    Resolving,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled
}

public sealed partial class DownloadJob : ObservableObject
{
    private string _name = "Download";
    private string? _subtitle;
    private DownloadJobState _state = DownloadJobState.Queued;
    private long _bytesDownloaded;
    private long? _totalBytes;
    private string? _error;
    private string? _remedy;

    public DownloadJob(RemoteLink link, string siteId, IReadOnlyList<string>? confirmedGameIds = null)
    {
        Link = link;
        SiteId = siteId;
        Key = Guid.NewGuid().ToString("N");
        ConfirmedGameIds = confirmedGameIds ?? Array.Empty<string>();
    }

    /// <summary>Games the user confirmed in the download dialog; empty means "all games".</summary>
    public IReadOnlyList<string> ConfirmedGameIds { get; }

    /// <summary>Stable identity so the bound list can be reconciled instead of rebuilt.</summary>
    public string Key { get; }

    public RemoteLink Link { get; }
    public string SiteId { get; }
    public ModEntry? Entry { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string? Subtitle
    {
        get => _subtitle;
        set
        {
            if (SetProperty(ref _subtitle, value))
                OnPropertyChanged(nameof(RowSubtitle));
        }
    }
    public string? Error { get => _error; set => SetProperty(ref _error, value); }
    public string? Remedy { get => _remedy; set => SetProperty(ref _remedy, value); }

    public DownloadJobState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(RowSubtitle));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFaulted));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(CanReveal));
        }
    }

    public long BytesDownloaded
    {
        get => _bytesDownloaded;
        set
        {
            if (SetProperty(ref _bytesDownloaded, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(RowSubtitle));
            }
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(RowSubtitle));
            }
        }
    }

    public double Progress => TotalBytes is > 0 ? (double)BytesDownloaded / TotalBytes.Value : 0;

    public string StateText => State switch
    {
        DownloadJobState.Queued => "Queued",
        DownloadJobState.Resolving => "Getting details",
        DownloadJobState.Downloading => "Downloading",
        DownloadJobState.Verifying => "Verifying",
        DownloadJobState.Completed => "Completed",
        DownloadJobState.Failed => "Failed",
        _ => "Cancelled"
    };

    public bool IsActive => State is DownloadJobState.Queued or DownloadJobState.Resolving or DownloadJobState.Downloading or DownloadJobState.Verifying;
    public bool IsFaulted => State is DownloadJobState.Failed or DownloadJobState.Cancelled;
    public bool CanCancel => IsActive;
    public bool CanRetry => State is DownloadJobState.Failed or DownloadJobState.Cancelled;
    public bool CanReveal => State == DownloadJobState.Completed && !string.IsNullOrWhiteSpace(Entry?.ArchivePath);
    public bool CanOpenPage => Link.IsDownloadable;

    /// <summary>One trimmed line so the row reflows instead of popping columns in and out.</summary>
    public string RowSubtitle
    {
        get
        {
            var size = TotalBytes is > 0 ? $" · {FormatBytes(BytesDownloaded)} of {FormatBytes(TotalBytes.Value)}" : string.Empty;
            return string.IsNullOrWhiteSpace(Subtitle) ? StateText + size : $"{Subtitle} · {StateText}{size}";
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>Everything a link resolves to before any bytes are transferred.</summary>
public sealed record RemoteDownloadPreview(
    RemoteLink Link,
    string SiteName,
    RemoteModMetadata Mod,
    RemoteFileMetadata File,
    RemoteAccount Account);

/// <summary>
/// Orchestrates downloads for any registered site. Every mutation of <see cref="Jobs"/> and of a job
/// is marshalled to the UI thread, because activation arrives on a background thread.
/// </summary>
public sealed class DownloadQueueCoordinator
{
    private readonly RemoteArchiveAcquisitionService _acquisition;
    private DispatcherQueue? _dispatcher;

    public DownloadQueueCoordinator(RemoteArchiveAcquisitionService acquisition) => _acquisition = acquisition;

    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    public event EventHandler<ModEntry>? EntryUpdated;

    /// <summary>Raised for a link that cannot be downloaded, so the shell can explain rather than ignore it.</summary>
    public event EventHandler<RemoteLink>? LinkRejected;

    public void AttachDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Resolves everything a link can tell us without downloading anything, so the user can review a
    /// pre-filled record and be warned about a missing local game before any bytes move.
    /// </summary>
    public Task<RemoteDownloadPreview> PreviewAsync(RemoteLink link, CancellationToken cancellationToken = default) =>
        _acquisition.PreviewAsync(link, cancellationToken);

    public DownloadJob? Enqueue(RemoteLink link, IReadOnlyList<string>? confirmedGameIds = null)
    {
        if (!link.IsDownloadable)
        {
            Post(() => LinkRejected?.Invoke(this, link));
            return null;
        }

        var job = new DownloadJob(link, link.SiteId, confirmedGameIds);
        Post(() => Jobs.Add(job));
        _ = RunAsync(job);
        return job;
    }

    public DownloadJob? Retry(DownloadJob job) => job.CanRetry ? Enqueue(job.Link, job.ConfirmedGameIds) : null;

    public void ClearFinished()
    {
        Post(() =>
        {
            // Removed one at a time: clearing would reset the list and drop selection and scroll.
            foreach (var job in Jobs.Where(item => !item.IsActive).ToList())
                Jobs.Remove(job);
        });
    }

    private async Task RunAsync(DownloadJob job)
    {
        try
        {
            var progress = new Progress<RemoteAcquisitionProgress>(update => Set(job, () =>
            {
                job.State = update.Phase switch
                {
                    RemoteAcquisitionPhase.Resolving => DownloadJobState.Resolving,
                    RemoteAcquisitionPhase.Downloading => DownloadJobState.Downloading,
                    _ => DownloadJobState.Verifying
                };
                job.BytesDownloaded = update.BytesDownloaded;
                job.TotalBytes = update.TotalBytes;
                if (!string.IsNullOrWhiteSpace(update.FileName))
                    job.Name = update.FileName;
                if (!string.IsNullOrWhiteSpace(update.ModName))
                    job.Subtitle = $"{update.ModName} · {update.SiteName}";
                if (update.Entry is not null)
                {
                    job.Entry = update.Entry;
                    update.Entry.DownloadProgress = job.Progress;
                }
            }));
            var result = await _acquisition.AcquireAsync(job.Link, confirmedGameIds: job.ConfirmedGameIds, progress: progress, cancellationToken: job.Cancellation.Token);

            Set(job, () =>
            {
                job.Entry = result.Entry;
                job.BytesDownloaded = result.File.SizeInBytes ?? job.BytesDownloaded;
                job.State = DownloadJobState.Completed;
                EntryUpdated?.Invoke(this, result.Entry);
            });
        }
        catch (OperationCanceledException)
        {
            Set(job, () => job.State = DownloadJobState.Cancelled);
        }
        catch (RemoteSiteException exception)
        {
            Set(job, () =>
            {
                job.Error = exception.Message;
                job.Remedy = exception.Remedy;
                job.State = DownloadJobState.Failed;
            });
        }
        catch (Exception exception)
        {
            Set(job, () =>
            {
                job.Error = exception.Message;
                job.State = DownloadJobState.Failed;
            });
        }
    }

    private void Set(DownloadJob job, Action mutate) => Post(mutate);

    private void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasThreadAccess)
            action();
        else
            dispatcher.TryEnqueue(() => action());
    }
}
