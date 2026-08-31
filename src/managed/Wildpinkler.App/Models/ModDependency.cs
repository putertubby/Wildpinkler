using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ModDependencyKind>))]
public enum ModDependencyKind
{
    /// <summary>The target mod must be present and enabled.</summary>
    Requires,

    /// <summary>The owning mod's folder must sit above the target's in load order (higher priority).</summary>
    LoadAfter,

    /// <summary>The owning mod's folder must sit below the target's in load order (lower priority).</summary>
    LoadBefore,

    /// <summary>The owning and target mods should not both be enabled at once.</summary>
    Conflicts,

    /// <summary>The owning mod requires a specific game executable version; <see cref="ModDependency.Target"/> is unused.</summary>
    GameVersion
}

/// <summary>
/// Identifies the other side of a <see cref="ModDependencyKind.Requires"/>/<see cref="ModDependencyKind.LoadAfter"/>/
/// <see cref="ModDependencyKind.LoadBefore"/>/<see cref="ModDependencyKind.Conflicts"/> edge. A requirement is often
/// known before the target mod exists locally, so <see cref="ModId"/> and <see cref="RemoteRef"/> are both optional;
/// <see cref="DisplayName"/> is always populated so the edge can be shown even when nothing resolves.
/// </summary>
public sealed record ModDependencyTarget(string? ModId, RemoteRef? RemoteRef, string DisplayName);

/// <summary>
/// A single-version or range game-version compatibility requirement, used only with
/// <see cref="ModDependencyKind.GameVersion"/>. Version strings are compared as opaque, dotted
/// numeric tuples since games and script extenders do not share one numbering scheme.
/// </summary>
public sealed record GameVersionConstraint(IReadOnlyList<string> ExactVersions, string? MinVersion, string? MaxVersion, string Label);

/// <summary>
/// One compatibility edge sourced from a mod's own data (FOMOD, plugin masters) or entered by hand.
/// Purely advisory: nothing in Wildpinkler blocks an install, reorder or launch because of one.
/// </summary>
public sealed class ModDependency
{
    public string Id { get; set; } = string.Empty;
    public string SourceModId { get; set; } = string.Empty;
    public ModDependencyKind Kind { get; set; }
    public ModDependencyTarget? Target { get; set; }
    public GameVersionConstraint? VersionConstraint { get; set; }

    /// <summary>Where this edge came from: "fomod", "plugin-master", or "manual".</summary>
    public string Origin { get; set; } = string.Empty;
}

/// <summary>Per-mod summary of the worst outstanding issue <c>DependencyGraphService</c> found for it, if any.</summary>
public enum DependencyState
{
    Ok,
    MissingRequirement,
    DisabledRequirement,
    OrderViolation,
    Cycle,
    GameVersionMismatch,
    Conflict
}
