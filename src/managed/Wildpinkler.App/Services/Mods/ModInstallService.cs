using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpSevenZip;
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
    private readonly ArchiveExtractionLimits _extractionLimits;

    public ModInstallService(
        ModInstallationStore store,
        IArchiveInspector archiveInspector,
        FomodInstallerParser parser,
        string? installsRoot = null,
        ArchiveExtractionLimits? extractionLimits = null)
    {
        _store = store;
        _archiveInspector = archiveInspector;
        _parser = parser;
        _extractionLimits = extractionLimits ?? ArchiveExtractionLimits.Default;
        _installsRoot = installsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "mod-installs");
    }

    /// <summary>Parses the mod's ModuleConfig.xml, or returns null if it is not a FOMOD / cannot be read.</summary>
    /// <param name="analysisProgress">Reports entry-scan and FOMOD-metadata-file progress, so the UI can show real progress during analysis.</param>
    public FomodModule? TryParseFomod(ModEntry mod, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mod.ArchivePath) || !File.Exists(mod.ArchivePath))
            return null;
        if (_archiveInspector.DetectFomod(mod.ArchivePath, analysisProgress, cancellationToken) != FomodState.Yes)
            return null;

        var files = _archiveInspector.ReadFomodFiles(mod.ArchivePath, analysisProgress, cancellationToken);
        return files.TryGetValue("ModuleConfig.xml", out var xml) ? _parser.TryParse(xml) : null;
    }

    /// <summary>Like <see cref="TryParseFomod"/>, but runs the archive decode off the caller's thread.</summary>
    public Task<FomodModule?> TryParseFomodAsync(ModEntry mod, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => TryParseFomod(mod, analysisProgress, cancellationToken), cancellationToken);

    public ArchiveLayout InspectLayout(ModEntry mod, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mod.ArchivePath) || !File.Exists(mod.ArchivePath))
            return new ArchiveLayout(Array.Empty<string>(), null, false);

        return _archiveInspector.InspectLayout(mod.ArchivePath, analysisProgress, cancellationToken);
    }

    /// <summary>Like <see cref="InspectLayout"/>, but runs the archive scan off the caller's thread.</summary>
    public Task<ArchiveLayout> InspectLayoutAsync(ModEntry mod, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => InspectLayout(mod, analysisProgress, cancellationToken), cancellationToken);

    /// <summary>
    /// Reuse or create an installation for the given resolved FOMOD files.
    /// <paramref name="destination"/> is the destination base chosen in the FOMOD wizard
    /// (default: the game's PluginDataFolder) and is the TARGET FOLDER every file is placed
    /// under. FOMOD destinations are relative to it: an empty destination resolves to the
    /// base itself (files go directly in the game data folder), and a non-empty one nests
    /// under it (e.g. <c>SKSE</c> -> <c>Data/SKSE/...</c>, <c>textures</c> -> <c>Data/textures/...</c>).
    /// The base is also persisted on the recipe for replay and distinguishes installs that
    /// chose different wizard destinations.
    /// </summary>
    public async Task<ModInstallation> FindOrCreateFomodInstallationAsync(
        ModEntry mod, IReadOnlyList<FomodFileInstall> resolvedFiles, string signature, string selectionSummary,
        IReadOnlyList<FomodStepSelection> selections,
        string destination = "",
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archiveSha256 = await EnsureArchiveSha256Async(mod, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var recipe = new FomodInstallationRecipe
        {
            ModuleConfigSha256 = ReadFomodModuleSha256(mod),
            Selections = selections.SelectMany(step => step.Groups
                .Where(group => group.SelectedPlugins.Count > 0)
                .Select(group => new FomodSelectionChoice
                {
                    Step = step.Step.Name,
                    Group = group.Group.Name,
                    Plugins = group.SelectedPlugins.Select(plugin => plugin.Name).ToList()
                }))
                .ToList(),
            Destination = destination
        };
        var existing = (await _store.LoadAsync())
            .FirstOrDefault(item => item.ModId == mod.Id && item.Recipe is FomodInstallationRecipe &&
                                    item.SourceArchiveSha256 == archiveSha256 && item.SelectionSignature == signature);
        if (existing is not null && Directory.Exists(existing.FolderPath))
            return existing;

        var installation = new ModInstallation
        {
            Id = Guid.NewGuid().ToString("N"),
            ModId = mod.Id,
            SourceArchiveSha256 = archiveSha256,
            Recipe = recipe,
            SelectionSignature = signature,
            SelectionSummary = selectionSummary
        };
        return await ExtractAndPublishAsync(
            installation,
            (root, token) => Task.Run(() => ExtractFomodFiles(mod.ArchivePath, resolvedFiles, root, destination, progress, token), token),
            cancellationToken);
    }

    public async Task<ModInstallation> FindOrCreateFromRecipeAsync(
        ModEntry mod, ModInstallationRecipe recipe, IReadOnlyList<ProfileFolder> profileFolders,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (recipe is ManualInstallationRecipe manual)
            return await FindOrCreateManualInstallationAsync(mod, manual.SourceRoot, manual.Destination, progress, cancellationToken);
        if (recipe is not FomodInstallationRecipe fomod)
            throw new InvalidOperationException("This installation requires user guidance and cannot be replayed automatically.");

        var module = await TryParseFomodAsync(mod, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The archive no longer contains a readable FOMOD installer.");
        var actualModuleHash = ReadFomodModuleSha256(mod);
        if (!string.Equals(actualModuleHash, fomod.ModuleConfigSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The FOMOD installer changed since this recipe was recorded.");

        var fileState = new ProfileFileStateProvider(profileFolders);
        var engine = new FomodSelectionResolver();
        var selections = new List<FomodStepSelection>();
        var flags = new Dictionary<string, string>();
        foreach (var step in module.InstallSteps)
        {
            var requestedForStep = fomod.Selections.Where(choice => choice.Step == step.Name).ToList();
            if (!engine.IsStepVisible(step, flags, fileState))
            {
                if (requestedForStep.Count > 0)
                    throw new InvalidDataException($"Recorded FOMOD step '{step.Name}' is no longer visible.");
                continue;
            }

            var stepSelection = new FomodStepSelection { Step = step };
            foreach (var group in step.Groups)
            {
                var requested = requestedForStep.Where(choice => choice.Group == group.Name).ToList();
                if (requested.Count > 1)
                    throw new InvalidDataException($"FOMOD group '{group.Name}' is duplicated in the recipe.");
                var plugins = requested.Count == 0
                    ? new List<FomodPlugin>()
                    : requested[0].Plugins.Select(name =>
                        group.Plugins.SingleOrDefault(plugin => plugin.Name == name)
                        ?? throw new InvalidDataException($"FOMOD option '{name}' no longer exists in '{group.Name}'.")).ToList();
                var validationError = engine.ValidateGroup(group, plugins);
                if (validationError is not null)
                    throw new InvalidDataException(validationError);
                stepSelection.Groups.Add(new FomodGroupSelection { Group = group, SelectedPlugins = plugins });
            }
            if (requestedForStep.Any(choice => step.Groups.All(group => group.Name != choice.Group)))
                throw new InvalidDataException($"A recorded FOMOD group in step '{step.Name}' no longer exists.");
            selections.Add(stepSelection);
            flags = engine.AccumulateFlags(selections);
        }
        if (fomod.Selections.Any(choice => module.InstallSteps.All(step => step.Name != choice.Step)))
            throw new InvalidDataException("A recorded FOMOD step no longer exists.");

        var resolvedFiles = engine.ResolveFileInstalls(module, selections, fileState);
        var signature = engine.ComputeSelectionSignature(selections, fomod.Destination);
        return await FindOrCreateFomodInstallationAsync(
            mod, resolvedFiles, signature, DescribeSelections(fomod), selections, fomod.Destination, progress, cancellationToken);
    }

    private static string DescribeSelections(FomodInstallationRecipe recipe) =>
        string.Join(", ", recipe.Selections.SelectMany(choice => choice.Plugins.Select(plugin => $"{choice.Group}: {plugin}")));

    public async Task<ModInstallation> FindOrCreateManualInstallationAsync(
        ModEntry mod, string sourceRootRelativePath, string destinationRelativePath,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceRoot = NormalizeRelativePath(sourceRootRelativePath);
        var destination = destinationRelativePath?.Trim() ?? string.Empty;
        var archiveSha256 = await EnsureArchiveSha256Async(mod, progress, cancellationToken);
        if (sourceRoot.Length > 0 && !DefinitionValidation.IsSafeRelativePath(sourceRoot))
            throw new ArgumentException("Source root must be empty or a safe archive-relative path.", nameof(sourceRootRelativePath));
        if (destination.Length > 0 && !DefinitionValidation.IsSafeRelativePath(destination))
            throw new ArgumentException("Destination must be empty or a safe relative path.", nameof(destinationRelativePath));

        var signature = BuildManualSelectionSignature(sourceRoot, destination);
        var existing = (await _store.LoadAsync())
            .FirstOrDefault(item => item.ModId == mod.Id && item.Recipe is ManualInstallationRecipe &&
                                    item.SourceArchiveSha256 == archiveSha256 && item.SelectionSignature == signature);
        if (existing is not null && Directory.Exists(existing.FolderPath))
            return existing;

        var installation = new ModInstallation
        {
            Id = Guid.NewGuid().ToString("N"),
            ModId = mod.Id,
            SourceArchiveSha256 = archiveSha256,
            Recipe = new ManualInstallationRecipe { SourceRoot = sourceRoot, Destination = destination },
            SelectionSignature = signature,
            SelectionSummary = $"{(sourceRoot.Length == 0 ? "Archive root" : sourceRoot)} -> {(destination.Length == 0 ? "Profile root" : destination)}"
        };
        return await ExtractAndPublishAsync(installation, (root, token) => Task.Run(() =>
        {
            var mountRoot = destination.Length == 0 ? root : Path.Combine(root, destination);
            Directory.CreateDirectory(mountRoot);
            ExtractWholeArchive(mod.ArchivePath, sourceRoot, mountRoot, _extractionLimits, progress, token);
        }, token), cancellationToken);
    }

    /// <summary>
    /// Unpacks into a hidden staging folder, moves it into place, and publishes the record. The extraction
    /// and post-extraction work run on worker threads; cancellation tears the staging folder down.
    /// </summary>
    private async Task<ModInstallation> ExtractAndPublishAsync(
        ModInstallation installation,
        Func<string, CancellationToken, Task> extract,
        CancellationToken cancellationToken)
    {
        var installParent = Path.Combine(_installsRoot, installation.ModId);
        var stagingPath = Path.Combine(installParent, $".pending-{installation.Id}");
        installation.FolderPath = Path.Combine(installParent, installation.Id);

        try
        {
            Directory.CreateDirectory(stagingPath);
            await extract(stagingPath, cancellationToken);
            await Task.Run(() =>
            {
                Directory.Move(stagingPath, installation.FolderPath);
                ScanPluginFiles(installation);
            }, cancellationToken);
            await _store.AddAsync(installation);
            return installation;
        }
        catch
        {
            // A cancelled or failed extraction can leave a multi-gigabyte staging tree; remove it off
            // the UI thread so cleanup never freezes the app. Cleanup deliberately ignores the
            // (possibly cancelled) token so partial files are still removed.
            await Task.Run(() => DeleteDirectoryIfPresent(stagingPath), CancellationToken.None);
            await Task.Run(() => DeleteDirectoryIfPresent(installation.FolderPath), CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Records the plugin files (.esm/.esp/.esl) shipped by the mod into
    /// <see cref="ModInstallation.Plugins"/>. Best-effort: a scan failure leaves the list empty
    /// rather than failing an otherwise successful installation.
    /// </summary>
    private static void ScanPluginFiles(ModInstallation installation)
    {
        var folderPath = installation.FolderPath;
        if (!Directory.Exists(folderPath))
            return;

        var plugins = installation.Plugins ??= new List<PluginFileEntry>();
        plugins.Clear();
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(
                folderPath, "*", SearchOption.AllDirectories)
                .Where(PluginMasterInspector.IsPluginFile))
            {
                var relativePath = Path.GetRelativePath(folderPath, filePath).Replace('\\', '/');
                plugins.Add(new PluginFileEntry
                {
                    FileName = Path.GetFileName(filePath),
                    RelativePath = relativePath
                });
            }

            plugins.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // A read failure (permissions, long paths, etc.) must not abort the installation.
            plugins.Clear();
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private string ReadFomodModuleSha256(ModEntry mod)
    {
        var files = _archiveInspector.ReadFomodFiles(mod.ArchivePath);
        if (!files.TryGetValue("ModuleConfig.xml", out var xml))
            throw new InvalidDataException("The FOMOD archive has no ModuleConfig.xml.");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));
    }

    /// <summary>
    /// Returns the mod's archive SHA-256, computing it (and persisting it on the entry) on first
    /// use. The digest is intentionally lazy: large archives are not hashed at import time, only
    /// when an install actually needs it, and the result is cached on the <see cref="ModEntry"/>
    /// so it is computed at most once.
    /// </summary>
    public async Task<string> EnsureArchiveSha256Async(
        ModEntry mod, IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(mod.Sha256))
            return mod.Sha256!;
        if (string.IsNullOrWhiteSpace(mod.ArchivePath) || !File.Exists(mod.ArchivePath))
            throw new InvalidOperationException($"'{mod.Name}' has no archive to hash. Re-import or re-download the archive before installing it.");

        var path = mod.ArchivePath;
        var bytesTotal = new FileInfo(path).Length;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hasher = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        var entryLabel = $"Hashing {Path.GetFileName(path)}";
        var reporter = new ExtractionReporter(progress, 1, bytesTotal, entryLabel);
        var bytesWritten = 0L;
        var digestBytes = Array.Empty<byte>();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                hasher.TransformBlock(buffer, 0, read, buffer, 0);
                bytesWritten += read;
                reporter.Report(0, 1, bytesWritten, bytesTotal, entryLabel);
            }
            hasher.TransformFinalBlock(buffer, 0, 0);
            digestBytes = (byte[])hasher.Hash!;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            reporter.Finish(1, 1, bytesWritten, bytesTotal, entryLabel);
        }

        var digest = Convert.ToHexString(digestBytes);
        mod.Sha256 = digest;
        return digest;
    }

    /// <summary>
    /// Benchmark entry point onto the production extraction paths. Lets the opt-in extraction
    /// benchmark measure the exact production code without duplicating it. The <paramref name="strategy"/>
    /// selects which backend to exercise so a fair A/B can be run: "sharpcompress" forces the
    /// SharpCompress path (even for 7z archives), and "native7z" forces the native 7z path.
    /// </summary>
    internal static void ExtractFomodFilesForBenchmark(
        string archivePath, IReadOnlyList<FomodFileInstall> installs, string destinationRoot,
        IProgress<ExtractionProgress>? progress, string strategy, CancellationToken cancellationToken, string destinationBase = "")
    {
        var service = new ModInstallService(null!, null!, null!);
        var canonical = strategy.Trim().Equals("native7z", StringComparison.OrdinalIgnoreCase) ? "native7z" : "sharpcompress";
        if (canonical == "native7z")
            service.ExtractFomodFilesSevenZip(archivePath, installs, destinationRoot, destinationBase, progress, cancellationToken);
        else
            service.ExtractFomodFilesSharpCompress(archivePath, installs, destinationRoot, destinationBase, progress, cancellationToken);
    }

    // Applied in the caller's priority-ascending order, so a later entry legitimately overwrites an earlier one.
    private void ExtractFomodFiles(
        string archivePath, IReadOnlyList<FomodFileInstall> installs, string destinationRoot, string destinationBase,
        IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        if (IsSevenZipArchive(archivePath))
        {
            // Solid 7z archives are slow to re-open per entry, so the native backend decodes the
            // selected entries in ONE archive-order operation instead of re-decoding each entry.
            ExtractFomodFilesSevenZip(archivePath, installs, destinationRoot, destinationBase, progress, cancellationToken);
        }
        else
        {
            ExtractFomodFilesSharpCompress(archivePath, installs, destinationRoot, destinationBase, progress, cancellationToken);
        }
    }

    private void ExtractFomodFilesSharpCompress(
        string archivePath, IReadOnlyList<FomodFileInstall> installs, string destinationRoot, string destinationBase,
        IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();

        // A normalized-key index sorted case-insensitively lets each install's file set be found
        // by binary search instead of a linear O(entries) scan per install (FOMOD archives can
        // declare hundreds of installs against tens of thousands of files).
        var index = new List<(IArchiveEntry Entry, string Key)>(entries.Count);
        foreach (var entry in entries)
            index.Add((entry, NormalizeKey(entry.Key)));
        index.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

        var work = new List<(IArchiveEntry Entry, string Destination)>();
        foreach (var install in installs)
        {
            var source = NormalizeKey(install.Source);
            // Destinations are relative to the target folder (the destination base, default the
            // game's data folder): an empty destination resolves to the base itself, and a
            // non-empty one nests under it (e.g. `SKSE` -> `Data/SKSE`).
            var effectiveDest = install.Destination.Length > 0
                ? CombineRelative(destinationBase, install.Destination)
                : destinationBase;
            if (install.IsFolder)
            {
                var prefix = source.Length == 0 ? string.Empty : source + "/";
                for (var i = FindKeyStart(index.Count, i => index[i].Key, prefix);
                    i < index.Count && index[i].Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    i++)
                {
                    var relative = index[i].Key[prefix.Length..];
                    if (relative.Length == 0)
                        continue;

                    work.Add((index[i].Entry, CombineRelative(effectiveDest, relative)));
                }
            }
            else
            {
                var i = FindKeyStart(index.Count, i => index[i].Key, source);
                if (i < index.Count && index[i].Key.Equals(source, StringComparison.OrdinalIgnoreCase))
                {
                    var leaf = install.Destination.Length > 0 ? install.Destination : Path.GetFileName(source);
                    work.Add((index[i].Entry, CombineRelative(destinationBase, leaf)));
                }
            }
        }

        var budget = new ArchiveExtractionBudget(_extractionLimits);
        var verifiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reporter = new ExtractionReporter(progress, work.Count, work.Sum(item => item.Entry.Size), "Extracting");
        foreach (var (entry, destinationRelative) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var written = ExtractEntry(
                entry, destinationRoot, ResolveSafeDestination(destinationRoot, destinationRelative),
                budget, verifiedDirectories, cancellationToken);
            reporter.Report(entry, written);
        }

        reporter.Finish();
    }

    /// <summary>
    /// Unpacks a 7z FOMOD through the native 7z backend in a single archive-order decode. SharpCompress
    /// re-opens a per-entry stream for every file, which on a SOLID archive re-decodes a large block
    /// each time (the dominant cost for big FOMODs). Instead this builds the priority-ordered work
    /// list, lets 7-Zip decode the distinct selected entries in one pass into a staging folder, and
    /// then relocates the staged files to their destinations in install (priority) order so a later
    /// entry still overwrites an earlier one. All on-disk safety checks (path containment, link
    /// rejection, byte budget, written-file verification, cancellation) are preserved.
    /// </summary>
    private void ExtractFomodFilesSevenZip(
        string archivePath, IReadOnlyList<FomodFileInstall> installs, string destinationRoot, string destinationBase,
        IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var budget = new ArchiveExtractionBudget(_extractionLimits);
        var verifiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string stagingDir;

        using (var extractor = new SharpSevenZipExtractor(archivePath))
        {
            var fileData = extractor.ArchiveFileData;
            if (fileData.Count == 0)
                throw new InvalidOperationException("The 7z archive contains no entries.");
            if (extractor.ErrorFlags != ArchiveErrorFlags.None)
                throw new InvalidOperationException($"The 7z archive could not be opened: {extractor.ErrorMessage}");

            // A normalized-key index of non-directory entries sorted case-insensitively lets each
            // install's file set be found by binary search (same shape as the SharpCompress path).
            // Metadata is captured at planning time so the shared budget/link checks never have to
            // understand SharpSevenZip's types.
            var index = new List<(int Index, string Key, string ArchivePath, ArchiveEntryMetadata Metadata)>(fileData.Count);
            var bytesTotal = 0L;
            foreach (var info in fileData)
            {
                if (info.IsDirectory)
                    continue;

                var key = NormalizeKey(info.FileName);
                if (key.Length == 0)
                    continue;

                index.Add((info.Index, key, info.FileName.Replace('\\', '/'),
                    new ArchiveEntryMetadata(key, (long)info.Size, 0L, (uint)info.Attributes, null)));
                bytesTotal += (long)info.Size;
            }
            index.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            // Every candidate (source entry, destination) the FOMOD rules map to, in rule order.
            // The rule's priority and position are carried alongside so the dedup below does not
            // have to reverse-map back into the installs list (folder rules emit many candidates).
            var candidates = new List<(int ArchiveIndex, int Priority, int RuleIndex, string ArchivePath, string Destination, ArchiveEntryMetadata Metadata)>();
            for (var rule = 0; rule < installs.Count; rule++)
            {
                var install = installs[rule];
                var source = NormalizeKey(install.Source);
                // Destinations are relative to the target folder (the destination base, default the
                // game's data folder): an empty destination resolves to the base itself, and a
                // non-empty one nests under it (e.g. `SKSE` -> `Data/SKSE`).
                var effectiveDest = install.Destination.Length > 0
                    ? CombineRelative(destinationBase, install.Destination)
                    : destinationBase;
                if (install.IsFolder)
                {
                    var prefix = source.Length == 0 ? string.Empty : source + "/";
                    for (var i = FindKeyStart(index.Count, i => index[i].Key, prefix);
                        i < index.Count && index[i].Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                        i++)
                    {
                        var relative = index[i].Key[prefix.Length..];
                        if (relative.Length == 0)
                            continue;

                        candidates.Add((index[i].Index, install.Priority, rule, index[i].ArchivePath, CombineRelative(effectiveDest, relative), index[i].Metadata));
                    }
                }
                else
                {
                    var i = FindKeyStart(index.Count, i => index[i].Key, source);
                    if (i < index.Count && index[i].Key.Equals(source, StringComparison.OrdinalIgnoreCase))
                    {
                        var leaf = install.Destination.Length > 0 ? install.Destination : Path.GetFileName(source);
                        candidates.Add((index[i].Index, install.Priority, rule, index[i].ArchivePath, CombineRelative(destinationBase, leaf), index[i].Metadata));
                    }
                }
            }

            // Run the per-entry pre-checks and link rejections over every candidate in rule order,
            // so a malicious or oversized entry aborts the install exactly as the SharpCompress
            // path would abort, even when a higher-priority rule overwrites it later.
            foreach (var candidate in candidates)
            {
                ArchiveExtractionBudget.RejectLinkEntry(candidate.Metadata);
                budget.AccountForEntry(candidate.Metadata);
            }

            // Resolve ONE winning source per case-insensitive destination so each source is
            // decoded only once. The winner must match the on-disk result of the SharpCompress
            // path, which writes every candidate in rule order so the LAST write to a
            // destination wins: the highest-priority rule, and for equal priority the later
            // rule index.
            var work = new List<(int ArchiveIndex, string SourceArchivePath, string Destination, ArchiveEntryMetadata Metadata)>();
            var byDestination = new Dictionary<string, (int Priority, int RuleIndex, int ArchiveOrdinal, string ArchivePath, string Destination, ArchiveEntryMetadata Metadata)>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var (archiveIndex, priority, ruleIndex, sourcePath, destination, metadata) = candidate;
                if (byDestination.TryGetValue(destination, out var current) &&
                    (current.Priority > priority ||
                     (current.Priority == priority && current.RuleIndex >= ruleIndex)))
                    continue;

                byDestination[destination] = (priority, ruleIndex, archiveIndex, sourcePath, destination, metadata);
            }

            foreach (var (_, stored) in byDestination)
            {
                work.Add((stored.ArchiveOrdinal, stored.ArchivePath, stored.Destination, stored.Metadata));
            }
            work.Sort((a, b) =>
            {
                var byArchive = a.ArchiveIndex.CompareTo(b.ArchiveIndex);
                return byArchive != 0 ? byArchive : string.Compare(a.Destination, b.Destination, StringComparison.Ordinal);
            });

            // Canonicalize the extraction root ONCE. Every planned destination is then
            // resolved and containment-checked once against this root before any output is
            // created, so the relocation loop only walks already-verified absolute paths.
            var destinationRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
            stagingDir = Path.Combine(destinationRoot, "__sevenzip_staging__");
            // A crashed earlier run can leave the staging folder behind; remove it so this run
            // starts from a clean tree.
            DeleteDirectoryBestEffort(stagingDir);
            Directory.CreateDirectory(stagingDir);
            try
            {
                // ONE archive-order decode of every distinct selected entry. 7-Zip streams the solid
                // blocks once and writes each selected file; no per-entry re-decode.
                var distinctIndexes = work.Select(item => item.ArchiveIndex).Distinct().OrderBy(x => x).ToArray();
                if (distinctIndexes.Length > 0)
                    extractor.ExtractFiles(stagingDir, distinctIndexes);

                // Pre-resolve and containment-check each distinct planned destination ONCE, so the
                // relocation loop never re-canonicalizes or re-checks containment per file.
                var resolvedDestinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var destination in work.Select(item => item.Destination).Distinct(StringComparer.OrdinalIgnoreCase))
                    resolvedDestinations[destination] = ResolveSafeDestination(destinationRootFull, destination);

                var reporter = new ExtractionReporter(progress, work.Count, bytesTotal, "Extracting");
                var bytesWritten = 0L;
                for (var i = 0; i < work.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (_, sourceArchivePath, destinationRelative, metadata) = work[i];
                    var destinationPath = resolvedDestinations[destinationRelative];
                    var stagedPath = Path.Combine(stagingDir, sourceArchivePath.Replace('/', Path.DirectorySeparatorChar));
                    var written = RelocateStagedFile(
                        stagedPath, destinationPath, metadata.Key, destinationRootFull, budget, verifiedDirectories, cancellationToken);
                    bytesWritten += written;
                    reporter.Report(i + 1, work.Count, bytesWritten, bytesTotal, destinationRelative);
                }

                reporter.Finish();
            }
            finally
            {
                DeleteDirectoryBestEffort(stagingDir);
            }
        }
    }

    /// <summary>
    /// Copies one natively-decoded file from the staging folder to its final destination, applying the
    /// same on-disk safety checks as <see cref="ExtractEntry"/>: link rejection, directory-link
    /// avoidance, byte budget, written-file verification, and cancellation.
    /// </summary>
    private static long RelocateStagedFile(
        string stagedPath, string destinationPath, string entryKey, string destinationRoot,
        ArchiveExtractionBudget budget, HashSet<string>? verifiedDirectories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            ArchiveExtractionBudget.CreateDirectoryWithoutLinks(destinationRoot, directory, verifiedDirectories);

        RejectExistingLink(destinationPath);

        long written = 0;
        try
        {
            // The native decode already wrote the full file, so a bounded copy lands it in place.
            using var source = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    written += read;

                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException("Entry copy cancelled.", cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(destinationPath); }
            catch
            {
                // Best effort: cleanup failures must not mask the cancellation.
            }
            throw;
        }

        budget.AccountForWrittenBytes(entryKey, written);
        ArchiveExtractionBudget.VerifyWrittenFile(destinationPath);
        return written;
    }

    /// <summary>Detects a 7z archive by its magic signature (not by extension).</summary>
    internal static bool IsSevenZipArchive(string path)
    {
        try
        {
            var buffer = new byte[6];
            using var stream = File.OpenRead(path);
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0)
                    break;
                read += count;
            }

            return read == buffer.Length &&
                buffer[0] == 0x37 && buffer[1] == 0x7A && buffer[2] == 0xBC &&
                buffer[3] == 0xAF && buffer[4] == 0x27 && buffer[5] == 0x1C;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort: staging cleanup must not mask a real error or cancellation.
        }
    }

    internal static string BuildManualSelectionSignature(string sourceRoot, string destination) =>
        $"source:{sourceRoot.Length}:{sourceRoot};destination:{destination.Length}:{destination}";

    internal static void ExtractWholeArchive(
        string archivePath, string sourceRoot, string destinationRoot, ArchiveExtractionLimits? limits = null,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var work = new List<(IArchiveEntry Entry, string RelativePath)>();
        foreach (var entry in archive.Entries.Where(item => !item.IsDirectory))
        {
            if (TryGetRelativePathBelowSourceRoot(NormalizeKey(entry.Key), sourceRoot, out var relativePath))
                work.Add((entry, relativePath));
        }

        var budget = new ArchiveExtractionBudget(limits);
        var verifiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reporter = new ExtractionReporter(progress, work.Count, work.Sum(item => item.Entry.Size), "Extracting");
        foreach (var (entry, relativePath) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var written = ExtractEntry(
                entry, destinationRoot, ResolveSafeDestination(destinationRoot, relativePath),
                budget, verifiedDirectories, cancellationToken);
            reporter.Report(entry, written);
        }

        reporter.Finish();
    }

    /// <summary>
    /// Unpacks one entry and returns the number of bytes written. <paramref name="verifiedDirectories"/>
    /// is a run-scoped memo of directories already verified (created or checked for a reparse point)
    /// during this extraction, so a directory tree shared by many entries is walked once.
    /// </summary>
    private static long ExtractEntry(
        IArchiveEntry entry, string destinationRoot, string destinationPath,
        ArchiveExtractionBudget budget, HashSet<string>? verifiedDirectories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArchiveExtractionBudget.RejectLinkEntry(entry);
        budget.AccountForEntry(entry);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            ArchiveExtractionBudget.CreateDirectoryWithoutLinks(destinationRoot, directory, verifiedDirectories);

        RejectExistingLink(destinationPath);

        long written = 0;
        var canceled = false;
        try
        {
            using var source = entry.OpenEntryStream();
            using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // Pre-allocate the destination when the archive declares a reliable size, so the disk
            // reserves the space up front instead of growing the file in increments as data lands.
            var declaredSize = entry.Size;
            if (declaredSize > 0)
            {
                // SetLength repositions the stream to the new end, so rewind before writing data
                // at the start of the file.
                target.SetLength(declaredSize);
                target.Seek(0, SeekOrigin.Begin);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    written += read;

                    // Check cancellation every 4 MB chunk so a multi-gigabyte entry can be cancelled
                    // promptly without draining the whole stream first.
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException("Entry copy cancelled.", cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // The declared size is a hint, not a guarantee: trim any pre-allocated padding so the
            // file is exactly the number of bytes the stream actually produced (no trailing zeros
            // if the stream ended short, and no over-short truncation if it was truncated).
            if (declaredSize > 0 && written != declaredSize)
                target.SetLength(written);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            throw;
        }
        finally
        {
            if (canceled)
            {
                // Remove the partially written file so a cancelled install leaves no partial data behind.
                try { File.Delete(destinationPath); }
                catch
                {
                    // Best effort: cleanup failures must not mask the cancellation.
                }
            }
        }

        budget.AccountForWrittenBytes(entry.Key ?? destinationPath, written);
        ArchiveExtractionBudget.VerifyWrittenFile(destinationPath);
        return written;
    }

    private const int CopyChunkSize = 4 * 1024 * 1024;

    private static void RejectExistingLink(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new ArchiveEntryRejectedException(
                    $"'{path}' is a link, so Wildpinkler will not overwrite it.");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string CombineRelative(string destination, string relative) =>
        destination.Length == 0 ? relative : $"{destination.TrimEnd('/', '\\')}/{relative}";

    private static string NormalizeKey(string? key) => (key ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static string NormalizeRelativePath(string? path) =>
        NormalizeKey(path).TrimEnd('/');

    /// <summary>
    /// Returns the first index in a key-sorted entry index whose key compares >= <paramref name="key"/>
    /// (case-insensitive), or the index count when none do. Binary search keeps each install's lookup
    /// O(log n) even when a FOMOD declares many installs against tens of thousands of files.
    /// </summary>
    private static int FindKeyStart(int count, Func<int, string> keyAt, string key)
    {
        var low = 0;
        var high = count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (string.Compare(keyAt(mid), key, StringComparison.OrdinalIgnoreCase) < 0)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

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

/// <summary>
/// Tracks per-entry extraction progress on a single worker thread and forwards updates to an optional
/// <see cref="IProgress{T}"/> observer. Updates are coalesced so a FOMOD with tens of thousands of small
/// files does not marshal a storm of callbacks to the UI thread: at most one update is forwarded per
/// <see cref="CoalesceWindow"/>, an update is forced when a very large entry finishes (no other per-file
/// update would arrive until it completes), and a final update is always sent from <see cref="Finish"/>
/// so observers reliably see the terminal state. No locking is needed because each extraction runs on
/// exactly one thread.
/// </summary>
internal sealed class ExtractionReporter
{
    /// <summary>Minimum time between two forwarded updates (at most ~10 updates per second).</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A single entry at or above this size forces an immediate update, since no other per-file update
    /// will arrive until this (possibly multi-gigabyte) entry has fully completed.
    /// </summary>
    private static readonly long LargeEntryThresholdBytes = 256L * 1024 * 1024;

    private readonly IProgress<ExtractionProgress>? _progress;
    private readonly int _filesTotal;
    private readonly long _bytesTotal;
    private readonly string _stage;
    private readonly Func<TimeSpan> _elapsed;
    private int _filesDone;
    private long _bytesWritten;
    private bool _hasReported;
    private TimeSpan _lastReportedAt;

    public ExtractionReporter(
        IProgress<ExtractionProgress>? progress, int filesTotal, long bytesTotal,
        string stage = "", Func<TimeSpan>? clock = null)
    {
        _progress = progress;
        _filesTotal = filesTotal;
        _bytesTotal = bytesTotal;
        _stage = stage;
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _elapsed = () => stopwatch.Elapsed;
        }
        else
        {
            _elapsed = clock;
        }
    }

    public void Report(IArchiveEntry entry, long bytesWritten)
    {
        _filesDone++;
        _bytesWritten += bytesWritten;
        if (_progress is null)
            return;

        var now = _elapsed();
        var dueByTime = !_hasReported || now - _lastReportedAt >= CoalesceWindow;
        var largeEntry = entry.Size >= LargeEntryThresholdBytes;
        if (!dueByTime && !largeEntry)
            return;

        _hasReported = true;
        _lastReportedAt = now;
        ReportUpdate($"{_stage} {entry.Key ?? string.Empty}");
    }

    /// <summary>
    /// Reports cumulative progress for work that is not organized per-entry (e.g. hashing a single
    /// archive). The caller owns <paramref name="filesDone"/>/<paramref name="filesTotal"/>/
    /// <paramref name="bytesWritten"/> and the <paramref name="label"/> shown to the user. The same
    /// coalesce window applies: at most one update is forwarded per <see cref="CoalesceWindow"/>.
    /// </summary>
    public void Report(int filesDone, int filesTotal, long bytesWritten, long bytesTotal, string label)
    {
        _filesDone = filesDone;
        _bytesWritten = bytesWritten;
        if (_progress is null)
            return;

        var now = _elapsed();
        if (_hasReported && now - _lastReportedAt < CoalesceWindow)
            return;

        _hasReported = true;
        _lastReportedAt = now;
        _progress.Report(new ExtractionProgress(filesDone, filesTotal, bytesWritten, bytesTotal, label));
    }

    public void Finish()
    {
        ReportUpdate(_stage);
    }

    /// <summary>
    /// Emits the terminal state for work that is not organized per-entry (e.g. hashing). Unlike
    /// <see cref="Report(int, int, long, long, string)"/> this always forwards, so observers reliably
    /// see the completed state even if it falls inside the coalesce window.
    /// </summary>
    public void Finish(int filesDone, int filesTotal, long bytesWritten, long bytesTotal, string label)
    {
        _progress?.Report(new ExtractionProgress(filesDone, filesTotal, bytesWritten, bytesTotal, label));
    }

    private void ReportUpdate(string detail)
    {
        _progress?.Report(new ExtractionProgress(_filesDone, _filesTotal, _bytesWritten, _bytesTotal, detail));
    }
}
