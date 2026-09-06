using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class ModListBuildStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ModListBuildStore(string? rootPath = null)
    {
        var root = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(root, "mod-list-builds.json");
        _backupPath = _path + ".bak";
        _temporaryPath = _path + ".tmp";
    }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<ModListBuild>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<ModListBuild>();
            var builds = await ReadAsync(_path, cancellationToken);
            var recovered = false;
            foreach (var build in builds.Where(build => build.State == ModListBuildState.Running))
            {
                build.State = ModListBuildState.Ready;
                foreach (var task in build.Tasks.Where(task => task.State == ModListBuildTaskState.Running))
                {
                    task.State = task.Kind is ModListBuildTaskKind.ConfigureFolder or ModListBuildTaskKind.ToolConsent or ModListBuildTaskKind.ToolInvocation
                        ? ModListBuildTaskState.NeedsUser
                        : ModListBuildTaskState.Pending;
                    task.StatusText = "Interrupted; review and resume.";
                }
                recovered = true;
            }
            if (recovered)
                await WriteAsync(builds, cancellationToken);
            return builds;
        }
        catch (JsonException) when (File.Exists(_backupPath))
        {
            return await ReadAsync(_backupPath, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<ModListBuild> builds, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(builds.ToList(), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpsertAsync(ModListBuild build, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var builds = File.Exists(_path) ? await ReadAsync(_path, cancellationToken) : new List<ModListBuild>();
            var index = builds.FindIndex(item => item.Id == build.Id);
            build.UpdatedAt = DateTimeOffset.UtcNow;
            if (index < 0)
                builds.Add(build);
            else
                builds[index] = build;
            await WriteAsync(builds, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(string buildId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var builds = File.Exists(_path) ? await ReadAsync(_path, cancellationToken) : new List<ModListBuild>();
            await WriteAsync(builds.Where(build => build.Id != buildId).ToList(), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlySet<string>> GetReferencedInstallationIdsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken))
            .Where(build => build.State is not (ModListBuildState.Completed or ModListBuildState.Discarded))
            .SelectMany(build => build.Artifacts)
            .Select(artifact => artifact.InstallationId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<List<ModListBuild>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<DatabaseDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new JsonException("The mod-list build journal is empty.");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException($"The mod-list build journal is schema {document.SchemaVersion}, but this build requires schema {CurrentSchemaVersion}.");
        return document.Builds ?? new List<ModListBuild>();
    }

    private async Task WriteAsync(List<ModListBuild> builds, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using (var stream = new FileStream(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, new DatabaseDocument(CurrentSchemaVersion, builds), JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        if (File.Exists(_path))
            File.Replace(_temporaryPath, _path, _backupPath, true);
        else
            File.Move(_temporaryPath, _path, true);
    }

    private sealed record DatabaseDocument(int SchemaVersion, List<ModListBuild> Builds);
}
