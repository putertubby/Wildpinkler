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

    public async Task<ModInstallation> FindOrCreateFomodInstallationAsync(
        ModEntry mod, IReadOnlyList<FomodFileInstall> resolvedFiles, string signature, string selectionSummary,
        IReadOnlyList<FomodStepSelection> selections,
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
                .ToList()
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
            (root, token) => Task.Run(() => ExtractFomodFiles(mod.ArchivePath, resolvedFiles, root, progress, token), token),
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
        var signature = engine.ComputeSelectionSignature(selections);
        return await FindOrCreateFomodInstallationAsync(
            mod, resolvedFiles, signature, DescribeSelections(fomod), selections, progress, cancellationToken);
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

    // Applied in the caller's priority-ascending order, so a later entry legitimately overwrites an earlier one.
    private void ExtractFomodFiles(
        string archivePath, IReadOnlyList<FomodFileInstall> installs, string destinationRoot,
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
            if (install.IsFolder)
            {
                var prefix = source.Length == 0 ? string.Empty : source + "/";
                for (var i = FindKeyStart(index, prefix);
                    i < index.Count && index[i].Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    i++)
                {
                    var relative = index[i].Key[prefix.Length..];
                    if (relative.Length == 0)
                        continue;

                    work.Add((index[i].Entry, CombineRelative(install.Destination, relative)));
                }
            }
            else
            {
                var i = FindKeyStart(index, source);
                if (i < index.Count && index[i].Key.Equals(source, StringComparison.OrdinalIgnoreCase))
                    work.Add((index[i].Entry, install.Destination.Length > 0 ? install.Destination : Path.GetFileName(source)));
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
    private static int FindKeyStart(List<(IArchiveEntry Entry, string Key)> index, string key)
    {
        var low = 0;
        var high = index.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (string.Compare(index[mid].Key, key, StringComparison.OrdinalIgnoreCase) < 0)
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
