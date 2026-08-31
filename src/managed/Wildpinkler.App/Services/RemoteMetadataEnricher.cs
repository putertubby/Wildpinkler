using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed record RemoteIdentification(string SiteId, string SiteName, RemoteModMetadata Mod, RemoteFileMetadata File);

/// <summary>
/// Identifies a locally added archive by content hash so it can carry the same metadata a protocol
/// download would have filled in. Always user-initiated: the acceptable use policy forbids bulk
/// lookups, and this costs one request per site per archive.
/// </summary>
public sealed class RemoteMetadataEnricher
{
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;

    public RemoteMetadataEnricher(RemoteSiteRegistry registry, RemoteSiteContext context)
    {
        _registry = registry;
        _context = context;
    }

    public async Task<RemoteIdentification?> IdentifyAsync(ModEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.ArchivePath) || !File.Exists(entry.ArchivePath))
            throw new InvalidOperationException("This mod has no local archive to identify.");

        var md5 = entry.Md5 ?? await ComputeMd5Async(entry.ArchivePath, cancellationToken);

        foreach (var provider in _registry.Providers.Where(item => item.Capabilities.SupportsHashLookup))
        {
            var credential = await _context.GetCredentialAsync(provider, cancellationToken);
            if (!credential.IsUsable)
                continue;

            // A game key is required by the lookup, so an already-known game narrows it; otherwise
            // the site's own "all games" key is used.
            var gameKey = entry.Remote?.GameKey ?? "all";
            var matches = await provider.FindByHashAsync(gameKey, md5, credential, cancellationToken);

            var match = matches.FirstOrDefault(item =>
                entry.FileSize is null || item.File.SizeInBytes is null || item.File.SizeInBytes == entry.FileSize);
            if (match is not null)
                return new RemoteIdentification(provider.SiteId, provider.DisplayName, match.Mod, match.File);
        }

        return null;
    }

    /// <summary>Applies an identification to the entry. Never called without the user accepting it.</summary>
    public static void Apply(ModEntry entry, RemoteIdentification identification)
    {
        entry.Remote = identification.Mod.Ref with { FileKey = identification.File.FileKey };
        entry.Name = identification.Mod.Name;
        entry.Game = identification.Mod.Ref.GameKey;
        entry.Source = identification.SiteName;
        entry.Author = identification.Mod.Author;
        entry.Description = identification.Mod.Summary ?? identification.Mod.DescriptionHtml;
        entry.CategoryName = identification.Mod.CategoryName;
        entry.Website = identification.Mod.Ref.PageUrl;
        entry.Version = identification.File.Version ?? identification.Mod.Version ?? entry.Version;
        entry.Md5 = identification.File.Md5 ?? entry.Md5;
        entry.FileSize = identification.File.SizeInBytes ?? entry.FileSize;
        entry.UploadedAt = identification.File.UploadedAt;
        entry.RemoteFileCategory = identification.File.Category;
        entry.IsPrimaryFile = identification.File.IsPrimary;
        entry.ChangelogText = identification.File.ChangelogText;
        entry.RemoteUpdatedAt = identification.Mod.UpdatedAt;
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(await md5.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
