using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>Persists <see cref="ModInstallation"/> records; mirrors <see cref="ModStore"/>'s atomic-save/backup pattern.</summary>
public sealed class ModInstallationStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int CurrentSchemaVersion = 2;
    private readonly string _rootPath;
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public ModInstallationStore(string? rootPath = null)
    {
        _rootPath = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _databasePath = Path.Combine(_rootPath, "mod-installations.json");
        _backupPath = Path.Combine(_rootPath, "mod-installations.json.bak");
        _temporaryPath = Path.Combine(_rootPath, "mod-installations.json.tmp");
    }

    public async Task<IReadOnlyList<ModInstallation>> LoadAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            if (!File.Exists(_databasePath))
                return Array.Empty<ModInstallation>();

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

    public async Task SaveAsync(IEnumerable<ModInstallation> entries)
    {
        var snapshot = entries.ToList();
        await _databaseLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_rootPath);
            await using (var stream = new FileStream(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new DatabaseDocument(CurrentSchemaVersion, snapshot), JsonOptions);
                await stream.FlushAsync();
            }

            AtomicFile.Publish(_temporaryPath, _databasePath, _backupPath);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task AddAsync(ModInstallation installation)
    {
        var entries = (await LoadAsync()).ToList();
        entries.Add(installation);
        await SaveAsync(entries);
    }

    private static async Task<IReadOnlyList<ModInstallation>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<DatabaseDocument>(stream, JsonOptions)
            ?? throw new JsonException("The mod installations file is empty or malformed.");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException($"The mod installations file is schema {document.SchemaVersion}, but this build requires schema {CurrentSchemaVersion}.");
        return document.Installations;
    }

    private sealed record DatabaseDocument(int SchemaVersion, List<ModInstallation> Installations);

    public void Dispose() => _databaseLock.Dispose();
}
