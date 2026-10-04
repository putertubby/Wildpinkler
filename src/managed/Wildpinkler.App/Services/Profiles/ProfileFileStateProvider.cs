using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Answers a FOMOD fileDependency check against a profile's current, not-yet-modified load order -
/// the same folder set that resolves to the reserved load-order merged view, checked highest priority first.
/// A file present in an enabled folder is Active; present only in a disabled folder is Inactive;
/// absent everywhere is Missing.
/// </summary>
public sealed class ProfileFileStateProvider : IFomodFileStateProvider
{
    private readonly IReadOnlyList<ProfileFolder> _folders;

    public ProfileFileStateProvider(IReadOnlyList<ProfileFolder> folders) => _folders = folders;

    public FomodFileState GetState(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var foundInactive = false;
        foreach (var folder in _folders.Where(folder => folder.Path.Length > 0))
        {
            var candidate = Path.Combine(folder.Path, normalized);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                if (folder.IsEnabled)
                    return FomodFileState.Active;
                foundInactive = true;
            }
        }

        return foundInactive ? FomodFileState.Inactive : FomodFileState.Missing;
    }
}
