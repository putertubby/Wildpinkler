using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>
/// Orchestrates installing a mod into a shared, managed folder: parses a FOMOD's ModuleConfig.xml,
/// and finds-or-creates the physical folder for either a FOMOD selection or a manual destination path.
/// </summary>
public sealed class ModInstallService
{
    private readonly ModInstallationStore _store;
    private readonly IArchiveInspector _archiveInspector;
    private readonly FomodInstallerParser _parser;
    private readonly string _installsRoot;

    public ModInstallService(ModInstallationStore store, IArchiveInspector archiveInspector, FomodInstallerParser parser)
    {
        _store = store;
        _archiveInspector = archiveInspector;
        _parser = parser;
        _installsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "mod-installs");
    }

    /// <summary>Parses the mod's ModuleConfig.xml, or returns null if it is not a FOMOD / cannot be read.</summary>
    public FomodModule? TryParseFomod(ModEntry mod)
    {
        if (string.IsNullOrWhiteSpace(mod.ArchivePath) || !File.Exists(mod.ArchivePath))
            return null;
        if (_archiveInspector.DetectFomod(mod.ArchivePath) != FomodState.Yes)
            return null;

        var files = _archiveInspector.ReadFomodFiles(mod.ArchivePath);
        return files.TryGetValue("ModuleConfig.xml", out var xml) ? _parser.TryParse(xml) : null;
    }

    public ArchiveLayout InspectLayout(ModEntry mod)
    {
        if (string.IsNullOrWhiteSpace(mod.ArchivePath) || !File.Exists(mod.ArchivePath))
            return new ArchiveLayout(Array.Empty<string>(), null, false);

        return _archiveInspector.InspectLayout(mod.ArchivePath);
    }

    public async Task<string> FindOrCreateFomodInstallationAsync(
        ModEntry mod, IReadOnlyList<FomodFileInstall> resolvedFiles, string signature, string selectionSummary)
    {
        var existing = (await _store.LoadAsync())
            .FirstOrDefault(item => item.ModId == mod.Id && item.IsFomod && item.SelectionSignature == signature);
        if (existing is not null && Directory.Exists(existing.FolderPath))
            return existing.FolderPath;

        var installation = new ModInstallation
        {
            Id = Guid.NewGuid().ToString("N"),
            ModId = mod.Id,
            IsFomod = true,
            SelectionSignature = signature,
            SelectionSummary = selectionSummary
        };
        installation.FolderPath = Path.Combine(_installsRoot, mod.Id, installation.Id);
        Directory.CreateDirectory(installation.FolderPath);

        ExtractFomodFiles(mod.ArchivePath, resolvedFiles, installation.FolderPath);
        await _store.AddAsync(installation);
        return installation.FolderPath;
    }

    public async Task<string> FindOrCreateManualInstallationAsync(
        ModEntry mod, string sourceRootRelativePath, string destinationRelativePath)
    {
        var sourceRoot = NormalizeRelativePath(sourceRootRelativePath);
        var destination = destinationRelativePath?.Trim() ?? string.Empty;
        if (sourceRoot.Length > 0 && !DefinitionValidation.IsSafeRelativePath(sourceRoot))
            throw new ArgumentException("Source root must be empty or a safe archive-relative path.", nameof(sourceRootRelativePath));
        if (destination.Length > 0 && !DefinitionValidation.IsSafeRelativePath(destination))
            throw new ArgumentException("Destination must be empty or a safe relative path.", nameof(destinationRelativePath));

        var signature = BuildManualSelectionSignature(sourceRoot, destination);
        var existing = (await _store.LoadAsync())
            .FirstOrDefault(item => item.ModId == mod.Id && !item.IsFomod && item.SelectionSignature == signature);
        if (existing is not null && Directory.Exists(existing.FolderPath))
            return existing.FolderPath;

        var installation = new ModInstallation
        {
            Id = Guid.NewGuid().ToString("N"),
            ModId = mod.Id,
            IsFomod = false,
            SelectionSignature = signature,
            SelectionSummary = $"{(sourceRoot.Length == 0 ? "Archive root" : sourceRoot)} -> {(destination.Length == 0 ? "Profile root" : destination)}"
        };
        installation.FolderPath = Path.Combine(_installsRoot, mod.Id, installation.Id);
        Directory.CreateDirectory(installation.FolderPath);

        var mountRoot = destination.Length == 0 ? installation.FolderPath : Path.Combine(installation.FolderPath, destination);
        Directory.CreateDirectory(mountRoot);
        ExtractWholeArchive(mod.ArchivePath, sourceRoot, mountRoot);
        await _store.AddAsync(installation);
        return installation.FolderPath;
    }

    // Applied in the caller's priority-ascending order, so a later entry legitimately overwrites an earlier one.
    private void ExtractFomodFiles(string archivePath, IReadOnlyList<FomodFileInstall> installs, string destinationRoot)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();

        foreach (var install in installs)
        {
            var source = NormalizeKey(install.Source);
            if (install.IsFolder)
            {
                var prefix = source.Length == 0 ? string.Empty : source + "/";
                foreach (var entry in entries)
                {
                    var key = NormalizeKey(entry.Key);
                    if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var relative = key[prefix.Length..];
                    if (relative.Length == 0)
                        continue;

                    ExtractEntry(entry, ResolveSafeDestination(destinationRoot, CombineRelative(install.Destination, relative)));
                }
            }
            else
            {
                var entry = entries.FirstOrDefault(item => NormalizeKey(item.Key).Equals(source, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    continue;

                var destinationRelative = install.Destination.Length > 0 ? install.Destination : Path.GetFileName(source);
                ExtractEntry(entry, ResolveSafeDestination(destinationRoot, destinationRelative));
            }
        }
    }

    internal static string BuildManualSelectionSignature(string sourceRoot, string destination) =>
        $"source:{sourceRoot.Length}:{sourceRoot};destination:{destination.Length}:{destination}";

    internal static void ExtractWholeArchive(string archivePath, string sourceRoot, string destinationRoot)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(item => !item.IsDirectory))
        {
            if (TryGetRelativePathBelowSourceRoot(NormalizeKey(entry.Key), sourceRoot, out var relativePath))
                ExtractEntry(entry, ResolveSafeDestination(destinationRoot, relativePath));
        }
    }

    private static void ExtractEntry(IArchiveEntry entry, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var source = entry.OpenEntryStream();
        using var target = File.Create(destinationPath);
        source.CopyTo(target);
    }

    private static string CombineRelative(string destination, string relative) =>
        destination.Length == 0 ? relative : $"{destination.TrimEnd('/', '\\')}/{relative}";

    private static string NormalizeKey(string? key) => (key ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static string NormalizeRelativePath(string? path) =>
        NormalizeKey(path).TrimEnd('/');

    private static bool TryGetRelativePathBelowSourceRoot(string entryPath, string sourceRoot, out string relativePath)
    {
        if (sourceRoot.Length == 0)
        {
            relativePath = entryPath;
            return relativePath.Length > 0;
        }

        var prefix = sourceRoot + "/";
        if (!entryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = entryPath[prefix.Length..];
        return relativePath.Length > 0;
    }

    /// <summary>
    /// Resolves an archive-declared relative path against the install folder, rejecting any path that
    /// escapes it - archives are untrusted input, so this guards against zip-slip-style path traversal.
    /// </summary>
    private static string ResolveSafeDestination(string root, string relativePath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Archive entry '{relativePath}' escapes the install folder.");

        return candidate;
    }
}
