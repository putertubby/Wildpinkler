using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-modlist-" + Guid.NewGuid().ToString("N"));
    private readonly ModListManifestSerializer _serializer = new();

    public ModListManifestTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RoundTrip_PreservesPortableRecipeAndExactRemoteFile()
    {
        var manifest = CreateValidManifest();
        var path = Path.Combine(_root, "test" + ModListManifestSerializer.FileExtension);

        await _serializer.SaveAsync(manifest, path, TestContext.Current.CancellationToken);
        var loaded = await _serializer.LoadAsync(path, TestContext.Current.CancellationToken);

        var mod = Assert.IsType<ModListModEntry>(Assert.Single(loaded.Content));
        Assert.Equal("42", mod.Source!.ModKey);
        Assert.Equal("84", mod.Source.FileKey);
        var recipe = Assert.IsType<ManualInstallationRecipe>(mod.Installation);
        Assert.Equal("wrapper", recipe.SourceRoot);
        Assert.Equal("Data", recipe.Destination);
    }

    [Fact]
    public void Validate_RejectsFutureSchema()
    {
        var manifest = CreateValidManifest();
        manifest.SchemaVersion++;

        Assert.Contains(ModListManifestValidator.Validate(manifest), error => error.Contains("Schema version", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMachineLocalPaths()
    {
        var manifest = CreateValidManifest();
        manifest.Profile.Variables["LocalFiles"] = @"C:\Users\someone\mods";

        Assert.Contains(ModListManifestValidator.Validate(manifest), error => error.Contains("machine-local", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsCredentialOrQueryBearingPageUrl()
    {
        var manifest = CreateValidManifest();
        var mod = Assert.IsType<ModListModEntry>(Assert.Single(manifest.Content));
        mod.Source = mod.Source! with { PageUrl = "https://example.test/mod?key=secret" };

        Assert.Contains(ModListManifestValidator.Validate(manifest), error => error.Contains("page URL", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDuplicateOrNonContiguousOrder()
    {
        var manifest = CreateValidManifest();
        manifest.Content.Add(new ModListGuidedFolderEntry
        {
            EntryId = "private-files",
            Order = 1,
            Name = "Private files",
            Instructions = "Choose the private archive."
        });
        manifest.Content.Add(new ModListGuidedFolderEntry
        {
            EntryId = "generated-files",
            Order = 4,
            Name = "Generated files",
            Instructions = "Generate these files with the required tool."
        });

        var errors = ModListManifestValidator.Validate(manifest);
        Assert.Contains(errors, error => error.Contains("duplicated", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("contiguous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_RejectsUnknownFields()
    {
        var path = Path.Combine(_root, "unknown.wpmodlist.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "listId": "test-list",
              "revision": 1,
              "name": "Test list",
              "author": "",
              "description": "",
              "game": { "definitionId": "skyrim-se", "minimumDefinitionVersion": 1 },
              "profile": { "variables": {}, "mergedViews": [] },
              "content": [],
              "tools": [],
              "command": "powershell.exe"
            }
            """, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<JsonException>(() => _serializer.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    private static ModListManifest CreateValidManifest() => new()
    {
        ListId = "test-list",
        Revision = 1,
        Name = "Test list",
        Game = new ModListGameRequirement { DefinitionId = "skyrim-se", MinimumDefinitionVersion = 2 },
        Content =
        {
            new ModListModEntry
            {
                EntryId = "test-mod",
                Order = 1,
                Name = "Test mod",
                Version = "1.0",
                Source = new RemoteRef("nexus", "skyrimspecialedition", "42", "84", "https://example.test/mod"),
                Archive = new ModListArchiveRequirement
                {
                    FileName = "test-mod.zip",
                    Sha256 = new string('A', 64),
                    Md5 = new string('B', 32),
                    SizeInBytes = 1024
                },
                Installation = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "Data" }
            }
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
