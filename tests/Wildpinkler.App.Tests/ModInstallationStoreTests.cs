using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModInstallationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-installstore-" + Guid.NewGuid().ToString("N"));

    public ModInstallationStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RoundTrip_PreservesFomodRecipeAndArchiveIdentity()
    {
        var store = new ModInstallationStore(_root);
        var installation = new ModInstallation
        {
            Id = "install",
            ModId = "mod",
            SourceArchiveSha256 = new string('A', 64),
            SelectionSignature = new string('B', 64),
            SelectionSummary = "Core",
            FolderPath = Path.Combine(_root, "install"),
            Recipe = new FomodInstallationRecipe
            {
                ModuleConfigSha256 = new string('C', 64),
                Selections =
                {
                    new FomodSelectionChoice { Step = "Version", Group = "Edition", Plugins = { "Anniversary" } }
                }
            }
        };

        await store.SaveAsync(new[] { installation });
        var loaded = Assert.Single(await store.LoadAsync());

        Assert.Equal(installation.SourceArchiveSha256, loaded.SourceArchiveSha256);
        var recipe = Assert.IsType<FomodInstallationRecipe>(loaded.Recipe);
        Assert.Equal(installation.Recipe is FomodInstallationRecipe source ? source.ModuleConfigSha256 : null, recipe.ModuleConfigSha256);
        var choice = Assert.Single(recipe.Selections);
        Assert.Equal("Version", choice.Step);
        Assert.Equal("Edition", choice.Group);
        Assert.Equal("Anniversary", Assert.Single(choice.Plugins));
    }

    [Fact]
    public async Task RoundTrip_PreservesManualRecipe()
    {
        var store = new ModInstallationStore(_root);
        await store.SaveAsync(new[]
        {
            new ModInstallation
            {
                Id = "install",
                ModId = "mod",
                SourceArchiveSha256 = new string('A', 64),
                Recipe = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "Data" }
            }
        });

        var recipe = Assert.IsType<ManualInstallationRecipe>(Assert.Single(await store.LoadAsync()).Recipe);
        Assert.Equal("wrapper", recipe.SourceRoot);
        Assert.Equal("Data", recipe.Destination);
    }

    [Fact]
    public async Task LegacySchema_IsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "mod-installations.json"), """
            { "SchemaVersion": 1, "Installations": [] }
            """);

        await Assert.ThrowsAsync<JsonException>(() => new ModInstallationStore(_root).LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
