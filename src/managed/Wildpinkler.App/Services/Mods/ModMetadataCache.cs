using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Persists <see cref="ModMetadataCacheEntry"/> snapshots keyed by remote identity. This is the
/// locally stored, refreshable record of remote metadata that drives the remote-first policy:
/// a fresh or stale remote entry always wins over an archive entry, and an archive entry is kept
/// only for mods with no remote identity.
///
/// Mirrors <see cref="ModInstallationStore"/>'s atomic-save/backup pattern. Entries are stored
/// flat in a dictionary keyed by a normalized identity string so lookups are O(1).
/// </summary>
public sealed class ModMetadataCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // Schema 1: the initial layout. There is no prior schema to migrate from.
    private const int CurrentSchemaVersion = 1;
    private readonly string _rootPath;
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public ModMetadataCache(string? rootPath = null)
    {
        _rootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _databasePath = Path.Combine(_rootPath, "mod-metadata-cache.json");
        _backupPath = Path.Combine(_rootPath, "mod-metadata-cache.json.bak");
        _temporaryPath = Path.Combine(_rootPath, "mod-metadata-cache.json.tmp");
    }

    /// <summary>
    /// Loads every cache entry. A missing file yields an empty list; a corrupt primary file falls
    /// back to the backup, and both corrupt yields empty. Callers needing a keyed view can group by
    /// <see cref="IdentityKey"/>.
    /// </summary>
    public async Task<IReadOnlyList<ModMetadataCacheEntry>> LoadAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            if (!File.Exists(_databasePath))
                return Array.Empty<ModMetadataCacheEntry>();

            try
            {
                return (await ReadEntriesAsync(_databasePath)).Values.ToList();
            }
            catch (JsonException) when (File.Exists(_backupPath))
            {
                return (await ReadEntriesAsync(_backupPath)).Values.ToList();
            }
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>Persists the full set of entries (callers pass the reconciled collection).</summary>
    public async Task SaveAsync(IEnumerable<ModMetadataCacheEntry> entries)
    {
        var snapshot = entries.ToList();
        await _databaseLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_rootPath);
            var byKey = new Dictionary<string, ModMetadataCacheEntry>(StringComparer.Ordinal);
            foreach (var entry in snapshot)
                byKey[IdentityKey(entry.Ref)] = entry;
            var document = new DatabaseDocument(CurrentSchemaVersion, byKey);
            await using (var stream = new FileStream(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
                await stream.FlushAsync();
            }

            AtomicFile.Publish(_temporaryPath, _databasePath, _backupPath);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Adds or replaces the entry for an identity and persists the whole cache. Replacing a
    /// stale archive entry with a fresh remote one (a "link to site mod") is done by simply
    /// upserting the remote entry; the caller decides when a fetch is worth doing.
    /// </summary>
    public async Task UpsertAsync(ModMetadataCacheEntry entry)
    {
        var entries = (await LoadAsync()).ToList();
        var key = IdentityKey(entry.Ref);
        var existingIndex = entries.FindIndex(candidate => string.Equals(IdentityKey(candidate.Ref), key, StringComparison.Ordinal));
        if (existingIndex >= 0)
            entries[existingIndex] = entry;
        else
            entries.Add(entry);
        await SaveAsync(entries);
    }

    /// <summary>Removes the entry for an identity and persists the whole cache.</summary>
    public async Task RemoveAsync(RemoteRef remoteRef)
    {
        var key = IdentityKey(remoteRef);
        var entries = (await LoadAsync()).ToList();
        entries.RemoveAll(candidate => string.Equals(IdentityKey(candidate.Ref), key, StringComparison.Ordinal));
        await SaveAsync(entries);
    }

    /// <summary>
    /// The normalized cache key for a remote identity: site/game/mod in a canonical (lower-site,
    /// original-case game/mod) form, so lookups are stable regardless of how the caller cased the
    /// site id.
    /// </summary>
    public static string IdentityKey(RemoteRef remoteRef)
        => $"{remoteRef.SiteId.ToLowerInvariant()}::{remoteRef.GameKey}::{remoteRef.ModKey}";

    private static async Task<Dictionary<string, ModMetadataCacheEntry>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<DatabaseDocument>(stream, JsonOptions)
            ?? throw new JsonException("The mod metadata cache file is empty or malformed.");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException($"The mod metadata cache file is schema {document.SchemaVersion}, but this build supports schema {CurrentSchemaVersion}.");
        return document.Entries;
    }

    private sealed record DatabaseDocument(int SchemaVersion, Dictionary<string, ModMetadataCacheEntry> Entries);

    public void Dispose() => _databaseLock.Dispose();
}
