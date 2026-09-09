using System;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListCatalogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-modlist-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly ModListManifestSerializer _serializer = new();

    public ModListCatalogStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ImportAsync_IsIdempotentForIdenticalRevision()
    {
        var source = await WriteSourceAsync(CreateManifest());
        var store = new ModListCatalogStore(_serializer, _root);

        var first = await store.ImportAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var second = await store.ImportAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ModListImportStatus.Imported, first.Status);
        Assert.Equal(ModListImportStatus.AlreadyPresent, second.Status);
        Assert.Equal(first.Entry.Key, second.Entry.Key);
        Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportAsync_RequiresExplicitReplacementForChangedRevision()
    {
        var store = new ModListCatalogStore(_serializer, _root);
        var source = await WriteSourceAsync(CreateManifest());
        await store.ImportAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var changed = CreateManifest();
        changed.Description = "Changed content";
        await _serializer.SaveAsync(changed, source, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ModListRevisionConflictException>(
            () => store.ImportAsync(source, cancellationToken: TestContext.Current.CancellationToken));
        var replaced = await store.ImportAsync(source, replace: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ModListImportStatus.Replaced, replaced.Status);
        Assert.Equal("Changed content", replaced.Entry.Manifest.Description);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCatalogRevision()
    {
        var store = new ModListCatalogStore(_serializer, _root);
        var imported = await store.ImportAsync(
            await WriteSourceAsync(CreateManifest()), cancellationToken: TestContext.Current.CancellationToken);

        await store.DeleteAsync(imported.Entry, TestContext.Current.CancellationToken);

        Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GradeEvaluator_MarksManualAcquisitionAsGuidedAndMissingInstructionsAsUnavailable()
    {
        var manifest = CreateManifest();
        var mod = Assert.IsType<ModListModEntry>(Assert.Single(manifest.Content));
        mod.Source = null;
        mod.AcquisitionInstructions = "Choose the archive from your backup.";
        Assert.Equal(ModListGrade.Guided, ModListGradeResolver.Evaluate(manifest).Grade);

        mod.AcquisitionInstructions = string.Empty;
        Assert.Equal(ModListGrade.Unavailable, ModListGradeResolver.Evaluate(manifest).Grade);
    }

    private async Task<string> WriteSourceAsync(ModListManifest manifest)
    {
        var path = Path.Combine(_root, "source.wpmodlist.json");
        await _serializer.SaveAsync(manifest, path, TestContext.Current.CancellationToken);
        return path;
    }

    private static ModListManifest CreateManifest() => new()
    {
        ListId = "test-list",
        Name = "Test list",
        Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
        Content =
        {
            new ModListModEntry
            {
                EntryId = "test-mod",
                Order = 1,
                Name = "Test mod",
                AcquisitionInstructions = "Choose the archive.",
                Archive = new ModListArchiveRequirement { FileName = "test.zip", Sha256 = new string('A', 64) },
                Installation = new ManualInstallationRecipe()
            }
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
