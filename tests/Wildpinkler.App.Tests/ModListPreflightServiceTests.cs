using System;
using System.IO;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListPreflightServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-preflight-" + Guid.NewGuid().ToString("N"));

    public ModListPreflightServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Evaluate_ReusesMatchingArchiveAndInstallation()
    {
        var archive = Path.Combine(_root, "mod.zip");
        var folder = Path.Combine(_root, "install");
        File.WriteAllText(archive, "archive");
        Directory.CreateDirectory(folder);
        var manifest = CreateManifest();
        var mod = new ModEntry { Id = "local-mod", Name = "Mod", Sha256 = Hash, ArchivePath = archive };
        var installation = new ModInstallation
        {
            Id = "local-install",
            ModId = mod.Id,
            SourceArchiveSha256 = Hash,
            FolderPath = folder,
            Recipe = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "Data" }
        };

        var result = new ModListPreflightService().Evaluate(
            manifest, CreateGame(), new[] { mod }, new[] { installation }, Array.Empty<ToolEntry>());

        Assert.True(result.CanStart);
        Assert.Equal(ModListBuildTaskState.Completed, result.Tasks[0].State);
        Assert.Equal(ModListBuildTaskState.Completed, result.Tasks[1].State);
        Assert.Equal("local-install", Assert.Single(result.Artifacts).InstallationId);
    }

    [Fact]
    public void Evaluate_MarksPrivateArchiveAndGuidedInstallAsUserActions()
    {
        var manifest = CreateManifest();
        var requirement = Assert.IsType<ModListModEntry>(Assert.Single(manifest.Content));
        requirement.Source = null;
        requirement.AcquisitionInstructions = "Choose the archive.";
        requirement.Installation = new GuidedInstallationRecipe { Instructions = "Choose options." };

        var result = new ModListPreflightService().Evaluate(
            manifest, CreateGame(), Array.Empty<ModEntry>(), Array.Empty<ModInstallation>(), Array.Empty<ToolEntry>());

        Assert.True(result.CanStart);
        Assert.Equal(ModListBuildTaskState.NeedsUser, result.Tasks[0].State);
        Assert.Equal(ModListBuildTaskState.NeedsUser, result.Tasks[1].State);
    }

    [Fact]
    public void Evaluate_BlocksWrongGameDefinition()
    {
        var game = CreateGame();
        game.DefinitionId = "fallout4";

        var result = new ModListPreflightService().Evaluate(
            CreateManifest(), game, Array.Empty<ModEntry>(), Array.Empty<ModInstallation>(), Array.Empty<ToolEntry>());

        Assert.False(result.CanStart);
        Assert.Contains(result.BlockingIssues, issue => issue.Contains("does not use definition", StringComparison.Ordinal));
    }

    private static ModListManifest CreateManifest() => new()
    {
        ListId = "list",
        Name = "List",
        Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
        Content =
        {
            new ModListModEntry
            {
                EntryId = "mod",
                Order = 1,
                Name = "Mod",
                Source = new Wildpinkler.Remote.RemoteRef("nexus", "skyrimspecialedition", "1", "2"),
                Archive = new ModListArchiveRequirement { FileName = "mod.zip", Sha256 = Hash },
                Installation = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "Data" }
            }
        }
    };

    private static GameEntry CreateGame() => new()
    {
        Id = "game",
        Name = "Skyrim",
        DefinitionId = "skyrim-se",
        DefinitionVersion = 1,
        Definition = new GameDefinition { DefinitionId = "skyrim-se", DefinitionVersion = 1, Name = "Skyrim" }
    };

    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
