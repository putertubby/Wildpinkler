using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public enum RemoteAcquisitionPhase
{
    Resolving,
    Downloading,
    Verifying
}

public sealed record RemoteAcquisitionProgress(
    RemoteAcquisitionPhase Phase,
    long BytesDownloaded = 0,
    long? TotalBytes = null,
    string? FileName = null,
    string? ModName = null,
    string? SiteName = null,
    ModEntry? Entry = null);

public sealed record RemoteArchiveAcquisitionResult(
    RemoteLink Link,
    ModEntry Entry,
    RemoteModMetadata Mod,
    RemoteFileMetadata File,
    RemoteAccount Account);

public sealed class RemoteArchiveAcquisitionService : IDisposable
{
    private readonly RemoteSiteRegistry _registry;
    private readonly Func<IRemoteSiteProvider, CancellationToken, Task<(RemoteCredential Credential, RemoteAccount Account)>> _authenticate;
    private readonly ArchiveDownloadService _archiveDownloader;
    private readonly ModStore _store;
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    public RemoteArchiveAcquisitionService(
        RemoteSiteRegistry registry,
        RemoteSiteContext context,
        ArchiveDownloadService archiveDownloader,
        ModStore store)
    {
        _registry = registry;
        _authenticate = context.AuthenticateAsync;
        _archiveDownloader = archiveDownloader;
        _store = store;
    }

    internal RemoteArchiveAcquisitionService(
        RemoteSiteRegistry registry,
        Func<IRemoteSiteProvider, CancellationToken, Task<(RemoteCredential Credential, RemoteAccount Account)>> authenticate,
        ArchiveDownloadService archiveDownloader,
        ModStore store)
    {
        _registry = registry;
        _authenticate = authenticate;
        _archiveDownloader = archiveDownloader;
        _store = store;
    }

    public async Task<RemoteDownloadPreview> PreviewAsync(RemoteLink link, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(link);
        var (credential, account) = await _authenticate(provider, cancellationToken);
        var resolved = await ResolveExactFileAsync(provider, link, credential, cancellationToken);
        var reference = resolved.ToRef(provider.BuildModPageUrl(resolved.ToRef()));
        var mod = await provider.GetModAsync(reference, credential, cancellationToken);
        var file = await provider.GetFileAsync(reference, credential, cancellationToken);
        return new RemoteDownloadPreview(resolved, provider.DisplayName, mod, file, account);
    }

    // MD5 is not a security control here: it is the checksum the remote site publishes for the file.
    [SuppressMessage("Security", "CA5351:Do not use broken cryptographic algorithms", Justification = "Fixed by the remote site's published checksum format; transport integrity is provided by TLS.")]
    public async Task<RemoteArchiveAcquisitionResult> AcquireAsync(
        RemoteLink link,
        string? expectedSha256 = null,
        IReadOnlyList<string>? confirmedGameIds = null,
        IProgress<RemoteAcquisitionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            var provider = GetProvider(link);
            progress?.Report(new RemoteAcquisitionProgress(RemoteAcquisitionPhase.Resolving));
            var (credential, account) = await _authenticate(provider, cancellationToken);
            var resolved = await ResolveExactFileAsync(provider, link, credential, cancellationToken);
            var reference = resolved.ToRef(provider.BuildModPageUrl(resolved.ToRef()));
            var mod = await provider.GetModAsync(reference, credential, cancellationToken);
            var file = await provider.GetFileAsync(reference, credential, cancellationToken);
            var sources = await provider.GetDownloadSourcesAsync(resolved, account, credential, cancellationToken);
            var entry = await BuildEntryAsync(provider, mod, file, reference, confirmedGameIds);
            var destination = _store.GetArchivePath(entry.Id, file.FileName);
            EnsureDiskSpace(destination, file.SizeInBytes);

            progress?.Report(new RemoteAcquisitionProgress(
                RemoteAcquisitionPhase.Downloading, TotalBytes: file.SizeInBytes,
                FileName: file.DisplayName ?? file.FileName, ModName: mod.Name, SiteName: provider.DisplayName, Entry: entry));
            var byteProgress = new Progress<long>(bytes => progress?.Report(new RemoteAcquisitionProgress(
                RemoteAcquisitionPhase.Downloading, bytes, file.SizeInBytes,
                file.DisplayName ?? file.FileName, mod.Name, provider.DisplayName, entry)));
            await _archiveDownloader.DownloadFromMirrorsAsync(
                sources.OrderBy(source => source.Ordinal).Select(source => source.Uri).ToList(),
                destination + ".part", destination, byteProgress, cancellationToken);

            progress?.Report(new RemoteAcquisitionProgress(
                RemoteAcquisitionPhase.Verifying, file.SizeInBytes ?? 0, file.SizeInBytes,
                file.DisplayName ?? file.FileName, mod.Name, provider.DisplayName, entry));
            if (!string.IsNullOrWhiteSpace(file.Md5))
            {
                var actualMd5 = await ComputeHashAsync(destination, MD5.Create(), cancellationToken);
                if (!string.Equals(actualMd5, file.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(destination);
                    throw new InvalidDataException("The downloaded archive did not match the checksum published by the site.");
                }
            }

            var actualSha256 = await ComputeHashAsync(destination, SHA256.Create(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                throw new InvalidDataException("The downloaded archive did not match the SHA-256 required by the mod list.");
            }

            await _store.AttachDownloadedArchiveAsync(entry, destination);
            entry.Status = "Available";
            await _store.UpsertAsync(entry);
            return new RemoteArchiveAcquisitionResult(resolved, entry, mod, file, account);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private IRemoteSiteProvider GetProvider(RemoteLink link)
    {
        if (!link.IsDownloadable)
            throw new RemoteSiteException(RemoteErrorKind.NotFound, link.UnsupportedReason ?? "This link cannot be downloaded.", link.SiteId);
        if (!_registry.TryGet(link.SiteId, out var provider))
            throw new RemoteSiteException(RemoteErrorKind.Unknown, $"No connector is registered for site '{link.SiteId}'.", link.SiteId);
        return provider;
    }

    private static async Task<RemoteLink> ResolveExactFileAsync(
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

    private async Task<ModEntry> BuildEntryAsync(
        IRemoteSiteProvider provider, RemoteModMetadata mod, RemoteFileMetadata file, RemoteRef reference,
        IReadOnlyList<string>? confirmedGameIds)
    {
        var fileRef = reference with { FileKey = file.FileKey };
        var entries = await _store.LoadAsync();
        var entry = entries.FirstOrDefault(item => item.Remote?.IsSameFile(fileRef) == true)
                    ?? entries.FirstOrDefault(item => !string.IsNullOrWhiteSpace(file.Md5) &&
                                                      string.Equals(item.Md5, file.Md5, StringComparison.OrdinalIgnoreCase))
                    ?? new ModEntry { Id = Guid.NewGuid().ToString("N") };
        foreach (var superseded in entries.Where(item =>
                     item.Remote?.IsSameMod(fileRef) == true && item.Remote?.IsSameFile(fileRef) != true))
            superseded.HasUpdate = true;

        entry.Remote = fileRef;
        entry.Name = mod.Name;
        // Only what the user confirmed in the download dialog is applied; an empty list means "all games", not "unknown".
        if (confirmedGameIds is not null)
            entry.GameIds = confirmedGameIds.ToList();
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

    private static async Task<string> ComputeHashAsync(string path, HashAlgorithm algorithm, CancellationToken cancellationToken)
    {
        using (algorithm)
        await using (var stream = File.OpenRead(path))
            return Convert.ToHexString(await algorithm.ComputeHashAsync(stream, cancellationToken));
    }

    public void Dispose() => _concurrency.Dispose();
}
