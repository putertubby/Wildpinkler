using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

public sealed record GraphLayoutPoint(double X, double Y);

public sealed class GraphLayoutSnapshot
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Node positions keyed by mod id.</summary>
    public Dictionary<string, GraphLayoutPoint> Nodes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Edge bend points keyed by dependency id.</summary>
    public Dictionary<string, GraphLayoutPoint> Bends { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists where the user arranged the dependency graph. This is view state, so it lives beside the
/// mod database rather than inside it; a lost or unreadable file just falls back to a computed layout.
/// </summary>
public sealed class GraphLayoutStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const double MaxCoordinate = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public GraphLayoutStore(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "graph-layout.json");
        _backupPath = _path + ".bak";
    }

    public async Task<GraphLayoutSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return new GraphLayoutSnapshot();

            await using var stream = file.OpenRead();
            var snapshot = await JsonSerializer.DeserializeAsync<GraphLayoutSnapshot>(stream, JsonOptions, cancellationToken);
            if (snapshot is null || snapshot.SchemaVersion > CurrentSchemaVersion)
                return new GraphLayoutSnapshot();

            snapshot.Nodes = Usable(snapshot.Nodes);
            snapshot.Bends = Usable(snapshot.Bends);
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new GraphLayoutSnapshot();
        }
    }

    public async Task SaveAsync(GraphLayoutSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        snapshot.SchemaVersion = CurrentSchemaVersion;
        await _writeGate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            AtomicFile.Publish(temporaryPath, _path, _backupPath);
        }        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The dependency graph layout could not be saved.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _writeGate.Release();
        }
    }

    /// <summary>Drops placements a canvas cannot use; an out-of-range value would corrupt the whole layout pass.</summary>
    private static Dictionary<string, GraphLayoutPoint> Usable(Dictionary<string, GraphLayoutPoint> points) =>
        points
            .Where(entry => entry.Value is not null && IsUsable(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static bool IsUsable(GraphLayoutPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        Math.Abs(point.X) <= MaxCoordinate && Math.Abs(point.Y) <= MaxCoordinate;

    public void Dispose() => _writeGate.Dispose();
}
