using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int CurrentSchemaVersion = 2;
    private readonly string _rootPath;
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly string _legacyStubPath;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private readonly HashSet<string> _deletedIds = new(StringComparer.Ordinal);
    private long _revision;

    public ProfileStore(string? rootPath = null)
    {
        _rootPath = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _databasePath = Path.Combine(_rootPath, "profiles.json");
        _backupPath = Path.Combine(_rootPath, "profiles.json.bak");
        _temporaryPath = Path.Combine(_rootPath, "profiles.json.tmp");
        _legacyStubPath = Path.Combine(_rootPath, "profiles-stub.json");
    }

    /// <summary>Bumped on every save so a cached page can tell its in-memory list went stale.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Raised after a save, on whichever thread performed it.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Tombstones deleted profiles for the rest of the session, so a page still holding them in a
    /// cached list cannot write them back on its next save.
    /// </summary>
    public void MarkDeleted(IEnumerable<string> profileIds)
    {
        lock (_deletedIds)
        {
            foreach (var id in profileIds)
                _deletedIds.Add(id);
        }
    }

    /// <summary>Where profile directories are created.</summary>
    public string ProfilesRoot => Path.Combine(_rootPath, "profiles");

    public async Task<IReadOnlyList<Profile>> LoadAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            DiscardLegacyStub();

            if (!File.Exists(_databasePath))
                return Array.Empty<Profile>();

            try
            {
                return await ReadEntriesAsync(_databasePath);
            }
            catch (JsonException) when (File.Exists(_backupPath))
            {
                return await ReadEntriesAsync(_backupPath);
            }
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<Profile> profiles)
    {
        List<Profile> snapshot;
        lock (_deletedIds)
            snapshot = profiles.Where(profile => !_deletedIds.Contains(profile.Id)).ToList();
        await _databaseLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_rootPath);
            await using (var stream = new FileStream(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new DatabaseDocument(CurrentSchemaVersion, snapshot), JsonOptions);
                await stream.FlushAsync();
            }

            if (File.Exists(_databasePath))
                File.Replace(_temporaryPath, _databasePath, _backupPath, true);
            else
                File.Move(_temporaryPath, _databasePath, true);

            Interlocked.Increment(ref _revision);
        }
        finally
        {
            _databaseLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The stub only ever held id/name/game placeholders, so it is dropped rather than migrated.
    private void DiscardLegacyStub()
    {
        if (File.Exists(_legacyStubPath))
            File.Delete(_legacyStubPath);
    }

    private static async Task<IReadOnlyList<Profile>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var database = document.RootElement.Deserialize<DatabaseDocument>(JsonOptions);
        if (database is null || database.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException($"The profiles database is schema {database?.SchemaVersion.ToString() ?? "unknown"}, but this build requires schema {CurrentSchemaVersion}.");

        var profiles = database.Profiles ?? new List<Profile>();
        foreach (var profile in profiles)
            profile.EnforcePinnedOrder();

        return profiles;
    }

    private sealed record DatabaseDocument(int SchemaVersion, List<Profile> Profiles);
}
