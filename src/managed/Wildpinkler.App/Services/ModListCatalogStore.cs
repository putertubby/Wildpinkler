using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum ModListImportStatus
{
    Imported,
    AlreadyPresent,
    Replaced
}

public sealed record ModListImportResult(ModListImportStatus Status, ModListCatalogEntry Entry);

public sealed class ModListCatalogStore
{
    private readonly string _catalogRoot;
    private readonly ModListManifestSerializer _serializer;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);

    public ModListCatalogStore(ModListManifestSerializer serializer, string? rootPath = null)
    {
        _serializer = serializer;
        var root = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _catalogRoot = Path.Combine(root, "mod-lists");
    }

    public async Task<IReadOnlyList<ModListCatalogEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_catalogRoot))
            return Array.Empty<ModListCatalogEntry>();

        var entries = new List<ModListCatalogEntry>();
        foreach (var path in Directory.EnumerateFiles(_catalogRoot, "*" + ModListManifestSerializer.FileExtension, SearchOption.AllDirectories))
        {
            var manifest = await _serializer.LoadAsync(path, cancellationToken);
            entries.Add(CreateEntry(path, manifest));
        }

        return entries
            .OrderBy(entry => entry.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(entry => entry.Manifest.Revision)
            .ToList();
    }

    public async Task<ModListImportResult> ImportAsync(string sourcePath, bool replace = false, CancellationToken cancellationToken = default)
    {
        var manifest = await _serializer.LoadAsync(sourcePath, cancellationToken);
        return await SaveAsync(manifest, replace, cancellationToken);
    }

    public async Task<ModListImportResult> SaveAsync(ModListManifest manifest, bool replace = false, CancellationToken cancellationToken = default)
    {
        ModListManifestValidator.EnsureValid(manifest);
        var targetPath = GetPath(manifest);

        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            var existed = File.Exists(targetPath);
            if (existed)
            {
                var existing = await _serializer.LoadAsync(targetPath, cancellationToken);
                if (string.Equals(_serializer.Serialize(existing), _serializer.Serialize(manifest), StringComparison.Ordinal))
                    return new ModListImportResult(ModListImportStatus.AlreadyPresent, CreateEntry(targetPath, existing));
                if (!replace)
                    throw new ModListRevisionConflictException(manifest.ListId, manifest.Revision);
            }

            await _serializer.SaveAsync(manifest, targetPath, cancellationToken);
            return new ModListImportResult(
                existed ? ModListImportStatus.Replaced : ModListImportStatus.Imported,
                CreateEntry(targetPath, manifest));
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async Task DeleteAsync(ModListCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(entry.FilePath))
                File.Delete(entry.FilePath);
            var directory = Path.GetDirectoryName(entry.FilePath)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private string GetPath(ModListManifest manifest) =>
        Path.Combine(_catalogRoot, manifest.ListId, $"{manifest.Revision}{ModListManifestSerializer.FileExtension}");

    private static ModListCatalogEntry CreateEntry(string path, ModListManifest manifest) =>
        new($"{manifest.ListId}@{manifest.Revision}", path, manifest, ModListGradeEvaluator.Evaluate(manifest).Grade);
}

public sealed class ModListRevisionConflictException : Exception
{
    public ModListRevisionConflictException(string listId, int revision)
        : base($"Mod list '{listId}' revision {revision} already exists with different content.")
    {
    }
}
