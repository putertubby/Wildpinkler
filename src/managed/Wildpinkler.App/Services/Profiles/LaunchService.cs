using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>Exports a launch target's uufs64 configuration and starts it through the loader.</summary>
public sealed partial class LaunchService
{
    public const string LoaderFileName = "uufs64ldr.exe";

    private readonly ProfileConfigExporter _exporter;
    private readonly ProfileFolderService _provisioner;
    private readonly ActiveRunRegistry _runs;
    private readonly IProcessLauncher _processLauncher;
    private readonly string? _loaderPath;
    private readonly ILogger<LaunchService> _logger;

    public LaunchService(
        ProfileConfigExporter exporter,
        ProfileFolderService provisioner,
        ActiveRunRegistry runs,
        IProcessLauncher processLauncher,
        string? loaderPath = null,
        ILogger<LaunchService>? logger = null)
    {
        _exporter = exporter;
        _provisioner = provisioner;
        _runs = runs;
        _processLauncher = processLauncher;
        _loaderPath = loaderPath;
        _logger = logger ?? NullLogger<LaunchService>.Instance;
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

        // Correlates every log line of this launch (invocation, command line, completion) in the log sink.
        var runId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId });

        ProfileFolderService.PendingToolRun? pendingRun = null;
        try
        {
            // The pending version must exist before the config is exported: it is the target's top branch.
            if (binding is not null)
                pendingRun = _provisioner.BeginToolRun(profile, binding, target.Id);

            var configPath = await _exporter.ExportAsync(profile, target);

            if (!File.Exists(LoaderPath))
                throw new FileNotFoundException($"{LoaderFileName} was not found next to Wildpinkler.", LoaderPath);

            var startInfo = CreateStartInfo(target, configPath);
            LogInvocation(profile.Name, profile.Id, target.DisplayName, target.Id, target.Kind.ToString(), LoaderPath);
            LogCommandLine(BuildCommandLine(startInfo));
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
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(target.VirtualExecutablePath);
        startInfo.ArgumentList.Add("--curdir");
        startInfo.ArgumentList.Add(Path.GetDirectoryName(Path.GetFullPath(target.VirtualExecutablePath))!);
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

    /// <summary>Builds the full command line (loader path plus all arguments, each quoted only when needed) exactly as the loader will receive it.</summary>
    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var parts = new List<string> { QuoteIfNeeded(startInfo.FileName) };
        parts.AddRange(startInfo.ArgumentList.Select(QuoteIfNeeded));
        return string.Join(" ", parts);
    }

    /// <summary>Applies Windows command-line quoting to a single argument, matching what <see cref="ProcessStartInfo.ArgumentList"/> hands to the process.</summary>
    private static string QuoteIfNeeded(string argument)
    {
        if (argument.Length == 0 || argument.Contains(' ', StringComparison.Ordinal) || argument.Contains('"', StringComparison.Ordinal))
        {
            return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }
        return argument;
    }

    private async Task<LaunchCompletion> ObserveCompletionAsync(
        ILaunchedProcess process,
        ActiveRun run,
        Profile profile,
        LaunchTarget target,
        ProfileTool? binding,
        ProfileFolderService.PendingToolRun? pendingRun)
    {
        var succeeded = false;
        int? exitCode = null;
        try
        {
            using (process)
            {
                exitCode = await process.WaitForExitAsync();
                succeeded = exitCode == 0;
            }

            if (succeeded && binding is not null && pendingRun is not null)
                _provisioner.CompleteToolRun(profile, binding, target.Id, pendingRun);
        }
        catch
        {
            exitCode = null;
            succeeded = false;
        }
        finally
        {
            _runs.Release(run);
        }

        LogCompletion(
            profile.Name,
            profile.Id,
            target.DisplayName,
            target.Id,
            target.Kind.ToString(),
            exitCode);

        var completion = new LaunchCompletion(profile, target, succeeded, pendingRun);
        LaunchCompleted?.Invoke(completion);
        return completion;
    }

    /// <summary>
    /// Every relevant detail of the loader invocation as discrete structured fields, plus the full
    /// command line (each argument quoted exactly as the loader receives it).
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Starting '{ProfileName}' ({ProfileId}) -> '{TargetName}' ({TargetId}, {Kind}) via loader {LoaderPath}")]
    private partial void LogInvocation(
        string profileName,
        string profileId,
        string targetName,
        string targetId,
        string kind,
        string loaderPath);

    /// <summary>The full command line handed to the loader, with each argument quoted as the process will receive it.</summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loader command line: {CommandLine}")]
    private partial void LogCommandLine(string commandLine);

    /// <summary>The outcome of the loader run. A null <paramref name="exitCode"/> means the process could not be observed to exit normally.</summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loader for '{ProfileName}' ({ProfileId}) target '{TargetName}' ({TargetId}, {Kind}) exited with code {ExitCode}")]
    private partial void LogCompletion(
        string profileName,
        string profileId,
        string targetName,
        string targetId,
        string kind,
        int? exitCode);

    private sealed record StartedLaunch(string ConfigPath, Task<LaunchCompletion> Completion);
}

/// <summary>The final result of one loader-backed target run.</summary>
public sealed record LaunchCompletion(
    Profile Profile,
    LaunchTarget Target,
    bool Succeeded,
    ProfileFolderService.PendingToolRun? PendingToolRun);
