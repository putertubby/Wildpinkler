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

    public LaunchService(ProfileConfigExporter exporter, ProfileFolderProvisioner provisioner)
    {
        _exporter = exporter;
        _provisioner = provisioner;
    }

    /// <summary>Raised once a tool run finished and its output version was promoted, so the profile can be saved.</summary>
    public event Action<Profile>? ToolRunCompleted;

    public string LoaderPath => Path.Combine(AppContext.BaseDirectory, LoaderFileName);

    public async Task<string> LaunchAsync(Profile profile, LaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ExecutablePath))
            throw new InvalidOperationException($"'{target.DisplayName}' has no executable configured.");

        if (!File.Exists(target.ExecutablePath))
            throw new FileNotFoundException($"'{target.ExecutablePath}' does not exist.", target.ExecutablePath);

        var binding = target.IsGame || !target.ProducesOutput
            ? null
            : profile.Tools.FirstOrDefault(item => item.ToolEntryId == target.Id);

        // The pending version must exist before the config is exported: it is the target's top branch.
        if (binding is not null)
            _provisioner.BeginToolRun(profile, binding, target.Id);

        var configPath = await _exporter.ExportAsync(profile, target);

        if (!File.Exists(LoaderPath))
            throw new FileNotFoundException($"{LoaderFileName} was not found next to Wildpinkler.", LoaderPath);

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

        var process = Process.Start(startInfo);
        if (binding is not null && process is not null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                _provisioner.CompleteToolRun(profile, binding, target.Id);
                ToolRunCompleted?.Invoke(profile);
            };
        }

        return configPath;
    }
}
