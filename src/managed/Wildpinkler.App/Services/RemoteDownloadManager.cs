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

    public DownloadJob(RemoteLink link, string siteId)
    {
        Link = link;
        SiteId = siteId;
        Key = Guid.NewGuid().ToString("N");
    }

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
public sealed class RemoteDownloadManager
{
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;
    private readonly ArchiveDownloadService _archiveDownloader;
    private readonly ModStore _store;
    private readonly SemaphoreSlim _concurrency = new(2, 2);
    private DispatcherQueue? _dispatcher;

    public RemoteDownloadManager(
        RemoteSiteRegistry registry,
        RemoteSiteContext context,
        ArchiveDownloadService archiveDownloader,
        ModStore store)
    {
        _registry = registry;
        _context = context;
        _archiveDownloader = archiveDownloader;
        _store = store;
    }

    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    public event EventHandler<ModEntry>? EntryUpdated;

    /// <summary>Raised for a link that cannot be downloaded, so the shell can explain rather than ignore it.</summary>
    public event EventHandler<RemoteLink>? LinkRejected;

    public void AttachDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Resolves everything a link can tell us without downloading anything, so the user can review a
    /// pre-filled record and be warned about a missing local game before any bytes move.
    /// </summary>
    public async Task<RemoteDownloadPreview> PreviewAsync(RemoteLink link, CancellationToken cancellationToken = default)
    {
        if (!link.IsDownloadable)
            throw new RemoteSiteException(RemoteErrorKind.NotFound, link.UnsupportedReason ?? "This link cannot be downloaded.", link.SiteId);

        if (!_registry.TryGet(link.SiteId, out var provider))
            throw new RemoteSiteException(RemoteErrorKind.Unknown, $"No connector is registered for site '{link.SiteId}'.", link.SiteId);

        var (credential, account) = await _context.AuthenticateAsync(provider, cancellationToken);
        var resolved = await ResolvePrimaryFileAsync(provider, link, credential, cancellationToken);
        var reference = resolved.ToRef(provider.BuildModPageUrl(resolved.ToRef()));

        var mod = await provider.GetModAsync(reference, credential, cancellationToken);
        var file = await provider.GetFileAsync(reference, credential, cancellationToken);

        return new RemoteDownloadPreview(resolved, provider.DisplayName, mod, file, account);
    }

    public DownloadJob? Enqueue(RemoteLink link)
    {
        if (!link.IsDownloadable)
        {
            Post(() => LinkRejected?.Invoke(this, link));
            return null;
        }

        var job = new DownloadJob(link, link.SiteId);
        Post(() => Jobs.Add(job));
        _ = RunAsync(job);
        return job;
    }

    public DownloadJob? Retry(DownloadJob job) => job.CanRetry ? Enqueue(job.Link) : null;

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
        var acquired = false;
        try
        {
            await _concurrency.WaitAsync(job.Cancellation.Token);
            acquired = true;

            if (!_registry.TryGet(job.SiteId, out var provider))
                throw new RemoteSiteException(RemoteErrorKind.Unknown, $"No connector is registered for site '{job.SiteId}'.", job.SiteId);

            Set(job, () => job.State = DownloadJobState.Resolving);

            var (credential, account) = await _context.AuthenticateAsync(provider, job.Cancellation.Token);
            var link = await ResolvePrimaryFileAsync(provider, job.Link, credential, job.Cancellation.Token);

            var reference = link.ToRef(provider.BuildModPageUrl(link.ToRef()));
            var mod = await provider.GetModAsync(reference, credential, job.Cancellation.Token);
            var file = await provider.GetFileAsync(reference, credential, job.Cancellation.Token);
            var sources = await provider.GetDownloadSourcesAsync(link, account, credential, job.Cancellation.Token);

            Set(job, () =>
            {
                job.Name = file.DisplayName ?? file.FileName;
                job.Subtitle = $"{mod.Name} · {provider.DisplayName}";
                job.TotalBytes = file.SizeInBytes;
            });

            var entry = await BuildEntryAsync(provider, mod, file, reference);
            job.Entry = entry;

            var destination = _store.GetArchivePath(entry.Id, file.FileName);
            EnsureDiskSpace(destination, file.SizeInBytes);

            Set(job, () => job.State = DownloadJobState.Downloading);
            var progress = new Progress<long>(bytes => Set(job, () =>
            {
                job.BytesDownloaded = bytes;
                entry.DownloadProgress = job.Progress;
            }));

            await _archiveDownloader.DownloadFromMirrorsAsync(
                sources.OrderBy(source => source.Ordinal).Select(source => source.Uri).ToList(),
                destination + ".part",
                destination,
                progress,
                job.Cancellation.Token);

            if (!string.IsNullOrWhiteSpace(file.Md5))
            {
                Set(job, () => job.State = DownloadJobState.Verifying);
                var actual = await ComputeMd5Async(destination, job.Cancellation.Token);
                if (!string.Equals(actual, file.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(destination);
                    throw new InvalidDataException("The downloaded archive did not match the checksum published by the site.");
                }
            }

            await _store.AttachDownloadedArchiveAsync(entry, destination);
            entry.Status = "Available";
            await _store.UpsertAsync(entry);

            Set(job, () =>
            {
                job.BytesDownloaded = file.SizeInBytes ?? job.BytesDownloaded;
                job.State = DownloadJobState.Completed;
                EntryUpdated?.Invoke(this, entry);
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
        finally
        {
            if (acquired)
                _concurrency.Release();
        }
    }

    /// <summary>A mod-only link names no file, so the site's primary file is chosen for the user.</summary>
    private static async Task<RemoteLink> ResolvePrimaryFileAsync(
        IRemoteSiteProvider provider, RemoteLink link, RemoteCredential credential, CancellationToken cancellationToken)
    {
        if (link.Kind != RemoteLinkKind.Mod)
            return link;

        var listing = await provider.GetModFilesAsync(new RemoteRef(link.SiteId, link.GameKey!, link.ModKey!), credential, cancellationToken);
        var primary = listing.Files.FirstOrDefault(file => file.IsPrimary)
                      ?? listing.Files.FirstOrDefault(file => file.Category == RemoteFileCategory.Main)
                      ?? throw new RemoteSiteException(RemoteErrorKind.NotFound, "This mod has no main file to download.", provider.SiteId);

        return RemoteLink.ForModFile(link.SiteId, link.GameKey!, link.ModKey!, primary.FileKey);
    }

    /// <summary>
    /// Finds the entry this download belongs to, or creates one, and fills in everything the site
    /// told us. Matching by reference first and content hash second keeps a re-download from
    /// duplicating a mod the user already has.
    /// </summary>
    private async Task<ModEntry> BuildEntryAsync(
        IRemoteSiteProvider provider, RemoteModMetadata mod, RemoteFileMetadata file, RemoteRef reference)
    {
        var fileRef = reference with { FileKey = file.FileKey };
        var entries = await _store.LoadAsync();

        var entry = entries.FirstOrDefault(item => item.Remote?.IsSameFile(fileRef) == true)
                    ?? entries.FirstOrDefault(item => !string.IsNullOrWhiteSpace(file.Md5) &&
                                                      string.Equals(item.Md5, file.Md5, StringComparison.OrdinalIgnoreCase))
                    ?? new ModEntry { Id = Guid.NewGuid().ToString("N") };

        // An existing entry for the same mod but a different file is the version this one supersedes.
        foreach (var superseded in entries.Where(item =>
                     item.Remote?.IsSameMod(fileRef) == true && item.Remote?.IsSameFile(fileRef) != true))
            superseded.HasUpdate = true;

        entry.Remote = fileRef;
        entry.Name = mod.Name;
        entry.Game = mod.Ref.GameKey;
        entry.Version = file.Version ?? mod.Version ?? string.Empty;
        entry.Source = provider.DisplayName;
        entry.Status = "Downloading";
        entry.FileName = file.FileName;
        entry.Md5 = file.Md5;
        entry.FileSize = file.SizeInBytes;
        entry.Author = mod.Author;
        entry.Description = mod.Summary ?? mod.DescriptionHtml;
        entry.CategoryName = mod.CategoryName;
        entry.Website = mod.Ref.PageUrl;
        entry.UploadedAt = file.UploadedAt;
        entry.RemoteFileCategory = file.Category;
        entry.IsPrimaryFile = file.IsPrimary;
        entry.ChangelogText = file.ChangelogText;
        entry.RemoteUpdatedAt = mod.UpdatedAt;
        entry.HasUpdate = false;

        return await _store.UpsertAsync(entry);
    }

    private static void EnsureDiskSpace(string destination, long? required)
    {
        if (required is not > 0)
            return;
        var root = Path.GetPathRoot(destination);
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < required)
            throw new IOException("There is not enough free disk space for this archive.");
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
