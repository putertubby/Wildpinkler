using System;
using System.Collections.Generic;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ResolveRootNameTests
{
    private static ProfileFolder Folder(string modId, string name, ProfileFolderKind kind = ProfileFolderKind.Mod)
        => new() { Id = modId, ModId = modId, Name = name, Kind = kind };

    [Fact]
    public void FolderWithMatchingRemoteIdentity_ReturnsFolderName()
    {
        var folder = Folder("lib1", "My Mod Name");
        var mod = new ModEntry { Id = "lib1", Name = "Library Name" };
        mod.Remote = new RemoteRef("nexus", "skse", "1234");
        var root = new RemoteRef("nexus", "skse", "1234");

        var name = ProfileContentView.ResolveRootName(
            root,
            new List<ProfileFolder> { folder },
            new List<ModEntry> { mod });

        Assert.Equal("My Mod Name", name);
    }

    [Fact]
    public void NoMatch_FallsBackToModKey()
    {
        var folder = Folder("lib1", "Other Mod");
        var mod = new ModEntry { Id = "lib1", Name = "Other" };
        mod.Remote = new RemoteRef("nexus", "skse", "999");
        var root = new RemoteRef("nexus", "skse", "1234");

        var name = ProfileContentView.ResolveRootName(
            root,
            new List<ProfileFolder> { folder },
            new List<ModEntry> { mod });

        Assert.Equal("1234", name);
    }

    [Fact]
    public void FolderWithoutRemote_FallsBackToModKey()
    {
        var folder = Folder("lib1", "Local Mod");
        var mod = new ModEntry { Id = "lib1", Name = "Local" };
        var root = new RemoteRef("nexus", "skse", "1234");

        var name = ProfileContentView.ResolveRootName(
            root,
            new List<ProfileFolder> { folder },
            new List<ModEntry> { mod });

        Assert.Equal("1234", name);
    }
}
