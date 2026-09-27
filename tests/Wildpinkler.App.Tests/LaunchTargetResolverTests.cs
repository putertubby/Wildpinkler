using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class LaunchTargetResolverTests
{
    [Fact]
    public void Resolve_WithoutLauncher_UsesGameExecutable()
    {
        var game = CreateGame();

        var target = Resolve(game, Array.Empty<ProfileFolder>());

        Assert.Single(new[] { target });
        Assert.Equal("game", target.Id);
        Assert.Equal("Test game", target.DisplayName);
        Assert.Equal(game.ExecutablePath, target.ExecutablePath);
        Assert.Equal(game.InstallPath, target.WorkingDirectory);
        Assert.False(target.ProducesOutput);
        Assert.Equal(string.Empty, target.SteamGameId);
        Assert.Equal(target.ExecutablePath, target.VirtualExecutablePath);
        Assert.Equal(target.WorkingDirectory, target.VirtualWorkingDirectory);
    }

    [Fact]
    public void Resolve_UsesGameDefinitionSteamAppId()
    {
        var game = CreateGame();
        game.Definition!.SteamAppId = "489830";

        var target = Resolve(game, Array.Empty<ProfileFolder>());

        Assert.Equal("489830", target.SteamGameId);
    }

    [Fact]
    public void Resolve_WithLauncher_ReplacesGameExecutableAndKeepsGameIdentity()
    {
        var game = CreateGame();
        var launcher = CreateLauncher("launcher", "Loader", "loaders\\loader.exe", "C:\\mods\\loader");

        var target = Resolve(game, new[] { launcher });

        Assert.Equal("game", target.Id);
        Assert.Equal("profile.json", target.ConfigFileName);
        Assert.Equal("Test game (via Loader)", target.DisplayName);
        Assert.Equal(Path.Combine(launcher.Path, "loaders\\loader.exe"), target.ExecutablePath);
        Assert.Equal(launcher.Path, target.WorkingDirectory);
        Assert.False(target.ProducesOutput);
        // Virtual paths are the game-install-mounted identity of the launcher exe, never the mod's own real disk folder.
        Assert.Equal(Path.Combine(game.InstallPath, "loaders\\loader.exe"), target.VirtualExecutablePath);
        Assert.Equal(Path.Combine(game.InstallPath, "loaders"), target.VirtualWorkingDirectory);
    }

    [Fact]
    public void Resolve_IgnoresDisabledLauncher()
    {
        var game = CreateGame();
        var launcher = CreateLauncher("disabled", "Disabled loader", "loader.exe", "C:\\mods\\disabled");
        launcher.IsEnabled = false;

        var target = Resolve(game, new[] { launcher });

        Assert.Equal(game.ExecutablePath, target.ExecutablePath);
        Assert.Equal(game.InstallPath, target.WorkingDirectory);
        Assert.Equal(game.Name, target.DisplayName);
    }

    [Fact]
    public void Resolve_UsesHighestPriorityEnabledLauncherOnly()
    {
        var game = CreateGame();
        var higherPriority = CreateLauncher("first", "First loader", "first.exe", "C:\\mods\\first");
        var lowerPriority = CreateLauncher("second", "Second loader", "second.exe", "C:\\mods\\second");

        var target = Resolve(game, new[] { higherPriority, lowerPriority });

        Assert.Equal("Test game (via First loader)", target.DisplayName);
        Assert.Equal(Path.Combine(higherPriority.Path, "first.exe"), target.ExecutablePath);
    }

    [Fact]
    public void Resolve_ToolTargetCarriesOriginFolderAndModFromToolEntry()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Sorter", "sorter.exe", "C:\\mods\\sorter", "Sorter mod", "folder-1");
        var profile = CreateProfile(game);
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true });

        var targets = new LaunchTargetResolver().Resolve(profile, game, new[] { tool });

        var toolTarget = Assert.Single(targets, target => target.Kind == LaunchTargetKind.Tool);
        Assert.Equal("tool-1", toolTarget.Id);
        Assert.Equal("Sorter", toolTarget.DisplayName);
        Assert.Equal(Path.Combine("C:\\mods\\sorter", "sorter.exe"), toolTarget.ExecutablePath);
        Assert.Equal("folder-1", toolTarget.OriginFolderId);
        Assert.Equal("Sorter mod", toolTarget.OriginModName);
        // The tooltip surfaces the tool name and the providing mod.
        Assert.Equal("Sorter — from mod Sorter mod", toolTarget.TooltipText);
    }

    [Fact]
    public void Resolve_GlobalToolTargetHasNoOriginMod()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Editor", "editor.exe", "C:\\tools", string.Empty, string.Empty);
        var profile = CreateProfile(game);
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true });

        var targets = new LaunchTargetResolver().Resolve(profile, game, new[] { tool });

        var toolTarget = Assert.Single(targets, target => target.Kind == LaunchTargetKind.Tool);
        Assert.Equal(string.Empty, toolTarget.OriginFolderId);
        Assert.Equal(string.Empty, toolTarget.OriginModName);
        Assert.Equal("Editor", toolTarget.TooltipText);
    }

    [Fact]
    public void ResolveTool_ProducesOutput_MountsPendingVersionAtTopAndSkipsOverlay()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Sorter", "sorter.exe", "C:\\mods\\sorter", string.Empty, string.Empty);
        tool.Definition = new ToolDefinition { DefinitionId = "tool-1", ExecutableRelativePath = "sorter.exe", ProducesOutput = true };
        var profile = CreateProfile(game);
        var binding = new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true, OutputVersion = 1 };
        profile.Tools.Add(binding);
        var overlay = new ProfileFolder { Id = "overlay", Name = "Profile overlay", Kind = ProfileFolderKind.Overlay, IsEnabled = true, Path = Path.Combine(profile.FolderPath, "overlay") };
        profile.LoadOrder.Add(overlay);

        var target = Assert.Single(new LaunchTargetResolver().Resolve(profile, game, new[] { tool }), item => item.Kind == LaunchTargetKind.Tool);

        Assert.True(target.ProducesOutput);
        var loadOrderView = Assert.Single(target.MergedViews, view => view.MountPath == game.InstallPath);
        var pendingFolder = ProfileFolderService.GetToolOutputFolder(profile, tool.Id, binding.OutputVersion + 1);
        Assert.Equal(pendingFolder, loadOrderView.Branches[0]);
        Assert.DoesNotContain(overlay.Path, loadOrderView.Branches);
        Assert.True(loadOrderView.IsWritable);
    }

    [Fact]
    public void ResolveTool_SettingsOnly_ProducesNoOutputFolder()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Settings tool", "settings.exe", "C:\\mods\\settings", string.Empty, string.Empty);
        tool.Definition = new ToolDefinition { DefinitionId = "tool-1", ExecutableRelativePath = "settings.exe", ProducesOutput = false };
        var profile = CreateProfile(game);
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true });
        var overlay = new ProfileFolder { Id = "overlay", Name = "Profile overlay", Kind = ProfileFolderKind.Overlay, IsEnabled = true, Path = Path.Combine(profile.FolderPath, "overlay") };
        profile.LoadOrder.Add(overlay);

        var target = Assert.Single(new LaunchTargetResolver().Resolve(profile, game, new[] { tool }), item => item.Kind == LaunchTargetKind.Tool);

        Assert.False(target.ProducesOutput);
        var loadOrderView = Assert.Single(target.MergedViews, view => view.MountPath == game.InstallPath);
        Assert.Equal(overlay.Path, loadOrderView.Branches[0]);
        Assert.DoesNotContain("tool-output", string.Join('|', loadOrderView.Branches));
    }

    [Fact]
    public void ResolveTool_InjectsOutputIntoWritableToolDefinitionViews()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Sorter", "sorter.exe", "C:\\mods\\sorter", string.Empty, string.Empty);
        tool.Definition = new ToolDefinition
        {
            DefinitionId = "tool-1",
            ExecutableRelativePath = "sorter.exe",
            ProducesOutput = true,
            MergedViews = new List<MergedView>
            {
                new() { Name = "Config", MountPath = "C:\\mods\\sorter\\config", Branches = new List<string> { "C:\\mods\\sorter\\config" }, IsWritable = true },
                new() { Name = "ReadOnly", MountPath = "C:\\mods\\sorter\\data", Branches = new List<string> { "C:\\mods\\sorter\\data" }, IsWritable = false }
            }
        };
        var profile = CreateProfile(game);
        var binding = new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true, OutputVersion = 1 };
        profile.Tools.Add(binding);

        var target = Assert.Single(new LaunchTargetResolver().Resolve(profile, game, new[] { tool }), item => item.Kind == LaunchTargetKind.Tool);

        var pendingFolder = ProfileFolderService.GetToolOutputFolder(profile, tool.Id, binding.OutputVersion + 1);
        var writableView = Assert.Single(target.MergedViews, view => view.MountPath == "C:\\mods\\sorter\\config");
        Assert.Equal(pendingFolder, writableView.Branches[0]);
        Assert.True(writableView.IsWritable);
        var readOnlyView = Assert.Single(target.MergedViews, view => view.MountPath == "C:\\mods\\sorter\\data");
        Assert.Equal("C:\\mods\\sorter\\data", Assert.Single(readOnlyView.Branches));
    }

    [Fact]
    public void ResolveTool_LocalTool_CapturesOutputFlag_MountsPendingVersion()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Sorter", "sorter.exe", "C:\\mods\\sorter", string.Empty, string.Empty);
        var profile = CreateProfile(game);
        var binding = new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true, CapturesOutput = true, OutputVersion = 1 };
        profile.Tools.Add(binding);
        var overlay = new ProfileFolder { Id = "overlay", Name = "Profile overlay", Kind = ProfileFolderKind.Overlay, IsEnabled = true, Path = Path.Combine(profile.FolderPath, "overlay") };
        profile.LoadOrder.Add(overlay);

        var target = Assert.Single(new LaunchTargetResolver().Resolve(profile, game, new[] { tool }), item => item.Kind == LaunchTargetKind.Tool);

        Assert.True(target.ProducesOutput);
        var loadOrderView = Assert.Single(target.MergedViews, view => view.MountPath == game.InstallPath);
        var pendingFolder = ProfileFolderService.GetToolOutputFolder(profile, tool.Id, binding.OutputVersion + 1);
        Assert.Equal(pendingFolder, loadOrderView.Branches[0]);
        Assert.DoesNotContain(overlay.Path, loadOrderView.Branches);
    }

    [Fact]
    public void ResolveTool_LocalTool_CapturesOutputDisabled_KeepsOverlay()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Sorter", "sorter.exe", "C:\\mods\\sorter", string.Empty, string.Empty);
        var profile = CreateProfile(game);
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool-1", IsEnabled = true, CapturesOutput = false });
        var overlay = new ProfileFolder { Id = "overlay", Name = "Profile overlay", Kind = ProfileFolderKind.Overlay, IsEnabled = true, Path = Path.Combine(profile.FolderPath, "overlay") };
        profile.LoadOrder.Add(overlay);

        var target = Assert.Single(new LaunchTargetResolver().Resolve(profile, game, new[] { tool }), item => item.Kind == LaunchTargetKind.Tool);

        Assert.False(target.ProducesOutput);
        var loadOrderView = Assert.Single(target.MergedViews, view => view.MountPath == game.InstallPath);
        Assert.Equal(overlay.Path, loadOrderView.Branches[0]);
        Assert.DoesNotContain("tool-output", string.Join('|', loadOrderView.Branches));
    }

    [Fact]
    public void Resolve_SkipsDisabledToolBinding()
    {
        var game = CreateGame();
        var tool = CreateTool("tool-1", "Sorter", "sorter.exe", "C:\\mods\\sorter", "Sorter mod", "folder-1");
        var profile = CreateProfile(game);
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool-1", IsEnabled = false });

        var targets = new LaunchTargetResolver().Resolve(profile, game, new[] { tool });

        Assert.Equal(LaunchTargetKind.Game, Assert.Single(targets).Kind);
    }

    private static LaunchTarget Resolve(GameEntry game, ProfileFolder[] LoadOrder)
    {
        var profile = new Profile
        {
            Id = "profile",
            Name = "Profile",
            GameId = game.Id,
            FolderPath = "C:\\profiles\\profile"
        };
        foreach (var folder in LoadOrder)
            profile.LoadOrder.Add(folder);

        return new LaunchTargetResolver().Resolve(profile, game, Array.Empty<ToolEntry>()).Single();
    }

    private static Profile CreateProfile(GameEntry game) => new()
    {
        Id = "profile",
        Name = "Profile",
        GameId = game.Id,
        FolderPath = "C:\\profiles\\profile"
    };

    private static ToolEntry CreateTool(string id, string name, string executable, string installPath, string originModName, string originFolderId) => new()
    {
        Id = id,
        Name = name,
        SourceKind = ToolSourceKind.Discovered,
        InstallPath = installPath,
        ExecutableRelativePath = executable,
        OriginModName = originModName,
        OriginFolderId = originFolderId
    };

    private static GameEntry CreateGame() => new()
    {
        Id = "game-id",
        Name = "Test game",
        InstallPath = "C:\\games\\test",
        Definition = new GameDefinition
        {
            DefinitionId = "test-definition",
            Name = "Test definition",
            ExecutableRelativePath = "game.exe"
        }
    };

    private static ProfileFolder CreateLauncher(string id, string name, string executable, string path) => new()
    {
        Id = id,
        Name = name,
        Path = path,
        Kind = ProfileFolderKind.Mod,
        IsEnabled = true,
        LauncherExecutableRelativePath = executable
    };
}
