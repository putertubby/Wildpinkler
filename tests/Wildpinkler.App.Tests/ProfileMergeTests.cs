using System.Collections.ObjectModel;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

// Regression coverage for the bug where an external content-only change (e.g. an agent enabling a
// mod) never reached the Profiles page, because reconciliation either skipped profiles whose id set
// was unchanged, or replaced matched profiles without copying their new folder/tool state in place.
public sealed class ProfileMergeTests
{
    [Fact]
    public void ProfileFolder_UpdateFrom_CopiesMutableStateButNotIdentity()
    {
        var target = new ProfileFolder { Id = "folder", Name = "Old", Path = "old-path", Kind = ProfileFolderKind.Mod, IsEnabled = true, ModId = "mod" };
        var source = new ProfileFolder { Id = "folder", Name = "New", Path = "new-path", Kind = ProfileFolderKind.Mod, IsEnabled = false, ModId = "mod" };

        target.UpdateFrom(source);

        Assert.Equal("New", target.Name);
        Assert.Equal("new-path", target.Path);
        Assert.False(target.IsEnabled);
        Assert.Equal("mod", target.ModId);
    }

    [Fact]
    public void ProfileTool_UpdateFrom_CopiesMutableState()
    {
        var target = new ProfileTool { ToolEntryId = "tool", IsEnabled = false, LaunchArgumentsOverride = "old" };
        var source = new ProfileTool { ToolEntryId = "tool", IsEnabled = true, LaunchArgumentsOverride = "new" };

        target.UpdateFrom(source);

        Assert.True(target.IsEnabled);
        Assert.Equal("new", target.LaunchArgumentsOverride);
    }

    [Fact]
    public void ReconcileWithMerge_AppliesAFolderEnabledChange_WithoutReplacingTheProfileInstance()
    {
        // Simulates ProfilesPage.SyncProfilesAsync: an externally saved profile (e.g. from an agent
        // command) has the same profile id but a mod folder flipped from enabled to disabled.
        var boundFolder = new ProfileFolder { Id = "mod-a", Kind = ProfileFolderKind.Mod, ModId = "mod-a", IsEnabled = true };
        var boundProfile = new Profile { Id = "profile", Name = "Test" };
        boundProfile.LoadOrder.Add(boundFolder);
        var pageCollection = new ObservableCollection<Profile> { boundProfile };

        var externallySavedFolder = new ProfileFolder { Id = "mod-a", Kind = ProfileFolderKind.Mod, ModId = "mod-a", IsEnabled = false };
        var externallySavedProfile = new Profile { Id = "profile", Name = "Test" };
        externallySavedProfile.LoadOrder.Add(externallySavedFolder);

        CollectionReconciler.Reconcile(pageCollection, new[] { externallySavedProfile }, profile => profile.Id, MergeProfileContent);

        Assert.Same(boundProfile, pageCollection[0]);
        Assert.Same(boundFolder, boundProfile.LoadOrder[0]);
        Assert.False(boundProfile.LoadOrder[0].IsEnabled);
    }

    private static void MergeProfileContent(Profile target, Profile source)
    {
        target.Name = source.Name;
        CollectionReconciler.Reconcile(target.LoadOrder, source.LoadOrder, folder => folder.Id, (current, desired) => current.UpdateFrom(desired));
        CollectionReconciler.Reconcile(target.Tools, source.Tools, tool => tool.ToolEntryId, (current, desired) => current.UpdateFrom(desired));
    }
}
