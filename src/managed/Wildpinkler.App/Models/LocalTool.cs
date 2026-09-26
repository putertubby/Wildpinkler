namespace Wildpinkler.App.Models;

/// <summary>
/// A tool executable discovered inside a profile's mod load order, persisted on the profile itself
/// (rather than in the global tools store). Maps to an in-memory <see cref="ToolEntry"/> for reuse
/// with the launch resolver and profile tool rows.
/// </summary>
public sealed class LocalTool
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The folder holding the discovered executable.</summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>The executable's path relative to <see cref="InstallPath"/>.</summary>
    public string ExecutableRelativePath { get; set; } = string.Empty;

    /// <summary>Per-profile launch argument override captured at discovery time (usually empty).</summary>
    public string LaunchArguments { get; set; } = string.Empty;

    /// <summary>The mod whose folder the executable was found in, for display.</summary>
    public string OriginModName { get; set; } = string.Empty;

    /// <summary>The id of the load-order folder the executable was found in.</summary>
    public string OriginFolderId { get; set; } = string.Empty;

    /// <summary>Maps this DTO to the in-memory tool representation shared with global tools.</summary>
    public ToolEntry ToToolEntry(string profileId) => new()
    {
        Id = Id,
        Name = Name,
        InstallPath = InstallPath,
        ExecutableRelativePath = ExecutableRelativePath,
        LaunchArguments = LaunchArguments,
        SourceKind = ToolSourceKind.Discovered,
        OriginModName = OriginModName,
        OriginFolderId = OriginFolderId,
        ProfileId = profileId
    };
}
