using System;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileFolderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-folder-service-" + Guid.NewGuid().ToString("N"));

    public ProfileFolderServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (Profile Profile, ProfileTool Binding, ToolEntry Tool, ProfileFolderService Service) CreateProvisionedProfile()
    {
        var game = new GameEntry
        {
            Id = "game",
            Name = "Test game",
            InstallPath = Path.Combine(_root, "game")
        };
        Directory.CreateDirectory(game.InstallPath);

        var profile = new Profile
        {
            Id = "profile",
            Name = "Profile",
            GameId = game.Id
        };
        var service = new ProfileFolderService(_root);
        service.Provision(profile, game);

        var tool = new ToolEntry
        {
            Id = "tool",
            Name = "Tool",
            InstallPath = Path.Combine(_root, "tool"),
            ExecutableRelativePath = "run.exe",
            SourceKind = ToolSourceKind.Manual
        };
        var binding = new ProfileTool
        {
            ToolEntryId = "tool",
            IsEnabled = true,
            OutputVersion = 1
        };
        profile.Tools.Add(binding);

        return (profile, binding, tool, service);
    }

    private static ProfileFolder GetToolOutputFolderEntry(Profile profile) =>
        profile.LoadOrder.Single(folder => folder.Kind == ProfileFolderKind.ToolOutput);

    [Fact]
    public void EnableTool_InsertsBranchBelowOverlay()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();

        service.EnableTool(profile, binding, tool);

        var folder = GetToolOutputFolderEntry(profile);
        Assert.Equal(1, profile.LoadOrder.IndexOf(folder));
        Assert.Equal(ProfileFolderKind.Overlay, profile.LoadOrder[0].Kind);
        Assert.Equal(ProfileFolderKind.ToolOutput, folder.Kind);
        Assert.Equal("tool", folder.ToolEntryId);
        Assert.Equal(ProfileFolderService.GetToolOutputFolder(profile, "tool", 1), folder.Path);
        Assert.True(Directory.Exists(folder.Path));
        Assert.Equal(folder.Id, binding.OutputFolderId);
    }

    [Fact]
    public void EnableTool_IsIdempotentWhenAlreadyBound()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();

        var first = service.EnableTool(profile, binding, tool);
        var second = service.EnableTool(profile, binding, tool);

        Assert.Same(first, second);
        Assert.Single(profile.LoadOrder, folder => folder.Kind == ProfileFolderKind.ToolOutput);
        Assert.Equal(first.Id, binding.OutputFolderId);
    }

    [Fact]
    public void BeginToolRun_CreatesNextVersionFolder()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);

        var pending = service.BeginToolRun(profile, binding, "tool");

        Assert.Equal(2, pending.Version);
        Assert.Equal(ProfileFolderService.GetToolOutputFolder(profile, "tool", 2), pending.FolderPath);
        Assert.True(Directory.Exists(pending.FolderPath));
        Assert.Equal(1, binding.OutputVersion);
    }

    [Fact]
    public void CompleteToolRun_PromotesPathAndVersion()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        var pending = service.BeginToolRun(profile, binding, "tool");

        service.CompleteToolRun(profile, binding, "tool", pending);

        Assert.Equal(2, binding.OutputVersion);
        Assert.Equal(ProfileFolderService.GetToolOutputFolder(profile, "tool", 2), GetToolOutputFolderEntry(profile).Path);
        // The previous version is left for garbage collection.
        Assert.True(Directory.Exists(ProfileFolderService.GetToolOutputFolder(profile, "tool", 1)));
    }

    [Fact]
    public void CompleteToolRun_ThrowsWhenStale()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        var pending = service.BeginToolRun(profile, binding, "tool");

        // Another run was finalized in the meantime, so this pending version is no longer current.
        binding.OutputVersion = 2;

        Assert.Throws<InvalidOperationException>(() => service.CompleteToolRun(profile, binding, "tool", pending));
        Assert.Equal(2, binding.OutputVersion);
    }

    [Fact]
    public void AbandonToolRun_DeletesPendingFolder()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        var pending = service.BeginToolRun(profile, binding, "tool");
        File.WriteAllText(Path.Combine(pending.FolderPath, "partial.txt"), "partial");

        service.AbandonToolRun(pending);

        Assert.False(Directory.Exists(pending.FolderPath));
        Assert.Equal(1, binding.OutputVersion);
    }

    [Fact]
    public void DiscardToolOutput_WithPreviousVersion_RevertsToPrevious()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        service.CompleteToolRun(profile, binding, "tool", service.BeginToolRun(profile, binding, "tool"));
        Assert.Equal(2, binding.OutputVersion);

        var current = service.DiscardToolOutput(profile, binding, "tool", 2);

        Assert.Equal(1, current);
        Assert.Equal(1, binding.OutputVersion);
        Assert.False(Directory.Exists(ProfileFolderService.GetToolOutputFolder(profile, "tool", 2)));
        Assert.True(Directory.Exists(ProfileFolderService.GetToolOutputFolder(profile, "tool", 1)));
    }

    [Fact]
    public void DiscardToolOutput_WithoutPreviousVersion_RecreatesEmptyPrevious()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        var v1 = ProfileFolderService.GetToolOutputFolder(profile, "tool", 1);
        Directory.Delete(v1, recursive: true);
        Assert.False(Directory.Exists(v1));

        var current = service.DiscardToolOutput(profile, binding, "tool", 1);

        Assert.Equal(1, current);
        Assert.Equal(1, binding.OutputVersion);
        Assert.True(Directory.Exists(v1));
        Assert.Empty(Directory.EnumerateFileSystemEntries(v1));
    }

    [Fact]
    public void DiscardToolOutput_KeepsLoadOrderFolderConsistent()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        service.CompleteToolRun(profile, binding, "tool", service.BeginToolRun(profile, binding, "tool"));
        var folder = GetToolOutputFolderEntry(profile);
        Assert.Equal(2, binding.OutputVersion);

        service.DiscardToolOutput(profile, binding, "tool", 2);

        Assert.Same(folder, GetToolOutputFolderEntry(profile));
        Assert.Equal(binding.OutputFolderId, folder.Id);
        Assert.Equal(ProfileFolderService.GetToolOutputFolder(profile, "tool", 1), folder.Path);
        Assert.Equal(1, binding.OutputVersion);
    }

    [Fact]
    public void ReconcileToolOutput_PromotesNonemptyOrphan()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        var orphan = ProfileFolderService.GetToolOutputFolder(profile, "tool", 2);
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "result.txt"), "captured");

        var result = service.ReconcileToolOutput(profile, binding, "tool");

        Assert.True(result.Promoted);
        Assert.Equal(2, result.Version);
        Assert.False(result.DeletedEmpty);
        Assert.Equal(2, binding.OutputVersion);
        Assert.Equal(orphan, GetToolOutputFolderEntry(profile).Path);
    }

    [Fact]
    public void ReconcileToolOutput_DeletesEmptyOrphan()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        var orphan = ProfileFolderService.GetToolOutputFolder(profile, "tool", 2);
        Directory.CreateDirectory(orphan);

        var result = service.ReconcileToolOutput(profile, binding, "tool");

        Assert.False(result.Promoted);
        Assert.Equal(1, result.Version);
        Assert.True(result.DeletedEmpty);
        Assert.Equal(1, binding.OutputVersion);
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void ReconcileToolOutput_NoOrphan_Noop()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);

        var result = service.ReconcileToolOutput(profile, binding, "tool");

        Assert.False(result.Promoted);
        Assert.Equal(1, result.Version);
        Assert.False(result.DeletedEmpty);
        Assert.Equal(1, binding.OutputVersion);
    }

    [Fact]
    public void ClearToolOutput_ResetsToVersionOne()
    {
        var (profile, binding, tool, service) = CreateProvisionedProfile();
        service.EnableTool(profile, binding, tool);
        service.CompleteToolRun(profile, binding, "tool", service.BeginToolRun(profile, binding, "tool"));
        File.WriteAllText(
            Path.Combine(ProfileFolderService.GetToolOutputFolder(profile, "tool", 2), "leftover.txt"),
            "old");
        Assert.Equal(2, binding.OutputVersion);

        var current = service.ClearToolOutput(profile, "tool");

        Assert.Equal(1, current);
        Assert.Equal(1, binding.OutputVersion);
        var root = ProfileFolderService.GetToolOutputRoot(profile);
        Assert.Single(Directory.EnumerateDirectories(root));
        Assert.True(Directory.Exists(ProfileFolderService.GetToolOutputFolder(profile, "tool", 1)));
        Assert.Equal(ProfileFolderService.GetToolOutputFolder(profile, "tool", 1), GetToolOutputFolderEntry(profile).Path);
    }
}
