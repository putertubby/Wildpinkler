using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RemoteCollectionImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-collection-import-" + Guid.NewGuid().ToString("N"));

    public RemoteCollectionImportServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RemoteRef File(string modKey, string fileKey) =>
        new("fake", "testgame", modKey, fileKey);

    private static RemoteCollectionModFileEntry ModFile(string modKey, string fileKey, string name, string? md5 = "d41d8cd98f00b204e9800998ecf8427e") =>
        new(File(modKey, fileKey), name, null, md5, 1024, null);

    private static RemoteCollectionSubCollectionEntry Sub(string key, string name) =>
        new("testgame", key, name);

    private RemoteCollectionImportService CreateService(
        Func<RemoteCollectionRef, CancellationToken, Task<RemoteCollection>> fetch,
        Func<string, string, CancellationToken, Task<RemoteGameMapping?>>? map = null,
        ModListCatalogStore? catalog = null)
    {
        catalog ??= new ModListCatalogStore(new ModListManifestSerializer(), _root);
        // A local game must map, so default to a known game unless the test overrides it.
        var mapper = map ?? ((_, _, _) => Task.FromResult<RemoteGameMapping?>(GameMapping("skyrim-se")));
        return new RemoteCollectionImportService(fetch, mapper, catalog, new ModListManifestSerializer());
    }

    private static RemoteGameMapping GameMapping(string definitionId) =>
        new(new GameDefinition { DefinitionId = definitionId, DefinitionVersion = 1, Name = "Skyrim" },
            new[] { new GameEntry { Id = "game-1", Name = "Skyrim", DefinitionId = definitionId, DefinitionVersion = 1 } },
            Array.Empty<Profile>());

    [Fact]
    public async Task ImportAsync_FlattensNestedCollectionsAndDeduplicates()
    {
        // Root contains mod A, then a sub-collection containing mod B and a duplicate of A.
        var rootCollection = new RemoteCollection("fake", "testgame", "100", "Root", null, 3,
            new List<RemoteCollectionEntry>
            {
                ModFile("1", "2", "Mod A"),
                Sub("200", "Sub")
            });
        var subCollection = new RemoteCollection("fake", "testgame", "200", "Sub", null, 1,
            new List<RemoteCollectionEntry>
            {
                ModFile("3", "4", "Mod B"),
                ModFile("1", "2", "Mod A (dup)")
            });

        var fetch = (RemoteCollectionRef reference, CancellationToken _) =>
            Task.FromResult(reference.CollectionKey == "100" ? rootCollection : subCollection);

        var service = CreateService(fetch);
        var result = await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 3), Token);

        var names = result.Manifest.Content.Select(entry => entry.Name).ToList();
        // Mod A appears once (deduped), Mod B appears after it; order preserved.
        Assert.Equal(new[] { "Mod A", "Mod B" }, names);
        Assert.Equal(3, result.Manifest.Revision);
    }

    [Fact]
    public async Task ImportAsync_MarksArchivesPendingAndRecordsProvenance()
    {
        var collection = new RemoteCollection("fake", "testgame", "100", "Root", "desc", 1,
            new List<RemoteCollectionEntry> { ModFile("1", "2", "Mod A") });
        var service = CreateService((_, _) => Task.FromResult(collection));
        var result = await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 1), Token);

        var mod = Assert.IsType<ModListModEntry>(result.Manifest.Content.Single());
        Assert.True(mod.Archive.Sha256Pending);
        Assert.Equal(string.Empty, mod.Archive.Sha256);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", mod.Archive.Md5);
        Assert.NotNull(mod.Source);
        Assert.NotNull(result.Manifest.SourceCollection);
        Assert.Equal("100", result.Manifest.SourceCollection!.CollectionKey);
        Assert.Equal(1, result.Manifest.SourceCollection!.Revision);
        Assert.StartsWith("collection-", result.Manifest.ListId);
    }

    [Fact]
    public async Task ImportAsync_WithLocalGame_LeavesAcquisitionAutomatic()
    {
        var collection = new RemoteCollection("fake", "testgame", "100", "Root", null, 1,
            new List<RemoteCollectionEntry> { ModFile("1", "2", "Mod A") });
        var service = CreateService(
            (_, _) => Task.FromResult(collection),
            map: (_, _, _) => Task.FromResult<RemoteGameMapping?>(GameMapping("skyrim-se")));
        var result = await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 1), Token);

        Assert.Equal("skyrim-se", result.Manifest.Game.DefinitionId);
        var mod = Assert.IsType<ModListModEntry>(result.Manifest.Content.Single());
        Assert.Equal(string.Empty, mod.AcquisitionInstructions);
    }

    [Fact]
    public async Task ImportAsync_WithoutLocalGame_IsRefusedWithExplanation()
    {
        var collection = new RemoteCollection("fake", "testgame", "100", "Root", null, 1,
            new List<RemoteCollectionEntry> { ModFile("1", "2", "Mod A") });
        var service = CreateService((_, _) => Task.FromResult(collection), map: (_, _, _) => Task.FromResult<RemoteGameMapping?>(null));

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 1), Token));
        Assert.Contains("testgame", exception.Message);
    }

    [Fact]
    public async Task ImportAsync_IsIdempotentForSameRevision()
    {
        var collection = new RemoteCollection("fake", "testgame", "100", "Root", null, 1,
            new List<RemoteCollectionEntry> { ModFile("1", "2", "Mod A") });
        var service = CreateService((_, _) => Task.FromResult(collection));

        var first = await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 1), Token);
        var second = await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 1), Token);

        Assert.Equal(ModListImportStatus.AlreadyPresent, second.Import.Status);
        Assert.Equal(first.Manifest.ListId, second.Manifest.ListId);
        Assert.Equal(1, second.Manifest.Revision);
    }

    [Fact]
    public async Task ImportAsync_NewerRevisionCreatesNewCatalogRevision()
    {
        var v1 = new RemoteCollection("fake", "testgame", "100", "Root", null, 1,
            new List<RemoteCollectionEntry> { ModFile("1", "2", "Mod A") });
        var v2 = new RemoteCollection("fake", "testgame", "100", "Root", null, 2,
            new List<RemoteCollectionEntry> { ModFile("1", "2", "Mod A"), ModFile("5", "6", "Mod C") });
        var service = CreateService((reference, _) => Task.FromResult(reference.Revision == 2 ? v2 : v1));

        await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 1), Token);
        var result = await service.ImportAsync(new RemoteCollectionRef("fake", "testgame", "100", "100", 2), Token);

        Assert.Equal(2, result.Manifest.Revision);
        Assert.Equal(2, result.Manifest.Content.Count);
    }
}

public sealed class CollectionSha256ValidationTests
{
    [Fact]
    public void Validator_AcceptsPendingArchiveWithEmptySha256()
    {
        var manifest = new ModListManifest
        {
            ListId = "test-list",
            Name = "List",
            Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
            Content =
            {
                new ModListModEntry
                {
                    EntryId = "mod-1",
                    Order = 1,
                    Name = "Mod A",
                    Source = new RemoteRef("fake", "testgame", "1", "2"),
                    Archive = new ModListArchiveRequirement { FileName = "a.zip", Sha256 = string.Empty, Md5 = "d41d8cd98f00b204e9800998ecf8427e", Sha256Pending = true },
                    Installation = new GuidedInstallationRecipe { Instructions = "Install manually." }
                }
            }
        };
        Assert.Empty(ModListManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Validator_RejectsNonPendingArchiveWithEmptySha256()
    {
        var manifest = new ModListManifest
        {
            ListId = "test-list",
            Name = "List",
            Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
            Content =
            {
                new ModListModEntry
                {
                    EntryId = "mod-1",
                    Order = 1,
                    Name = "Mod A",
                    Source = new RemoteRef("fake", "testgame", "1", "2"),
                    Archive = new ModListArchiveRequirement { FileName = "a.zip", Sha256 = string.Empty, Md5 = "d41d8cd98f00b204e9800998ecf8427e" },
                    Installation = new GuidedInstallationRecipe { Instructions = "Install manually." }
                }
            }
        };
        Assert.NotEmpty(ModListManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Validator_RejectsPendingArchiveWithNonEmptySha256()
    {
        var manifest = new ModListManifest
        {
            ListId = "test-list",
            Name = "List",
            Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
            Content =
            {
                new ModListModEntry
                {
                    EntryId = "mod-1",
                    Order = 1,
                    Name = "Mod A",
                    Source = new RemoteRef("fake", "testgame", "1", "2"),
                    Archive = new ModListArchiveRequirement { FileName = "a.zip", Sha256 = "not-a-hash", Sha256Pending = true },
                    Installation = new GuidedInstallationRecipe { Instructions = "Install manually." }
                }
            }
        };
        Assert.NotEmpty(ModListManifestValidator.Validate(manifest));
    }
}
