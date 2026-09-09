using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Writes the uufs64 configuration for a launch target: one file per target inside the profile
/// folder, in the exact shape uufs64 reads - top-level <c>variables</c> and <c>mountpoints</c> only.
/// A LOCAL/user variable is never exported; nor is a <see cref="SystemVariables.ReservedNames"/>
/// FOLDERID_* placeholder, since uufs64 already knows those internally. Only a profile-computed
/// built-in (InstallPath, ProfilePath - see <see cref="VariableScope.Symbolize"/>) is kept, since a
/// mount root or branch may still reference one as a literal ${name} placeholder to stay portable
/// across a moved profile.
/// </summary>
public sealed class ProfileConfigExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> ExportAsync(Profile profile, LaunchTarget target)
    {
        Directory.CreateDirectory(profile.FolderPath);
        var path = Path.Combine(profile.FolderPath, target.ConfigFileName);

        var document = new ConfigDocument(BuildVariables(target), BuildMountpoints(target));

        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
            await stream.FlushAsync();
        }

        File.Move(temporaryPath, path, true);
        return path;
    }

    /// <summary>Only the profile-computed built-ins (never a FOLDERID_* system folder, which uufs64 already knows), plus the fixed `workingDirectory` key - targetPath/arguments/steamGameId go to uufs64ldr as --target/--args/--steamid instead. Never an arbitrary user/profile/tool variable.</summary>
    private static Dictionary<string, string> BuildVariables(LaunchTarget target)
    {
        var variables = target.Variables
            .Where(item => target.BuiltInVariableNames.Contains(item.Key) && !SystemVariables.ReservedNames.Contains(item.Key))
            .OrderBy(item => item.Key)
            .ToDictionary(item => item.Key, item => item.Value);

        variables["workingDirectory"] = Symbolize(target.VirtualWorkingDirectory, target);

        return variables;
    }

    private static List<ConfigMountpoint> BuildMountpoints(LaunchTarget target) =>
        target.MergedViews.Select(view => new ConfigMountpoint(
            view.Name,
            Symbolize(view.MountPath, target),
            view.Branches.Select(branch => Symbolize(branch, target)).ToList(),
            view.IsWritable)).ToList();

    private static string Symbolize(string resolvedPath, LaunchTarget target) =>
        VariableScope.Symbolize(resolvedPath, target.Variables, target.BuiltInVariableNames);

    private sealed record ConfigMountpoint(string Name, string Root, IReadOnlyList<string> Branches, bool Writable);

    private sealed record ConfigDocument(Dictionary<string, string> Variables, List<ConfigMountpoint> Mountpoints);
}
