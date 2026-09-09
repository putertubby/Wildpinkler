using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class ToolStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int CurrentSchemaVersion = 1;
    private readonly string _rootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public ToolStore()
    {
        _databasePath = Path.Combine(_rootPath, "tools.json");
        _backupPath = Path.Combine(_rootPath, "tools.json.bak");
        _temporaryPath = Path.Combine(_rootPath, "tools.json.tmp");
    }

    public async Task<IReadOnlyList<ToolEntry>> LoadAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            if (!File.Exists(_databasePath))
                return Array.Empty<ToolEntry>();

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

    public async Task SaveAsync(IEnumerable<ToolEntry> entries)
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

    private static async Task<IReadOnlyList<ToolEntry>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var database = document.RootElement.Deserialize<DatabaseDocument>(JsonOptions);
        if (database is null || database.SchemaVersion > CurrentSchemaVersion)
            throw new JsonException("The tools database schema is not supported.");

        return database.Tools ?? new List<ToolEntry>();
    }

    private sealed record DatabaseDocument(int SchemaVersion, List<ToolEntry> Tools);

    public void Dispose() => _databaseLock.Dispose();
}
