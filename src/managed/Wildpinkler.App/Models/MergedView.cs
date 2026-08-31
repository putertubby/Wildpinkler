using System.Collections.Generic;

namespace Wildpinkler.App.Models;

/// <summary>
/// A union-filesystem mount: an ordered stack of branch paths (first = highest priority) merged at
/// <see cref="MountPath"/>, optionally writable (gets an upperdir). <see cref="Name"/> is a purely
/// cosmetic, non-unique label; the real merge/override identity across layers is the resolved,
/// normalized <see cref="MountPath"/>. Both <see cref="MountPath"/> and each branch are relative to
/// the owning entity's install/tool folder by default, or absolute when built from read-only system
/// folder variables - never a {VARIABLE} placeholder left unresolved past the entity that defined it.
/// </summary>
public sealed class MergedView
{
    public string Name { get; set; } = string.Empty;
    public string MountPath { get; set; } = string.Empty;
    public List<string> Branches { get; set; } = new();
    public bool IsWritable { get; set; }

    public MergedView Clone() => new()
    {
        Name = Name,
        MountPath = MountPath,
        Branches = new List<string>(Branches),
        IsWritable = IsWritable
    };
}
