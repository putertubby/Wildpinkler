using System;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileGarbageCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-gc-" + Guid.NewGuid().ToString("N"));

    public ProfileGarbageCollectorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CollectAsync_PreservesInstallationReferencedByActiveBuildJournal()
    {
        var folder = Path.Combine(_root, "install");
        Directory.CreateDirectory(folder);
        var installations = new ModInstallationStore(_root);
        await installations.SaveAsync(new[]
        {
            new ModInstallation { Id = "install", ModId = "mod", FolderPath = folder, SourceArchiveSha256 = new string('A', 64) }
        });
        var builds = new ModListBuildStore(_root);
        await builds.UpsertAsync(new ModListBuild
        {
            Id = "build",
            ListId = "list",
            ListRevision = 1,
            State = ModListBuildState.Ready,
            Artifacts = { new ModListBuildArtifact { EntryId = "entry", InstallationId = "install" } }
        }, TestContext.Current.CancellationToken);
        var collector = new ProfileGarbageCollector(installations, new ActiveRunRegistry(), builds);

        var result = await collector.CollectAsync(Array.Empty<Profile>());

        Assert.Equal(0, result.ModInstallations);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task CollectAsync_DeletesSupersededAndOrphanedToolOutputVersions_KeepsCurrent()
    {
        var folderPath = Path.Combine(_root, "profile");
        var profile = new Profile
        {
            Id = "profile",
            Name = "Profile",
            GameId = "game",
            FolderPath = folderPath,
            Tools =
            {
                new ProfileTool { ToolEntryId = "tool", IsEnabled = true, OutputVersion = 1 }
            }
        };
        Directory.CreateDirectory(folderPath);
        var v1 = Path.Combine(folderPath, "tool-output", "tool-1");
        var v2 = Path.Combine(folderPath, "tool-output", "tool-2");
        var v3 = Path.Combine(folderPath, "tool-output", "tool-3");
        Directory.CreateDirectory(v1);
        Directory.CreateDirectory(v2);
        Directory.CreateDirectory(v3);

        var collector = new ProfileGarbageCollector(new ModInstallationStore(_root), new ActiveRunRegistry());
        var result = await collector.CollectAsync(new[] { profile });

        Assert.True(Directory.Exists(v1));
        Assert.False(Directory.Exists(v2));
        Assert.False(Directory.Exists(v3));
        Assert.Equal(2, result.ToolOutputFolders);
        Assert.Equal(2, result.Total);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
