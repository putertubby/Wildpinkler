using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Profiles;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileLocalToolTests
{
    [Fact]
    public void ToToolEntry_MapsAllFieldsAndIsProfileScoped()
    {
        var local = CreateLocalTool("local-1", "Sorter", "sorter.exe");

        var entry = local.ToToolEntry("profile");

        Assert.Equal("local-1", entry.Id);
        Assert.Equal("Sorter", entry.Name);
        Assert.Equal("C:\\mods\\sorter", entry.InstallPath);
        Assert.Equal("sorter.exe", entry.ExecutableRelativePath);
        Assert.Equal("tool-args", entry.LaunchArguments);
        Assert.Equal(ToolSourceKind.Discovered, entry.SourceKind);
        Assert.Equal("Sorter mod", entry.OriginModName);
        Assert.Equal("folder-1", entry.OriginFolderId);
        Assert.Equal("profile", entry.ProfileId);
        Assert.True(entry.IsProfileScoped);
        Assert.Equal(Path.Combine("C:\\mods\\sorter", "sorter.exe"), entry.ExecutablePath);
    }

    [Fact]
    public void Migrate_MovesScopedEntriesOntoTheirProfile()
    {
        var global = CreateGlobalTool("global-1");
        var scoped = CreateScopedTool("scoped-1", "profile");
        var profile = CreateProfile("profile");

        var tools = new List<ToolEntry> { global, scoped };

        var moved = ProfileLocalToolMigration.Migrate(tools, new List<Profile> { profile });

        Assert.True(moved);
        Assert.Same(global, tools.Single());
        Assert.Single(profile.LocalTools);
        var local = profile.LocalTools[0];
        Assert.Equal("scoped-1", local.Id);
        Assert.Equal("Scoped tool", local.Name);
        Assert.Equal("scoped.exe", local.ExecutableRelativePath);
    }

    [Fact]
    public void Migrate_DropsScopedEntriesWhoseProfileIsGone()
    {
        var scoped = CreateScopedTool("scoped-1", "missing-profile");
        var tools = new List<ToolEntry> { scoped };

        var moved = ProfileLocalToolMigration.Migrate(tools, new List<Profile>());

        Assert.True(moved);
        Assert.Empty(tools);
    }

    [Fact]
    public void Migrate_ReturnsFalseWhenNothingIsScoped()
    {
        var tools = new List<ToolEntry> { CreateGlobalTool("global-1") };

        var moved = ProfileLocalToolMigration.Migrate(tools, new List<Profile>());

        Assert.False(moved);
        Assert.Single(tools);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var scoped = CreateScopedTool("scoped-1", "profile");
        var profile = CreateProfile("profile");
        var tools = new List<ToolEntry> { scoped };

        Assert.True(ProfileLocalToolMigration.Migrate(tools, new List<Profile> { profile }));
        Assert.False(ProfileLocalToolMigration.Migrate(tools, new List<Profile> { profile }));
        Assert.Single(profile.LocalTools);
    }

    [Fact]
    public void Materialize_RebuildsLocalToolEntriesFromLocalTools()
    {
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));
        profile.LocalTools.Add(CreateLocalTool("local-2", "Editor", "editor.exe"));

        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);

        var entries = profile.LocalToolEntries;
        Assert.Equal(new[] { "local-1", "local-2" }, entries.Select(entry => entry.Id));
        Assert.All(entries, entry => Assert.Equal(ToolSourceKind.Discovered, entry.SourceKind));
        Assert.All(entries, entry => Assert.Equal("profile", entry.ProfileId));
    }

    [Fact]
    public void MergedTools_ContainsGlobalToolsThenLocalTools()
    {
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));
        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);
        var global = CreateGlobalTool("global-1");

        var merged = ProfileLocalToolMigration.MergedTools(new[] { global }, profile);

        Assert.Equal(new[] { "global-1", "local-1" }, merged.Select(tool => tool.Id));
    }

    [Fact]
    public void KnownToolIds_ContainsGlobalAndLocalToolIds()
    {
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));
        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);
        var global = CreateGlobalTool("global-1");

        var known = ProfileLocalToolMigration.KnownToolIds(new[] { global }, profile);

        Assert.Contains("global-1", known);
        Assert.Contains("local-1", known);
        Assert.DoesNotContain("other", known);
    }

    [Fact]
    public void Resolve_ResolvesEnabledLocalToolAsToolTarget()
    {
        var game = CreateGame();
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));
        profile.Tools.Add(new ProfileTool { ToolEntryId = "local-1", IsEnabled = true });

        var tools = ProfileLocalToolMigration.MergedTools(Array.Empty<ToolEntry>(), profile);
        var targets = new LaunchTargetResolver().Resolve(profile, game, tools);

        var toolTarget = Assert.Single(targets, target => target.Kind == LaunchTargetKind.Tool);
        Assert.Equal("local-1", toolTarget.Id);
        Assert.Equal(Path.Combine("C:\\mods\\sorter", "sorter.exe"), toolTarget.ExecutablePath);
        Assert.Equal("tool-args", toolTarget.Arguments);
        Assert.False(toolTarget.ProducesOutput);
    }

    [Fact]
    public void Resolve_UsesBindingLaunchArgumentsOverrideForLocalTool()
    {
        var game = CreateGame();
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));
        profile.Tools.Add(new ProfileTool { ToolEntryId = "local-1", IsEnabled = true, LaunchArgumentsOverride = "-force" });

        var tools = ProfileLocalToolMigration.MergedTools(Array.Empty<ToolEntry>(), profile);
        var targets = new LaunchTargetResolver().Resolve(profile, game, tools);

        var toolTarget = targets.Single(target => target.Kind == LaunchTargetKind.Tool);
        Assert.Equal("-force", toolTarget.Arguments);
    }

    [Fact]
    public void Resolve_SkipsDisabledLocalToolBinding()
    {
        var game = CreateGame();
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));
        profile.Tools.Add(new ProfileTool { ToolEntryId = "local-1", IsEnabled = false });

        var tools = ProfileLocalToolMigration.MergedTools(Array.Empty<ToolEntry>(), profile);
        var targets = new LaunchTargetResolver().Resolve(profile, game, tools);

        Assert.Equal(LaunchTargetKind.Game, Assert.Single(targets).Kind);
    }

    [Fact]
    public void Resolve_SkipsLocalToolWithoutBinding()
    {
        var game = CreateGame();
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(CreateLocalTool("local-1", "Sorter", "sorter.exe"));

        var tools = ProfileLocalToolMigration.MergedTools(Array.Empty<ToolEntry>(), profile);
        var targets = new LaunchTargetResolver().Resolve(profile, game, tools);

        Assert.Equal(LaunchTargetKind.Game, Assert.Single(targets).Kind);
    }

    [Fact]
    public void Resolve_SkipsLocalToolWithoutExecutablePath()
    {
        var game = CreateGame();
        var profile = CreateProfile("profile");
        profile.LocalTools.Add(new LocalTool
        {
            Id = "local-1",
            Name = "Incomplete",
            ExecutableRelativePath = string.Empty
        });
        profile.Tools.Add(new ProfileTool { ToolEntryId = "local-1", IsEnabled = true });

        var tools = ProfileLocalToolMigration.MergedTools(Array.Empty<ToolEntry>(), profile);
        var targets = new LaunchTargetResolver().Resolve(profile, game, tools);

        Assert.Equal(LaunchTargetKind.Game, Assert.Single(targets).Kind);
    }

    private static Profile CreateProfile(string id) => new()
    {
        Id = id,
        Name = id,
        GameId = "game-id",
        FolderPath = Path.Combine("C:\\profiles", id)
    };

    private static LocalTool CreateLocalTool(string id, string name, string executableRelativePath) => new()
    {
        Id = id,
        Name = name,
        InstallPath = "C:\\mods\\sorter",
        ExecutableRelativePath = executableRelativePath,
        LaunchArguments = "tool-args",
        OriginModName = "Sorter mod",
        OriginFolderId = "folder-1"
    };

    private static ToolEntry CreateGlobalTool(string id) => new()
    {
        Id = id,
        Name = "Global tool",
        SourceKind = ToolSourceKind.Manual,
        ExecutableRelativePath = "global.exe",
        InstallPath = "C:\\tools"
    };

    private static ToolEntry CreateScopedTool(string id, string profileId) => new()
    {
        Id = id,
        Name = "Scoped tool",
        SourceKind = ToolSourceKind.Discovered,
        ExecutableRelativePath = "scoped.exe",
        InstallPath = "C:\\mods\\scoped",
        ProfileId = profileId
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
}
