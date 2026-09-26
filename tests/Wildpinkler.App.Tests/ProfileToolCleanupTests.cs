using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Profiles;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileToolCleanupTests
{
    private static Profile CreateProfile(ProfileFolder[] loadOrder)
    {
        var profile = new Profile
        {
            Id = "profile",
            Name = "Profile",
            GameId = "game-id",
            FolderPath = "C:\\profiles\\profile"
        };
        foreach (var folder in loadOrder)
            profile.LoadOrder.Add(folder);
        return profile;
    }

    private static ProfileFolder CreateModFolder(string id, string name, string path) => new()
    {
        Id = id,
        Name = name,
        Path = path,
        Kind = ProfileFolderKind.Mod,
        IsEnabled = true
    };

    private static ProfileFolder CreateToolOutputFolder(string toolId) => new()
    {
        Id = "output-" + toolId,
        Name = "output",
        Path = "C:\\profiles\\profile\\tool-output\\" + toolId + "-1",
        Kind = ProfileFolderKind.ToolOutput,
        ToolEntryId = toolId
    };

    private static LocalTool CreateLocalTool(string id, string folderId, string installPath, string relativePath) => new()
    {
        Id = id,
        Name = "tool",
        InstallPath = installPath,
        ExecutableRelativePath = relativePath,
        OriginFolderId = folderId,
        OriginModName = "MyMod"
    };

    private static ProfileTool CreateBinding(string toolId) => new()
    {
        ToolEntryId = toolId,
        IsEnabled = true
    };

    // ---- RemoveToolsForFolder ----

    [Fact]
    public void RemoveToolsForFolder_RemovesToolsBindingsAndOutputFolder()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        profile.LocalTools.Add(CreateLocalTool("tool-1", "mod-1", "C:\\mods\\MyMod", "bin\\a.exe"));
        profile.LocalTools.Add(CreateLocalTool("tool-2", "mod-1", "C:\\mods\\MyMod", "bin\\b.exe"));
        profile.LocalTools.Add(CreateLocalTool("tool-3", "other-mod", "C:\\mods\\Other", "c.exe"));

        profile.Tools.Add(CreateBinding("tool-1"));
        profile.Tools.Add(CreateBinding("tool-3"));
        profile.LoadOrder.Add(CreateToolOutputFolder("tool-1"));
        profile.LocalToolEntries = new List<ToolEntry>();

        var removed = ProfileToolCleanup.RemoveToolsForFolder(profile, "mod-1");

        Assert.Equal(2, removed);
        Assert.DoesNotContain(profile.LocalTools, tool => tool.Id == "tool-1" || tool.Id == "tool-2");
        Assert.Contains(profile.LocalTools, tool => tool.Id == "tool-3");
        Assert.DoesNotContain(profile.Tools, bound => bound.ToolEntryId == "tool-1");
        Assert.Contains(profile.Tools, bound => bound.ToolEntryId == "tool-3");
        Assert.DoesNotContain(profile.LoadOrder, item => item.Kind == ProfileFolderKind.ToolOutput && item.ToolEntryId == "tool-1");
        // Local tool entries are materialized from the surviving local tools.
        Assert.Equal(new[] { "tool-3" }, profile.LocalToolEntries.Select(tool => tool.Id).ToArray());
    }

    [Fact]
    public void RemoveToolsForFolder_NoToolsForFolder_ReturnsZeroAndLeavesEverything()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        profile.LocalTools.Add(CreateLocalTool("tool-3", "other-mod", "C:\\mods\\Other", "c.exe"));
        profile.Tools.Add(CreateBinding("tool-3"));
        profile.LocalToolEntries = new List<ToolEntry> { new() { Id = "stale" } };

        var removed = ProfileToolCleanup.RemoveToolsForFolder(profile, "mod-1");

        Assert.Equal(0, removed);
        Assert.Single(profile.LocalTools);
        Assert.Single(profile.Tools);
        // Still materialized even with nothing to remove.
        Assert.Equal(new[] { "tool-3" }, profile.LocalToolEntries.Select(tool => tool.Id).ToArray());
    }

    // ---- DisableToolsForFolder ----

    [Fact]
    public void DisableToolsForFolder_KeepsLocalToolsRemovesBindingsAndOutput()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        profile.LocalTools.Add(CreateLocalTool("tool-1", "mod-1", "C:\\mods\\MyMod", "bin\\a.exe"));
        profile.LocalTools.Add(CreateLocalTool("tool-2", "other-mod", "C:\\mods\\Other", "b.exe"));
        profile.Tools.Add(CreateBinding("tool-1"));
        profile.LoadOrder.Add(CreateToolOutputFolder("tool-1"));

        var provisioner = new ProfileFolderService("C:\\profiles");
        var disabled = ProfileToolCleanup.DisableToolsForFolder(profile, "mod-1", provisioner);

        Assert.Equal(1, disabled);
        // Local tool records survive so role memory is preserved.
        Assert.Contains(profile.LocalTools, tool => tool.Id == "tool-1");
        Assert.Contains(profile.LocalTools, tool => tool.Id == "tool-2");
        // The binding is dropped.
        Assert.DoesNotContain(profile.Tools, bound => bound.ToolEntryId == "tool-1");
        // The tool-output folder is removed from the load order.
        Assert.DoesNotContain(profile.LoadOrder, item => item.Kind == ProfileFolderKind.ToolOutput && item.ToolEntryId == "tool-1");
    }

    [Fact]
    public void DisableToolsForFolder_NoToolsForFolder_ReturnsZero()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });
        profile.LocalTools.Add(CreateLocalTool("tool-2", "other-mod", "C:\\mods\\Other", "b.exe"));
        profile.Tools.Add(CreateBinding("tool-2"));

        var provisioner = new ProfileFolderService("C:\\profiles");
        var disabled = ProfileToolCleanup.DisableToolsForFolder(profile, "mod-1", provisioner);

        Assert.Equal(0, disabled);
        Assert.Single(profile.LocalTools);
        Assert.Single(profile.Tools);
    }

    // ---- PruneMissingBindings ----

    [Fact]
    public void PruneMissingBindings_RemovesOrphanedAndKeepsKnown()
    {
        var profile = CreateProfile(Array.Empty<ProfileFolder>());
        profile.LocalTools.Add(CreateLocalTool("local-1", "mod-1", "C:\\mods\\MyMod", "a.exe"));

        var global = new List<ToolEntry>
        {
            new() { Id = "global-1", Name = "Global", SourceKind = ToolSourceKind.Manual, InstallPath = "C:\\tools", ExecutableRelativePath = "g.exe" }
        };

        profile.Tools.Add(CreateBinding("local-1"));
        profile.Tools.Add(CreateBinding("global-1"));
        profile.Tools.Add(CreateBinding("missing-1"));

        var pruned = ProfileToolCleanup.PruneMissingBindings(profile, global);

        Assert.True(pruned);
        Assert.Contains(profile.Tools, bound => bound.ToolEntryId == "local-1");
        Assert.Contains(profile.Tools, bound => bound.ToolEntryId == "global-1");
        Assert.DoesNotContain(profile.Tools, bound => bound.ToolEntryId == "missing-1");
    }

    [Fact]
    public void PruneMissingBindings_NothingOrphaned_ReturnsFalse()
    {
        var profile = CreateProfile(Array.Empty<ProfileFolder>());
        profile.LocalTools.Add(CreateLocalTool("local-1", "mod-1", "C:\\mods\\MyMod", "a.exe"));
        profile.Tools.Add(CreateBinding("local-1"));

        var pruned = ProfileToolCleanup.PruneMissingBindings(profile, Array.Empty<ToolEntry>());

        Assert.False(pruned);
        Assert.Single(profile.Tools);
    }

    // ---- ApplyRoles ----

    [Fact]
    public void ApplyRoles_NewToolRole_CreatesToolAndEnabledBinding()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "bin\\a.exe"),
            RelativePath = "bin/a.exe",
            SuggestedName = "a",
            Role = ModRole.Tool,
            IsEnabled = true
        };

        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        Assert.Equal(1, changed); // only the tool creation counts; the new binding does not
        var tool = Assert.Single(profile.LocalTools);
        Assert.Equal("a", tool.Name);
        Assert.Equal("mod-1", tool.OriginFolderId);
        Assert.Equal("MyMod", tool.OriginModName);
        Assert.Equal(folder.Path, tool.InstallPath);
        var binding = Assert.Single(profile.Tools);
        Assert.Equal(tool.Id, binding.ToolEntryId);
        Assert.True(binding.IsEnabled);
    }

    [Fact]
    public void ApplyRoles_ToolToSkip_RemovesToolAndBinding()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        var tool = CreateLocalTool("tool-1", "mod-1", "C:\\mods\\MyMod", "bin/a.exe");
        profile.LocalTools.Add(tool);
        profile.Tools.Add(CreateBinding("tool-1"));

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "bin\\a.exe"),
            RelativePath = "bin/a.exe",
            SuggestedName = "a",
            Role = ModRole.Skip
        };

        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        Assert.Equal(1, changed);
        Assert.Empty(profile.LocalTools);
        Assert.Empty(profile.Tools);
    }

    [Fact]
    public void ApplyRoles_LauncherOnly_SetsLauncherAndCreatesNoTool()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });
        Assert.Null(folder.LauncherExecutableRelativePath);

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "launcher.exe"),
            RelativePath = "launcher.exe",
            SuggestedName = "launcher",
            Role = ModRole.Launcher
        };

        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        Assert.Equal(1, changed); // only the launcher designation changed
        Assert.Empty(profile.LocalTools);
        Assert.Empty(profile.Tools);
        Assert.Equal("launcher.exe", folder.LauncherExecutableRelativePath);
    }

    [Fact]
    public void ApplyRoles_LauncherOnExistingTool_KeepsToolAndBinding()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });
        var folderLauncher = "other.exe";

        var tool = CreateLocalTool("tool-1", "mod-1", "C:\\mods\\MyMod", "launcher.exe");
        profile.LocalTools.Add(tool);
        profile.Tools.Add(CreateBinding("tool-1"));
        folder.LauncherExecutableRelativePath = folderLauncher;

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "launcher.exe"),
            RelativePath = "launcher.exe",
            SuggestedName = "launcher",
            Role = ModRole.Launcher
        };

        ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        // Existing tool and its binding survive; launcher path is updated.
        Assert.Single(profile.LocalTools);
        Assert.Single(profile.Tools);
        Assert.Equal("launcher.exe", folder.LauncherExecutableRelativePath);
    }

    [Fact]
    public void ApplyRoles_RedisableWithRememberedDisabled_FlipsBindingOff()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        // Use backslashes so Path.Combine produces a path matching choice.ExecutablePath.
        var tool = CreateLocalTool("tool-1", "mod-1", "C:\\mods\\MyMod", "bin\\a.exe");
        profile.LocalTools.Add(tool);
        var binding = CreateBinding("tool-1");
        profile.Tools.Add(binding);

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "bin\\a.exe"),
            RelativePath = "bin/a.exe",
            // Same name as the stored tool so only the enable-state flip counts.
            SuggestedName = "tool",
            Role = ModRole.Tool,
            IsEnabled = false
        };

        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        Assert.Equal(1, changed); // only the enable-state flip
        Assert.Contains(profile.LocalTools, item => item.Id == "tool-1");
        Assert.False(binding.IsEnabled);
    }

    [Fact]
    public void ApplyRoles_ExistingTool_PreservesLaunchArguments()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        var tool = CreateLocalTool("tool-1", "mod-1", "C:\\mods\\MyMod", "bin/a.exe");
        tool.LaunchArguments = "--flag value";
        profile.LocalTools.Add(tool);

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "bin\\a.exe"),
            RelativePath = "bin/a.exe",
            SuggestedName = "a",
            Role = ModRole.Tool,
            IsEnabled = true
        };

        ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        Assert.Equal("--flag value", tool.LaunchArguments);
    }

    [Fact]
    public void ApplyRoles_TwoLaunchers_LastPickWins()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        var first = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "first.exe"),
            RelativePath = "first.exe",
            SuggestedName = "first",
            Role = ModRole.Launcher
        };
        var second = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "second.exe"),
            RelativePath = "second.exe",
            SuggestedName = "second",
            Role = ModRole.Launcher
        };

        ProfileToolCleanup.ApplyRoles(profile, folder, new[] { first, second });

        Assert.Equal("second.exe", folder.LauncherExecutableRelativePath);
    }

    [Fact]
    public void ApplyRoles_UnrelatedLocalToolsUntouched()
    {
        var folder = CreateModFolder("mod-1", "MyMod", "C:\\mods\\MyMod");
        var profile = CreateProfile(new[] { folder });

        var unrelated = CreateLocalTool("tool-x", "other-mod", "C:\\mods\\Other", "x.exe");
        profile.LocalTools.Add(unrelated);
        profile.Tools.Add(CreateBinding("tool-x"));

        var choice = new ModRoleChoice
        {
            ExecutablePath = Path.Combine("C:\\mods\\MyMod", "bin\\a.exe"),
            RelativePath = "bin/a.exe",
            SuggestedName = "a",
            Role = ModRole.Tool,
            IsEnabled = true
        };

        ProfileToolCleanup.ApplyRoles(profile, folder, new[] { choice });

        Assert.Contains(profile.LocalTools, item => item.Id == "tool-x");
        Assert.Contains(profile.Tools, bound => bound.ToolEntryId == "tool-x");
        // The unrelated tool keeps its own origin metadata.
        Assert.Equal("other-mod", unrelated.OriginFolderId);
    }
}
