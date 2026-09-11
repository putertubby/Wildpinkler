using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

/// <summary>A named dependency template; the payload is the same document the graph exports.</summary>
public sealed record DependencyPreset(string Name, string SubgraphJson);

public sealed class DependencyPresetFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<DependencyPreset> Presets { get; set; } = new();
}

public sealed class DependencyPresetStore : IDisposable
{
    public const int MaxPresets = 50;
    private const int CurrentSchemaVersion = 1;
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DependencyPresetStore(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "dependency-presets.json");
        _backupPath = _path + ".bak";
    }

    public async Task<IReadOnlyList<DependencyPreset>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return [];

            await using var stream = file.OpenRead();
            var loaded = await JsonSerializer.DeserializeAsync<DependencyPresetFile>(stream, JsonOptions, cancellationToken);
            return loaded is null || loaded.SchemaVersion > CurrentSchemaVersion ? [] : loaded.Presets;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds or replaces a preset by name and returns the stored set.</summary>
    public async Task<IReadOnlyList<DependencyPreset>> SaveAsync(DependencyPreset preset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preset.Name))
            throw new ArgumentException("A preset needs a name.", nameof(preset));

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Replacing a name frees its slot, so the cap is measured after removing it.
            var others = (await LoadAsync(cancellationToken))
                .Where(existing => !string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count >= MaxPresets)
                throw new InvalidOperationException($"There is room for {MaxPresets} presets. Delete one before saving another.");

            var presets = others
                .Append(preset)
                .OrderBy(existing => existing.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            await WriteAsync(presets, cancellationToken);
            return presets;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<DependencyPreset>> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var presets = (await LoadAsync(cancellationToken))
                .Where(existing => !string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            await WriteAsync(presets, cancellationToken);
            return presets;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Throws when the write fails; a swallowed failure would report a save that never landed.</summary>
    private async Task WriteAsync(List<DependencyPreset> presets, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new DependencyPresetFile { Presets = presets }, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            AtomicFile.Publish(temporaryPath, _path, _backupPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
