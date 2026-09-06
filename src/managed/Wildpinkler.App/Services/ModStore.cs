using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class ModStore
{
    // Case-insensitive on read because this file is hand-edited between schema changes.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    // Schema 5 adds the archive SHA-256 used by reproducible mod-list manifests.
    private const int CurrentSchemaVersion = 5;
    private readonly string _rootPath;
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly string _archivePath;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private readonly IArchiveInspector _archiveInspector = new ArchiveInspector();

    public ModStore(string? rootPath = null)
    {
        _rootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _databasePath = Path.Combine(_rootPath, "mods.json");
        _backupPath = Path.Combine(_rootPath, "mods.json.bak");
        _temporaryPath = Path.Combine(_rootPath, "mods.json.tmp");
        _archivePath = Path.Combine(_rootPath, "archives");
    }

    public async Task<IReadOnlyList<ModEntry>> LoadAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            return await ReadCurrentAsync();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<ModEntry> entries)
    {
        var snapshot = entries.ToList();
        await _databaseLock.WaitAsync();
        try
        {
            await WriteAsync(snapshot);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private async Task<IReadOnlyList<ModEntry>> ReadCurrentAsync()
    {
        if (!File.Exists(_databasePath))
            return Array.Empty<ModEntry>();

        try
        {
            return await ReadEntriesAsync(_databasePath);
        }
        catch (JsonException) when (File.Exists(_backupPath))
        {
            return await ReadEntriesAsync(_backupPath);
        }
    }

    private async Task WriteAsync(List<ModEntry> snapshot)
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
    }

    /// <summary>
    /// Read-modify-write of a single entry under the database lock, so concurrent jobs cannot
    /// overwrite each other by each loading, mutating and saving the whole list.
    /// </summary>
    public async Task<ModEntry> UpsertAsync(ModEntry entry)
    {
        await _databaseLock.WaitAsync();
        try
        {
            var entries = (await ReadCurrentAsync()).ToList();
            var index = entries.FindIndex(item => item.Id == entry.Id);
            if (index >= 0)
                entries[index] = entry;
            else
                entries.Add(entry);

            await WriteAsync(entries);
            return entry;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<ModEntry> AddArchiveAsync(ModEntry entry, string sourceArchive)
    {
        if (!File.Exists(sourceArchive))
            throw new FileNotFoundException("The selected archive does not exist.", sourceArchive);

        Directory.CreateDirectory(_archivePath);
        var extension = Path.GetExtension(sourceArchive);
        var destination = Path.Combine(_archivePath, $"{entry.Id}{extension}");
        await using (var source = File.OpenRead(sourceArchive))
        await using (var target = File.Create(destination))
            await source.CopyToAsync(target);

        entry.ArchivePath = destination;
        entry.Sha256 = await ComputeSha256Async(destination);
        SetFomodState(entry, destination);
        return entry;
    }

    public Task DeleteArchiveAsync(ModEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ArchivePath) && File.Exists(entry.ArchivePath))
            File.Delete(entry.ArchivePath);
        return Task.CompletedTask;
    }

    public string GetArchivePath(string entryId, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not ".zip" and not ".7z" and not ".rar" and not ".fomod")
            extension = ".archive";
        return Path.Combine(_archivePath, $"{entryId}{extension}");
    }

    public async Task<ModEntry> AttachDownloadedArchiveAsync(ModEntry entry, string archivePath)
    {
        entry.ArchivePath = archivePath;
        entry.Sha256 = await ComputeSha256Async(archivePath);
        SetFomodState(entry, archivePath);
        return entry;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private void SetFomodState(ModEntry entry, string path)
    {
        entry.FomodState = _archiveInspector.DetectFomod(path);
        if (entry.FomodState is FomodState.Yes or FomodState.No)
            entry.HasFomod = entry.FomodState == FomodState.Yes;
    }

    private static async Task<IReadOnlyList<ModEntry>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);

        var database = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Deserialize<DatabaseDocument>(JsonOptions)
            : throw new JsonException("The mods database is not a recognised document.");

        if (database is null || database.SchemaVersion != CurrentSchemaVersion)
            throw new ModStoreSchemaException(database?.SchemaVersion, CurrentSchemaVersion);

        return database.Mods ?? new List<ModEntry>();
    }

    private sealed record DatabaseDocument(int SchemaVersion, List<ModEntry> Mods);
}

/// <summary>
/// Raised when the on-disk mods database is not the schema this build understands. Deliberately not
/// a <see cref="JsonException"/> so the corrupt-file backup fallback cannot silently swallow it.
/// </summary>
public sealed class ModStoreSchemaException : Exception
{
    public ModStoreSchemaException(int? found, int expected)
        : base($"The mods database is schema {found?.ToString() ?? "unknown"}, but this build requires schema {expected}. Convert the file before starting Wildpinkler.")
    {
        Found = found;
        Expected = expected;
    }

    public int? Found { get; }
    public int Expected { get; }
}
