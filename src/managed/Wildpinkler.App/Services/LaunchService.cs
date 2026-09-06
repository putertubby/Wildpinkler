using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>Exports a launch target's uufs64 configuration and starts it through the loader.</summary>
public sealed class LaunchService
{
    public const string LoaderFileName = "uufs64ldr.exe";

    private readonly ProfileConfigExporter _exporter;
    private readonly ProfileFolderProvisioner _provisioner;
    private readonly ActiveRunRegistry _runs;
    private readonly IProcessLauncher _processLauncher;
    private readonly string? _loaderPath;

    public LaunchService(
        ProfileConfigExporter exporter,
        ProfileFolderProvisioner provisioner,
        ActiveRunRegistry runs,
        IProcessLauncher processLauncher,
        string? loaderPath = null)
    {
        _exporter = exporter;
        _provisioner = provisioner;
        _runs = runs;
        _processLauncher = processLauncher;
        _loaderPath = loaderPath;
    }

    /// <summary>Raised after the loader exits and any captured tool output is finalized.</summary>
    public event Action<LaunchCompletion>? LaunchCompleted;

    public string LoaderPath => _loaderPath ?? Path.Combine(AppContext.BaseDirectory, LoaderFileName);

    public async Task<string> LaunchAsync(Profile profile, LaunchTarget target)
    {
        var launched = await StartAsync(profile, target);
        _ = launched.Completion;
        return launched.ConfigPath;
    }

    public async Task<LaunchCompletion> LaunchAndWaitAsync(Profile profile, LaunchTarget target)
    {
        var launched = await StartAsync(profile, target);
        return await launched.Completion;
    }

    private async Task<StartedLaunch> StartAsync(Profile profile, LaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ExecutablePath))
            throw new InvalidOperationException($"'{target.DisplayName}' has no executable configured.");

        if (!File.Exists(target.ExecutablePath))
            throw new FileNotFoundException($"'{target.ExecutablePath}' does not exist.", target.ExecutablePath);

        var binding = target.IsGame || !target.ProducesOutput
            ? null
            : profile.Tools.FirstOrDefault(item => item.ToolEntryId == target.Id);
        if (!_runs.TryReserve(profile, target, out var run))
            throw new InvalidOperationException($"'{profile.Name}' already has a target running.");

        ProfileFolderProvisioner.PendingToolRun? pendingRun = null;
        try
        {
            // The pending version must exist before the config is exported: it is the target's top branch.
            if (binding is not null)
                pendingRun = _provisioner.BeginToolRun(profile, binding, target.Id);

            var configPath = await _exporter.ExportAsync(profile, target);

            if (!File.Exists(LoaderPath))
                throw new FileNotFoundException($"{LoaderFileName} was not found next to Wildpinkler.", LoaderPath);

            var startInfo = CreateStartInfo(target, configPath);
            var process = _processLauncher.Start(startInfo);
            _runs.MarkRunning(run);
            return new StartedLaunch(configPath, ObserveCompletionAsync(process, run, profile, target, binding, pendingRun));
        }
        catch
        {
            if (pendingRun is not null)
                _provisioner.AbandonToolRun(pendingRun);
            _runs.Release(run);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(LaunchTarget target, string configPath)
    {
        var startInfo = new ProcessStartInfo(LoaderPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(target.WorkingDirectory)
                ? target.WorkingDirectory
                : Path.GetDirectoryName(target.ExecutablePath) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(target.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(target.Arguments))
        {
            startInfo.ArgumentList.Add("--args");
            startInfo.ArgumentList.Add(target.Arguments);
        }
        if (!string.IsNullOrWhiteSpace(target.SteamGameId))
        {
            startInfo.ArgumentList.Add("--steamid");
            startInfo.ArgumentList.Add(target.SteamGameId);
        }
        startInfo.ArgumentList.Add(configPath);
        return startInfo;
    }

    private async Task<LaunchCompletion> ObserveCompletionAsync(
        ILaunchedProcess process,
        ActiveRun run,
        Profile profile,
        LaunchTarget target,
        ProfileTool? binding,
        ProfileFolderProvisioner.PendingToolRun? pendingRun)
    {
        var succeeded = false;
        try
        {
            using (process)
            {
                succeeded = await process.WaitForExitAsync() == 0;
            }

            if (succeeded && binding is not null && pendingRun is not null)
                _provisioner.CompleteToolRun(profile, binding, target.Id, pendingRun);
        }
        catch
        {
            succeeded = false;
        }
        finally
        {
            _runs.Release(run);
        }

        var completion = new LaunchCompletion(profile, target, succeeded, pendingRun);
        LaunchCompleted?.Invoke(completion);
        return completion;
    }

    private sealed record StartedLaunch(string ConfigPath, Task<LaunchCompletion> Completion);
}

/// <summary>The final result of one loader-backed target run.</summary>
public sealed record LaunchCompletion(
    Profile Profile,
    LaunchTarget Target,
    bool Succeeded,
    ProfileFolderProvisioner.PendingToolRun? PendingToolRun);
