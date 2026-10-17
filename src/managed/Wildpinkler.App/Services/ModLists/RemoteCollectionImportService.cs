using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// The outcome of importing a remote collection into the local mod-list catalog. The manifest is
/// already saved; <see cref="BuildCoordinator"/> and <see cref="BuildStore"/> are left to the caller
/// so the UI can decide when to start the build.
/// </summary>
public sealed record RemoteCollectionImportResult(
    ModListManifest Manifest,
    ModListGradeEvaluation Grade,
    ModListImportResult Import);

/// <summary>
/// Imports a site collection (a revision-pinned, ordered list of mod files, possibly with nested
/// sub-collections) as a native Wildpinkler mod list. It resolves the collection through the site
/// connector, flattens nested entries in order with dedup by file identity, translates the result
/// into a <see cref="ModListManifest"/>, and stores it in the mod-list catalog. It never downloads
/// archive bytes - acquisition happens later through the normal mod-list build pipeline.
/// </summary>
public sealed class RemoteCollectionImportService
{
    private readonly Func<RemoteCollectionRef, CancellationToken, Task<RemoteCollection>> _fetchCollection;
    private readonly Func<string, string, CancellationToken, Task<RemoteGameMapping?>> _mapGame;
    private readonly ModListCatalogStore _catalog;
    private readonly ModListManifestSerializer _serializer;

    private const int MaxCollectionDepth = 16;

    public RemoteCollectionImportService(
        RemoteSiteRegistry registry,
        RemoteSiteContext context,
        RemoteGameMapper gameMapper,
        ModListCatalogStore catalog,
        ModListManifestSerializer serializer)
        : this(
            FetchCollectionAsync,
            MapGameAsync,
            catalog,
            serializer,
            registry,
            context,
            gameMapper)
    {
    }

    // Test seam: lets the fetch and game-mapping steps be substituted with in-memory fakes.
    internal RemoteCollectionImportService(
        Func<RemoteCollectionRef, CancellationToken, Task<RemoteCollection>> fetchCollection,
        Func<string, string, CancellationToken, Task<RemoteGameMapping?>> mapGame,
        ModListCatalogStore catalog,
        ModListManifestSerializer serializer,
        RemoteSiteRegistry? registry = null,
        RemoteSiteContext? context = null,
        RemoteGameMapper? gameMapper = null)
    {
        _fetchCollection = fetchCollection;
        _mapGame = mapGame;
        _catalog = catalog;
        _serializer = serializer;
        _registry = registry;
        _context = context;
        _gameMapper = gameMapper;
    }

    private readonly RemoteSiteRegistry? _registry;
    private readonly RemoteSiteContext? _context;
    private readonly RemoteGameMapper? _gameMapper;

    private static async Task<RemoteCollection> FetchCollectionAsync(
        RemoteCollectionRef reference, CancellationToken cancellationToken)
    {
        var registry = AppHost.Get<RemoteSiteRegistry>();
        if (!registry.TryGet(reference.SiteId, out var provider) ||
            !provider.Capabilities.SupportsCollections ||
            provider is not IRemoteCollectionProvider collectionProvider)
            throw new RemoteSiteException(
                RemoteErrorKind.Unknown,
                $"Site '{reference.SiteId}' does not expose collections.", reference.SiteId);

        var context = AppHost.Get<RemoteSiteContext>();
        var credential = await context.GetCredentialAsync(provider, cancellationToken);
        return await collectionProvider.GetCollectionAsync(reference, credential, cancellationToken);
    }

    private static async Task<RemoteGameMapping?> MapGameAsync(
        string siteId, string gameKey, CancellationToken cancellationToken)
    {
        var mapper = AppHost.Get<RemoteGameMapper>();
        return await mapper.ResolveAsync(new RemoteRef(siteId, gameKey, "0"));
    }

    /// <summary>
    /// Fetches the collection (resolving nested sub-collections), maps it to the local game, and
    /// translates it into a mod list that is saved into the catalog. Re-importing the same
    /// collection key+revision is idempotent; a newer revision produces a new catalog revision.
    /// </summary>
    public async Task<RemoteCollectionImportResult> ImportAsync(
        RemoteCollectionRef reference,
        CancellationToken cancellationToken = default)
    {
        var collection = await _fetchCollection(reference, cancellationToken);
        var flattened = await FlattenAsync(reference, collection, depth: 0, cancellationToken);

        // A mod list is anchored to a concrete local game definition, so a collection whose game
        // does not map to any installed game cannot be turned into a list yet.
        var mapping = await _mapGame(reference.SiteId, reference.GameKey, cancellationToken);
        if (mapping is null)
            throw new RemoteSiteException(
                RemoteErrorKind.Unknown,
                $"No local game maps to the collection's game '{reference.GameKey}'. " +
                "Install and add that game in Wildpinkler, then open the collection link again.",
                reference.SiteId);

        // Preserve the first import time when re-importing the same revision so the resulting
        // manifest is byte-identical and the catalog treats the re-import as already present.
        var revision = collection.Revision is > 0 ? collection.Revision.Value : 1;
        var existing = await _catalog.TryFindAsync(BuildListId(reference), revision, cancellationToken);
        var importedAt = existing?.SourceCollection?.ImportedAt ?? DateTimeOffset.UtcNow;

        var manifest = Translate(reference, collection, flattened, mapping, importedAt);
        ModListManifestValidator.EnsureValid(manifest);

        var import = await _catalog.SaveAsync(manifest, replace: true, cancellationToken);
        return new RemoteCollectionImportResult(manifest, ModListGradeResolver.Evaluate(manifest), import);
    }

    /// <summary>
    /// Walks the collection tree in entry order, resolving nested sub-collections recursively and
    /// deduplicating by file identity so a file reachable from two sub-collections appears once.
    /// </summary>
    private async Task<IReadOnlyList<RemoteCollectionModFileEntry>> FlattenAsync(
        RemoteCollectionRef root,
        RemoteCollection collection,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxCollectionDepth)
            throw new RemoteSiteException(
                RemoteErrorKind.Unknown,
                $"Collection nesting exceeded the maximum depth of {MaxCollectionDepth}.", root.SiteId);

        var ordered = new List<RemoteCollectionModFileEntry>();
        var seen = new HashSet<RemoteRef>(RemoteRefComparer.Instance);
        foreach (var entry in collection.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsModFile)
            {
                var file = entry.AsModFile!;
                if (seen.Add(file.Ref))
                    ordered.Add(file);
            }
            else if (entry.AsSubCollection is { } sub)
            {
                var subRef = new RemoteCollectionRef(root.SiteId, sub.GameKey, sub.CollectionKey);
                var subCollection = await _fetchCollection(subRef, cancellationToken);
                foreach (var nested in await FlattenAsync(root, subCollection, depth + 1, cancellationToken))
                {
                    if (seen.Add(nested.Ref))
                        ordered.Add(nested);
                }
            }
        }
        return ordered;
    }

    private ModListManifest Translate(
        RemoteCollectionRef reference,
        RemoteCollection collection,
        IReadOnlyList<RemoteCollectionModFileEntry> files,
        RemoteGameMapping mapping,
        DateTimeOffset importedAt)
    {
        var definitionId = mapping.Definition.DefinitionId;
        var manifest = new ModListManifest
        {
            ListId = BuildListId(reference),
            Revision = collection.Revision is > 0 ? collection.Revision.Value : 1,
            Name = collection.Name,
            Author = collection.Author ?? string.Empty,
            Description = collection.Description ?? string.Empty,
            Game = new ModListGameRequirement
            {
                DefinitionId = definitionId,
                MinimumDefinitionVersion = mapping.Definition.DefinitionVersion
            },
            SourceCollection = new ModListCollectionSource
            {
                SiteId = reference.SiteId,
                GameKey = reference.GameKey,
                CollectionKey = reference.CollectionKey,
                Slug = reference.Slug,
                Revision = collection.Revision,
                ImportedAt = importedAt
            }
        };

        var order = 0;
        foreach (var file in files)
        {
            order++;
            manifest.Content.Add(new ModListModEntry
            {
                EntryId = BuildEntryId(file.Ref, order),
                Order = order,
                Name = file.Name,
                IsEnabled = true,
                Version = file.Version,
                Source = file.Ref,
                AcquisitionInstructions = string.Empty,
                Archive = new ModListArchiveRequirement
                {
                    FileName = SafeFileName(file.FileName),
                    Sha256 = string.Empty,
                    Md5 = file.Md5,
                    SizeInBytes = file.SizeInBytes,
                    Sha256Pending = true
                },
                Installation = new GuidedInstallationRecipe
                {
                    Instructions = "Complete this mod's installation choices after the archive is downloaded."
                }
            });
        }

        return manifest;
    }

    // The list id must be a safe portable identifier and stay stable across revisions of the same
    // collection so a re-import at a newer revision replaces the same list.
    private static string BuildListId(RemoteCollectionRef reference)
    {
        var raw = $"{reference.SiteId}:{reference.GameKey}:{reference.CollectionKey}".ToLowerInvariant();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
        return $"collection-{digest[..24]}";
    }

    private static string BuildEntryId(RemoteRef reference, int order)
    {
        var raw = $"{reference.SiteId}:{reference.GameKey}:{reference.ModKey}:{reference.FileKey}".ToLowerInvariant();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
        return $"mod-{digest[..24]}";
    }

    private static string SafeFileName(string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(name) ? $"{System.IO.Path.GetRandomFileName()}.zip" : name;
    }
}

/// <summary>Case-insensitive equality on the file-level identity of a <see cref="RemoteRef"/>.</summary>
internal sealed class RemoteRefComparer : IEqualityComparer<RemoteRef>
{
    public static readonly RemoteRefComparer Instance = new();

    public bool Equals(RemoteRef? x, RemoteRef? y) =>
        x is not null && y is not null && x.IsSameFile(y);

    public int GetHashCode(RemoteRef obj)
    {
        unchecked
        {
            var hash = (obj.SiteId?.ToLowerInvariant() ?? string.Empty).GetHashCode(StringComparison.Ordinal);
            hash = (hash * 397) ^ (obj.GameKey?.ToLowerInvariant() ?? string.Empty).GetHashCode(StringComparison.Ordinal);
            hash = (hash * 397) ^ (obj.ModKey ?? string.Empty).GetHashCode(StringComparison.Ordinal);
            hash = (hash * 397) ^ (obj.FileKey ?? string.Empty).GetHashCode(StringComparison.Ordinal);
            return hash;
        }
    }
}
