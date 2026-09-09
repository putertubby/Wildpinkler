using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class GameStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int CurrentSchemaVersion = 3; // 3 removes per-entry executable paths; schema-1/2 files still load.
    private readonly string _rootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public GameStore()
    {
        _databasePath = Path.Combine(_rootPath, "games.json");
        _backupPath = Path.Combine(_rootPath, "games.json.bak");
        _temporaryPath = Path.Combine(_rootPath, "games.json.tmp");
    }

    public async Task<IReadOnlyList<GameEntry>> LoadAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            if (!File.Exists(_databasePath))
                return Array.Empty<GameEntry>();

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

    public async Task SaveAsync(IEnumerable<GameEntry> entries)
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

    private static async Task<IReadOnlyList<GameEntry>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document.RootElement.Deserialize<List<GameEntry>>(JsonOptions) ?? new List<GameEntry>();

        var database = document.RootElement.Deserialize<DatabaseDocument>(JsonOptions);
        if (database is null || database.SchemaVersion > CurrentSchemaVersion)
            throw new JsonException("The games database schema is not supported.");

        return database.Games ?? new List<GameEntry>();
    }

    private sealed record DatabaseDocument(int SchemaVersion, List<GameEntry> Games);

    public void Dispose() => _databaseLock.Dispose();
}
