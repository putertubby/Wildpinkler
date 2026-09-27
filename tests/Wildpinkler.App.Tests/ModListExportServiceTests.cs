using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListExportServiceTests
{
    [Fact]
    public async Task CreateAsync_ExportsPortableIntentWithoutLocalPathsOrGeneratedFolders()
    {
        var profile = new Profile
        {
            Id = "profile-local-id",
            Name = "Test profile",
            GameId = "game-local-id",
            FolderPath = @"C:\Users\someone\profile",
            Variables = new Dictionary<string, string>
            {
                ["Portable"] = @"relative\path",
                ["MachineLocal"] = @"C:\Private\path"
            }
        };
        profile.LoadOrder.Add(new ProfileFolder { Id = "overlay", Name = "Overlay", Path = @"C:\Users\someone\profile\overlay", Kind = ProfileFolderKind.Overlay });
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "mod-folder-local-id",
            Name = "Remote mod",
            Path = @"C:\Users\someone\install",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod-local-id",
            ModInstallationId = "installation-local-id",
            LauncherExecutableRelativePath = "loader.exe"
        });
        profile.LoadOrder.Add(new ProfileFolder { Id = "tool-output", Name = "Tool output", Path = @"C:\Users\someone\output", Kind = ProfileFolderKind.ToolOutput });
        profile.LoadOrder.Add(new ProfileFolder { Id = "private", Name = "Private patch", Path = @"D:\Private", Kind = ProfileFolderKind.Unmanaged });
        profile.LoadOrder.Add(new ProfileFolder { Id = "game", Name = "Game", Path = @"C:\Games\Skyrim", Kind = ProfileFolderKind.GameInstall });
        profile.Tools.Add(new ProfileTool
        {
            ToolEntryId = "tool-local-id",
            IsEnabled = true,
            LaunchArgumentsOverride = "--sort",
            VariableOverrides = new Dictionary<string, string> { ["Portable"] = "relative", ["Local"] = @"C:\Tool" }
        });

        var mod = new ModEntry
        {
            Id = "mod-local-id",
            Name = "Remote mod",
            Version = "1.2",
            FileName = "remote.zip",
            Sha256 = new string('A', 64),
            Md5 = new string('B', 32),
            Remote = new RemoteRef("nexus", "skyrimspecialedition", "42", "84", "https://example.test/mod?key=secret")
        };
        var installation = new ModInstallation
        {
            Id = "installation-local-id",
            ModId = mod.Id,
            SourceArchiveSha256 = mod.Sha256,
            Recipe = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "Data" }
        };
        var game = new GameEntry
        {
            Id = profile.GameId,
            DefinitionId = "skyrim-se",
            DefinitionVersion = 2,
            InstallPath = @"C:\Games\Skyrim"
        };
        var definition = new GameDefinition
        {
            DefinitionId = "skyrim-se",
            DefinitionVersion = 2,
            Name = "Skyrim SE",
            ExecutableRelativePath = "SkyrimSE.exe"
        };
        var tool = new ToolEntry
        {
            Id = "tool-local-id",
            Name = "LOOT",
            DefinitionId = "loot",
            DefinitionVersion = 1
        };

        var result = await new ModListExportService().CreateAsync(
            profile, game, definition, new[] { mod }, new[] { installation }, new[] { tool },
            new ModListExportMetadata("test-list", 1, "Test list", "Author", "Description"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Manifest.Content.Count);
        var exportedMod = Assert.IsType<ModListModEntry>(result.Manifest.Content[0]);
        Assert.Equal(1, exportedMod.Order);
        Assert.Equal("84", exportedMod.Source!.FileKey);
        Assert.Null(exportedMod.Source.PageUrl);
        Assert.Equal("loader.exe", exportedMod.LauncherExecutableRelativePath);
        var recipe = Assert.IsType<ManualInstallationRecipe>(exportedMod.Installation);
        Assert.Equal("wrapper", recipe.SourceRoot);
        Assert.Equal("Data", recipe.Destination);
        Assert.IsType<ModListGuidedFolderEntry>(result.Manifest.Content[1]);
        Assert.Single(result.Manifest.Profile.ManualSettings);
        Assert.Equal("relative\\path", result.Manifest.Profile.Variables["Portable"]);
        var exportedTool = Assert.Single(result.Manifest.Tools);
        Assert.Equal("loot", exportedTool.DefinitionId);
        Assert.False(exportedTool.VariableOverrides.ContainsKey("Local"));
        Assert.Equal(ModListGrade.Guided, result.Grade.Grade);

        var json = new ModListManifestSerializer().Serialize(result.Manifest);
        Assert.DoesNotContain("profile-local-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mod-local-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("installation-local-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_KeepsMissingArchiveAsUnavailableRequirement()
    {
        var profile = new Profile { Id = "profile", Name = "Test", GameId = "game" };
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "folder",
            Name = "Lost private mod",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod"
        });
        var game = new GameEntry { Id = "game", DefinitionId = "skyrim-se" };
        var definition = new GameDefinition { DefinitionId = "skyrim-se", DefinitionVersion = 1, Name = "Skyrim SE" };
        var mod = new ModEntry { Id = "mod", Name = "Lost private mod", FileName = "lost.zip" };

        var result = await new ModListExportService().CreateAsync(
            profile, game, definition, new[] { mod }, Array.Empty<ModInstallation>(), Array.Empty<ToolEntry>(),
            new ModListExportMetadata("test-list", 1, "Test list", "", ""),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModListGrade.Unavailable, result.Grade.Grade);
        var exported = Assert.IsType<ModListModEntry>(Assert.Single(result.Manifest.Content));
        Assert.Empty(exported.Archive.Sha256);
        Assert.IsType<GuidedInstallationRecipe>(exported.Installation);
    }

    [Fact]
    public async Task CreateAsync_ExportedToolRequirements_KeepDefinitionIdAndEnabledState()
    {
        var profile = new Profile { Id = "profile", Name = "Test", GameId = "game" };
        profile.Tools.Add(new ProfileTool { ToolEntryId = "output-tool", IsEnabled = true });
        profile.Tools.Add(new ProfileTool { ToolEntryId = "settings-tool", IsEnabled = true });
        profile.Tools.Add(new ProfileTool { ToolEntryId = "disabled-tool", IsEnabled = false });
        var game = new GameEntry { Id = "game", DefinitionId = "skyrim-se" };
        var definition = new GameDefinition { DefinitionId = "skyrim-se", DefinitionVersion = 1, Name = "Skyrim SE" };
        var outputTool = new ToolEntry { Id = "output-tool", Name = "Sorter", DefinitionId = "sorter", DefinitionVersion = 2 };
        outputTool.Definition = new ToolDefinition { DefinitionId = "sorter", ExecutableRelativePath = "sorter.exe", ProducesOutput = true };
        var settingsTool = new ToolEntry { Id = "settings-tool", Name = "Settings tool", DefinitionId = "settings", DefinitionVersion = 1 };
        settingsTool.Definition = new ToolDefinition { DefinitionId = "settings", ExecutableRelativePath = "settings.exe", ProducesOutput = false };
        var disabledTool = new ToolEntry { Id = "disabled-tool", Name = "Disabled" };

        var result = await new ModListExportService().CreateAsync(
            profile, game, definition, Array.Empty<ModEntry>(), Array.Empty<ModInstallation>(),
            new[] { outputTool, settingsTool, disabledTool },
            new ModListExportMetadata("test-list", 1, "Test list", "", ""),
            TestContext.Current.CancellationToken);

        var exportedTools = result.Manifest.Tools.OrderBy(tool => tool.Name).ToArray();
        Assert.Equal(2, exportedTools.Length);
        var sorter = Assert.Single(exportedTools, tool => tool.DefinitionId == "sorter");
        Assert.Equal("tool-sorter", sorter.RequirementId);
        Assert.True(sorter.IsEnabled);
        Assert.Equal("Sorter", sorter.Name);
        var settings = Assert.Single(exportedTools, tool => tool.DefinitionId == "settings");
        Assert.Equal("tool-settings", settings.RequirementId);
        Assert.True(settings.IsEnabled);
    }
}
