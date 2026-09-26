using System;

namespace Wildpinkler.App.Models;

/// <summary>
/// An executable found inside a mod in the active profile's load order, offered as a reviewable
/// suggestion. It is not persisted until the user approves it on the Tools page.
/// </summary>
public sealed record DiscoveredToolCandidate
{
    /// <summary>Stable identity for the review list (the executable's normalized absolute path).</summary>
    public required string Id { get; init; }

    /// <summary>A suggested display name (file name without extension).</summary>
    public required string SuggestedName { get; init; }

    /// <summary>The executable's absolute path on disk (inside the mod folder).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The executable's path relative to the mod folder, e.g. <c>bin\mytool.exe</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The display name of the mod (load-order folder) the executable was found in.</summary>
    public required string OriginModName { get; init; }

    /// <summary>The id of the load-order folder the executable was found in.</summary>
    public required string OriginFolderId { get; init; }

    /// <summary>The folder the executable should be treated as installed in (the mod folder path).</summary>
    public required string InstallPath { get; init; }

    /// <summary>File size in bytes, used for ranking and context.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Last write time, used to surface recently added executables.</summary>
    public DateTimeOffset LastWriteTime { get; init; }

    /// <summary>
    /// A short human-readable reason this candidate is considered a likely tool, e.g.
    /// "Small executable in mod root" or "Recently added". Used in the review UI.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}
