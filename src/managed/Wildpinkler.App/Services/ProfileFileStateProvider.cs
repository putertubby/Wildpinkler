using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Answers a FOMOD fileDependency check against a profile's current, not-yet-modified load order -
/// the same folder set that resolves to the reserved load-order merged view, checked highest priority first.
/// </summary>
public sealed class ProfileFileStateProvider : IFomodFileStateProvider
{
    private readonly IReadOnlyList<ProfileFolder> _folders;

    public ProfileFileStateProvider(IReadOnlyList<ProfileFolder> folders) => _folders = folders;

    public bool Exists(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var folder in _folders.Where(folder => folder.IsEnabled && folder.Path.Length > 0))
        {
            var candidate = Path.Combine(folder.Path, normalized);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return true;
        }

        return false;
    }
}
