using System;
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
