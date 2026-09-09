using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed class ModListBuildCoordinator
{
    private readonly ModListBuildStore _builds;
    private readonly ModListPreflightService _preflight;
    private readonly ProfileFolderService _provisioner;
    private readonly RemoteArchiveAcquisitionService _acquisition;
    private readonly ModInstallService _installer;
    private readonly ModStore _mods;
    private readonly ModInstallationStore _installations;
    private readonly ProfileStore _profiles;
    private readonly ToolStore _toolStore;
    private readonly LaunchTargetResolver _targetResolver;
    private readonly LaunchService _launcher;
    private readonly DependencyGraphService _dependencies;

    public ModListBuildCoordinator(
        ModListBuildStore builds,
        ModListPreflightService preflight,
        ProfileFolderService provisioner,
        RemoteArchiveAcquisitionService acquisition,
        ModInstallService installer,
        ModStore mods,
        ModInstallationStore installations,
        ProfileStore profiles,
        ToolStore toolStore,
        LaunchTargetResolver targetResolver,
        LaunchService launcher,
        DependencyGraphService dependencies)
    {
        _builds = builds;
        _preflight = preflight;
        _provisioner = provisioner;
        _acquisition = acquisition;
        _installer = installer;
        _mods = mods;
        _installations = installations;
        _profiles = profiles;
        _toolStore = toolStore;
        _targetResolver = targetResolver;
        _launcher = launcher;
        _dependencies = dependencies;
    }

    public event EventHandler<ModListBuild>? BuildChanged;

    public async Task<ModListBuild> CreateAsync(
        ModListManifest manifest,
        string profileName,
        GameEntry game,
        IReadOnlyList<ToolEntry> tools,
        CancellationToken cancellationToken = default)
    {
        var mods = await _mods.LoadAsync();
        var installations = await _installations.LoadAsync();
        var plan = _preflight.Evaluate(manifest, game, mods, installations, tools);
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = profileName.Trim(),
            GameId = game.Id,
            GameName = game.Name,
            Variables = new Dictionary<string, string>(manifest.Profile.Variables),
            MergedViews = manifest.Profile.MergedViews.Select(view => view.Clone()).ToList()
        };
        _provisioner.Provision(profile, game);
        var build = new ModListBuild
        {
            Id = Guid.NewGuid().ToString("N"),
            ListId = manifest.ListId,
            ListRevision = manifest.Revision,
            ProfileName = profile.Name,
            GameId = game.Id,
            State = plan.CanStart ? ModListBuildState.Ready : ModListBuildState.Blocked,
            StagedProfile = profile,
            Tasks = plan.Tasks.ToList(),
            Artifacts = plan.Artifacts.ToList()
        };
        if (!plan.CanStart)
        {
            var validation = build.Tasks.Single(task => task.Kind == ModListBuildTaskKind.Validate);
            validation.State = ModListBuildTaskState.Blocked;
            validation.Error = string.Join(" ", plan.BlockingIssues);
        }
        await SaveAsync(build, cancellationToken);
        return build;
    }

    public async Task ResumeAsync(
        ModListBuild build,
        ModListManifest manifest,
        GameEntry game,
        IReadOnlyList<ToolEntry> tools,
        CancellationToken cancellationToken = default)
    {
        if (build.State is ModListBuildState.Completed or ModListBuildState.Discarded or ModListBuildState.Blocked)
            return;
        build.State = ModListBuildState.Running;
        await SaveAsync(build, cancellationToken);

        try
        {
            foreach (var task in build.Tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (task.State is ModListBuildTaskState.Completed or ModListBuildTaskState.Skipped)
                    continue;
                if (task.State is ModListBuildTaskState.NeedsUser or ModListBuildTaskState.Blocked)
                {
                    build.State = task.State == ModListBuildTaskState.Blocked ? ModListBuildState.Blocked : ModListBuildState.NeedsUser;
                    await SaveAsync(build, cancellationToken);
                    return;
                }

                var canContinue = await ExecuteAutomaticTaskAsync(build, task, manifest, game, tools, cancellationToken);
                if (!canContinue)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            build.State = ModListBuildState.Ready;
            await SaveAsync(build, CancellationToken.None);
            throw;
        }
    }

    public async Task AcceptArchiveAsync(
        ModListBuild build, ModListManifest manifest, string entryId, string sourcePath, CancellationToken cancellationToken = default)
    {
        var requirement = GetMod(manifest, entryId);
        await using (var stream = File.OpenRead(sourcePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, requirement.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected archive does not match the mod list's SHA-256.");
        }
        var entry = new ModEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = requirement.Name,
            Version = requirement.Version ?? string.Empty,
            Game = manifest.Game.DefinitionId,
            FileName = requirement.Archive.FileName,
            Md5 = requirement.Archive.Md5,
            FileSize = requirement.Archive.SizeInBytes,
            Source = "Mod list"
        };
        await _mods.AddArchiveAsync(entry, sourcePath);
        await _mods.UpsertAsync(entry);
        SetArchiveArtifact(build, entryId, entry);
        Complete(build, $"acquire:{entryId}", "Verified manual archive.");
        await SaveAsync(build, cancellationToken);
    }

    public async Task AcceptFolderAsync(ModListBuild build, string entryId, string folderPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("The selected folder does not exist.");
        var artifact = build.Artifacts.FirstOrDefault(item => item.EntryId == entryId);
        if (artifact is null)
        {
            artifact = new ModListBuildArtifact { EntryId = entryId };
            build.Artifacts.Add(artifact);
        }
        artifact.FolderPath = folderPath;
        Complete(build, $"folder:{entryId}", "Folder selected.");
        await SaveAsync(build, cancellationToken);
    }

    public async Task AcceptInstalledFolderAsync(ModListBuild build, string entryId, string folderPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("The selected installed-mod folder does not exist.");
        var artifact = build.Artifacts.Single(item => item.EntryId == entryId);
        artifact.FolderPath = folderPath;
        Complete(build, $"install:{entryId}", "Installed folder selected.");
        build.State = ModListBuildState.Ready;
        await SaveAsync(build, cancellationToken);
    }

    public async Task GrantToolConsentAsync(ModListBuild build, CancellationToken cancellationToken = default)
    {
        build.ToolConsentGranted = true;
        Complete(build, "tools:consent", "Approved for this build.");
        await SaveAsync(build, cancellationToken);
    }

    public async Task RunToolAsync(
        ModListBuild build,
        ModListManifest manifest,
        GameEntry game,
        IReadOnlyList<ToolEntry> tools,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!build.ToolConsentGranted)
            throw new InvalidOperationException("Approve this build's tool invocations first.");
        EnsureProfileAssembled(build, manifest, tools);
        var task = build.Tasks.Single(candidate => candidate.Id == taskId && candidate.Kind == ModListBuildTaskKind.ToolInvocation);
        var requirement = manifest.Tools.Single(candidate => candidate.RequirementId == task.EntryId);
        var tool = tools.Single(candidate => string.Equals(candidate.DefinitionId, requirement.DefinitionId, StringComparison.OrdinalIgnoreCase));
        var target = _targetResolver.Resolve(build.StagedProfile, game, tools).Single(candidate => candidate.Id == tool.Id);
        task.State = ModListBuildTaskState.Running;
        await SaveAsync(build, cancellationToken);
        var completion = await _launcher.LaunchAndWaitAsync(build.StagedProfile, target);
        if (!completion.Succeeded)
        {
            Fail(build, task, "The tool exited unsuccessfully.");
            await SaveAsync(build, cancellationToken);
            return;
        }
        Complete(task, "Tool completed successfully.");
        build.State = ModListBuildState.Ready;
        await SaveAsync(build, cancellationToken);
    }

    public async Task RetryAsync(ModListBuild build, string taskId, CancellationToken cancellationToken = default)
    {
        var task = build.Tasks.Single(candidate => candidate.Id == taskId);
        task.State = ModListBuildTaskState.Pending;
        task.Error = null;
        task.StatusText = "Ready to retry.";
        build.State = ModListBuildState.Ready;
        await SaveAsync(build, cancellationToken);
    }

    public async Task CompleteUserTaskAsync(ModListBuild build, string taskId, string status, CancellationToken cancellationToken = default)
    {
        var task = build.Tasks.Single(candidate => candidate.Id == taskId);
        if (task.State != ModListBuildTaskState.NeedsUser)
            throw new InvalidOperationException("This task is not waiting for user completion.");
        Complete(task, status);
        build.State = ModListBuildState.Ready;
        await SaveAsync(build, cancellationToken);
    }

    public async Task AcknowledgeAdvisoryIssuesAsync(ModListBuild build, CancellationToken cancellationToken = default)
    {
        var task = build.Tasks.Single(candidate => candidate.Kind == ModListBuildTaskKind.Validate);
        if (task.State != ModListBuildTaskState.NeedsUser)
            throw new InvalidOperationException("Validation is not waiting for acknowledgment.");
        build.AdvisoryIssuesAcknowledged = true;
        task.State = ModListBuildTaskState.Pending;
        task.Error = null;
        task.StatusText = "Advisory findings acknowledged; ready to validate again.";
        build.State = ModListBuildState.Ready;
        await SaveAsync(build, cancellationToken);
    }

    public async Task DiscardAsync(ModListBuild build, CancellationToken cancellationToken = default)
    {
        _provisioner.Delete(build.StagedProfile);
        build.State = ModListBuildState.Discarded;
        await _builds.DeleteAsync(build.Id, cancellationToken);
        BuildChanged?.Invoke(this, build);
    }

    private async Task<bool> ExecuteAutomaticTaskAsync(
        ModListBuild build, ModListBuildTask task, ModListManifest manifest, GameEntry game,
        IReadOnlyList<ToolEntry> tools, CancellationToken cancellationToken)
    {
        task.State = ModListBuildTaskState.Running;
        task.Error = null;
        await SaveAsync(build, cancellationToken);
        try
        {
            switch (task.Kind)
            {
                case ModListBuildTaskKind.AcquireArchive:
                    await AcquireAsync(build, task, manifest, cancellationToken);
                    break;
                case ModListBuildTaskKind.InstallMod:
                    await InstallAsync(build, task, manifest, cancellationToken);
                    break;
                case ModListBuildTaskKind.ToolInvocation:
                    var (requirement, invocation) = FindInvocation(manifest, task.Id);
                    if (invocation.Mode == ModListToolInvocationMode.TrackedManual)
                    {
                        task.State = ModListBuildTaskState.NeedsUser;
                        task.StatusText = "Launch when ready; completion is tracked.";
                        build.State = ModListBuildState.NeedsUser;
                        await SaveAsync(build, cancellationToken);
                        return false;
                    }
                    if (!build.ToolConsentGranted)
                        throw new InvalidOperationException("Tool invocations have not been approved for this build.");
                    EnsureProfileAssembled(build, manifest, tools);
                    var automaticTool = tools.Single(candidate => string.Equals(candidate.DefinitionId, requirement.DefinitionId, StringComparison.OrdinalIgnoreCase));
                    var automaticTarget = _targetResolver.Resolve(build.StagedProfile, game, tools).Single(candidate => candidate.Id == automaticTool.Id);
                    var automaticCompletion = await _launcher.LaunchAndWaitAsync(build.StagedProfile, automaticTarget);
                    if (!automaticCompletion.Succeeded)
                        throw new InvalidOperationException("The tool exited unsuccessfully.");
                    Complete(task, "Tool completed successfully.");
                    break;
                case ModListBuildTaskKind.Validate:
                    await ValidateAsync(build, manifest, game, tools);
                    break;
                case ModListBuildTaskKind.Commit:
                    await CommitAsync(build, cancellationToken);
                    break;
                default:
                    Complete(task, task.StatusText);
                    break;
            }
            await SaveAsync(build, cancellationToken);
            return build.State is not (ModListBuildState.NeedsUser or ModListBuildState.Blocked or ModListBuildState.Failed);
        }
        catch (RemoteSiteException exception) when (exception.Kind is RemoteErrorKind.PremiumRequired or RemoteErrorKind.KeyExpired)
        {
            task.State = ModListBuildTaskState.NeedsUser;
            task.Error = exception.Message;
            task.StatusText = exception.Remedy ?? "Open the mod page and complete the download manually.";
            build.State = ModListBuildState.NeedsUser;
            await SaveAsync(build, cancellationToken);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(build, task, exception.Message);
            await SaveAsync(build, cancellationToken);
            return false;
        }
    }

    private async Task AcquireAsync(ModListBuild build, ModListBuildTask task, ModListManifest manifest, CancellationToken cancellationToken)
    {
        var mod = GetMod(manifest, task.EntryId!);
        if (mod.Source is null)
            throw new InvalidOperationException("This archive requires manual acquisition.");
        var link = RemoteLink.ForModFile(mod.Source.SiteId, mod.Source.GameKey, mod.Source.ModKey, mod.Source.FileKey!);
        var progress = new Progress<RemoteAcquisitionProgress>(update =>
        {
            task.Progress = update.TotalBytes is > 0 ? (double)update.BytesDownloaded / update.TotalBytes.Value : 0;
            task.StatusText = update.Phase.ToString();
        });
        var result = await _acquisition.AcquireAsync(link, mod.Archive.Sha256, progress, cancellationToken);
        SetArchiveArtifact(build, mod.EntryId, result.Entry);
        Complete(task, "Archive downloaded and verified.");
    }

    private async Task InstallAsync(ModListBuild build, ModListBuildTask task, ModListManifest manifest, CancellationToken cancellationToken)
    {
        var requirement = GetMod(manifest, task.EntryId!);
        var artifact = build.Artifacts.Single(item => item.EntryId == requirement.EntryId);
        var mod = (await _mods.LoadAsync()).Single(item => item.Id == artifact.ModId);
        var installation = await _installer.FindOrCreateFromRecipeAsync(mod, requirement.Installation, build.StagedProfile.LoadOrder.ToList());
        artifact.InstallationId = installation.Id;
        artifact.FolderPath = installation.FolderPath;
        Complete(task, "Installation is ready.");
    }

    private async Task ValidateAsync(ModListBuild build, ModListManifest manifest, GameEntry game, IReadOnlyList<ToolEntry> tools)
    {
        EnsureProfileAssembled(build, manifest, tools);
        var incomplete = build.Tasks.Where(task => task.Kind is not (ModListBuildTaskKind.Validate or ModListBuildTaskKind.Commit) &&
                                                   task.IsRequired && task.State is not (ModListBuildTaskState.Completed or ModListBuildTaskState.Skipped)).ToList();
        if (incomplete.Count > 0)
            throw new InvalidOperationException("Required build tasks are not complete.");
        var mods = await _mods.LoadAsync();
        var issues = _dependencies.Evaluate(build.StagedProfile, mods, game);
        if (issues.Any(issue => issue.Kind is DependencyIssueKind.MissingRequirement or DependencyIssueKind.DisabledRequirement or DependencyIssueKind.Cycle or DependencyIssueKind.GameVersionMismatch))
            throw new InvalidOperationException(string.Join(" ", issues.Select(issue => issue.Message)));
        if (issues.Count > 0 && !build.AdvisoryIssuesAcknowledged)
        {
            var validationTask = build.Tasks.Single(task => task.Kind == ModListBuildTaskKind.Validate);
            validationTask.State = ModListBuildTaskState.NeedsUser;
            validationTask.Error = string.Join(" ", issues.Select(issue => issue.Message));
            validationTask.StatusText = $"Review and acknowledge {issues.Count} advisory issue(s).";
            build.State = ModListBuildState.NeedsUser;
            return;
        }
        Complete(build.Tasks.Single(task => task.Kind == ModListBuildTaskKind.Validate),
            issues.Count == 0 ? "Profile requirements are satisfied." : $"Validated with {issues.Count} advisory issue(s).");
    }

    private async Task CommitAsync(ModListBuild build, CancellationToken cancellationToken)
    {
        var profiles = (await _profiles.LoadAsync()).ToList();
        if (profiles.All(profile => profile.Id != build.StagedProfile.Id))
            profiles.Add(build.StagedProfile);
        await _profiles.SaveAsync(profiles);

        var mods = (await _mods.LoadAsync()).ToList();
        var usedIds = build.StagedProfile.LoadOrder.Select(folder => folder.ModId).Where(id => id is not null).ToHashSet();
        foreach (var mod in mods.Where(mod => usedIds.Contains(mod.Id) && !mod.ProfileIds.Contains(build.StagedProfile.Id)))
            mod.ProfileIds = mod.ProfileIds.Append(build.StagedProfile.Id).ToList();
        await _mods.SaveAsync(mods);
        Complete(build.Tasks.Single(task => task.Kind == ModListBuildTaskKind.Commit), "Profile published.");
        build.State = ModListBuildState.Completed;
    }

    private void EnsureProfileAssembled(ModListBuild build, ModListManifest manifest, IReadOnlyList<ToolEntry> tools)
    {
        var profile = build.StagedProfile;
        foreach (var content in manifest.Content.OrderBy(entry => entry.Order))
        {
            if (profile.LoadOrder.Any(folder => folder.Id == $"build:{content.EntryId}"))
                continue;
            var artifact = build.Artifacts.FirstOrDefault(item => item.EntryId == content.EntryId);
            if (string.IsNullOrWhiteSpace(artifact?.FolderPath))
                continue;
            var folder = new ProfileFolder
            {
                Id = $"build:{content.EntryId}",
                Name = content.Name,
                Path = artifact.FolderPath,
                Kind = content is ModListModEntry ? ProfileFolderKind.Mod : ProfileFolderKind.Unmanaged,
                ModId = artifact.ModId,
                ModInstallationId = artifact.InstallationId,
                IsEnabled = content.IsEnabled,
                LauncherExecutableRelativePath = (content as ModListModEntry)?.LauncherExecutableRelativePath
            };
            var gameIndex = profile.LoadOrder.ToList().FindIndex(item => item.Kind == ProfileFolderKind.GameInstall);
            profile.LoadOrder.Insert(gameIndex, folder);
        }

        foreach (var requirement in manifest.Tools.Where(requirement => requirement.IsEnabled && requirement.DefinitionId is not null))
        {
            var tool = tools.FirstOrDefault(candidate => string.Equals(candidate.DefinitionId, requirement.DefinitionId, StringComparison.OrdinalIgnoreCase));
            if (tool is null || profile.Tools.Any(binding => binding.ToolEntryId == tool.Id))
                continue;
            var binding = new ProfileTool
            {
                ToolEntryId = tool.Id,
                IsEnabled = true,
                LaunchArgumentsOverride = requirement.LaunchArgumentsOverride,
                UseOutputOverlay = requirement.UseOutputOverlay,
                VariableOverrides = new Dictionary<string, string>(requirement.VariableOverrides),
                MergedViewOverrides = requirement.MergedViewOverrides.Select(view => view.Clone()).ToList()
            };
            profile.Tools.Add(binding);
            if (tool.Definition?.ProducesOutput == true)
                _provisioner.EnableTool(profile, binding, tool);
        }
    }

    private static ModListModEntry GetMod(ModListManifest manifest, string entryId) =>
        manifest.Content.OfType<ModListModEntry>().Single(mod => mod.EntryId == entryId);

    private static (ModListToolRequirement Requirement, ModListToolInvocation Invocation) FindInvocation(ModListManifest manifest, string taskId)
    {
        foreach (var requirement in manifest.Tools)
        foreach (var invocation in requirement.Invocations)
        {
            if (taskId == $"invoke:{requirement.RequirementId}:{invocation.InvocationId}")
                return (requirement, invocation);
        }
        throw new InvalidOperationException("The tool invocation is not present in the manifest.");
    }

    private static void SetArchiveArtifact(ModListBuild build, string entryId, ModEntry entry)
    {
        var artifact = build.Artifacts.Single(item => item.EntryId == entryId);
        artifact.ModId = entry.Id;
        artifact.ArchivePath = entry.ArchivePath;
    }

    private static void Complete(ModListBuild build, string taskId, string status) =>
        Complete(build.Tasks.Single(task => task.Id == taskId), status);

    private static void Complete(ModListBuildTask task, string status)
    {
        task.State = ModListBuildTaskState.Completed;
        task.Progress = 1;
        task.Error = null;
        task.StatusText = status;
    }

    private static void Fail(ModListBuild build, ModListBuildTask task, string error)
    {
        task.State = ModListBuildTaskState.Failed;
        task.Error = error;
        task.StatusText = "Resolve the error and retry.";
        build.State = ModListBuildState.Failed;
    }

    private async Task SaveAsync(ModListBuild build, CancellationToken cancellationToken)
    {
        await _builds.UpsertAsync(build, cancellationToken);
        BuildChanged?.Invoke(this, build);
    }
}
