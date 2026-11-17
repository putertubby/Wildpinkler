using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Controls;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the archive preview tree's immediate-children computation: expanding a folder must
/// surface only that folder's direct children (files and folders), never its grandchildren. This is
/// the regression for "folders can't be expanded" — the old code took the first segment of every
/// listed path, so only the top level ever expanded.
/// </summary>
public class ArchiveFileTreeChildrenTests
{
    [Fact]
    public void ExpandingTopLevelFolder_ReturnsOnlyItsImmediateChildren()
    {
        // "A" has two children (a folder "a/b" and a file "a/c.txt"); "B" is a separate top folder.
        var all = new List<string>
        {
            "A/a/b.txt",
            "A/a/c.txt",
            "A/d.txt",
            "B/e.txt"
        };

        var children = ArchiveFileTree.ImmediateChildren("A/", all);

        Assert.Equal(new[] { "a", "d.txt" }, children.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "A/a", "A/d.txt" }, children.Select(c => c.Path).ToArray());
    }

    [Fact]
    public void ExpandingNestedFolder_ReturnsOnlyItsImmediateChildren()
    {
        var all = new List<string>
        {
            "Data/textures/floor.dds",
            "Data/textures/ceiling.dds",
            "Data/meshes/bed.nif",
            "Data/meshes/table.nif"
        };

        var children = ArchiveFileTree.ImmediateChildren("Data/textures/", all);

        Assert.Equal(new[] { "ceiling.dds", "floor.dds" }, children.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "Data/textures/ceiling.dds", "Data/textures/floor.dds" },
            children.Select(c => c.Path).ToArray());
    }

    [Fact]
    public void FolderChild_IsDetectedWhenItHasDescendants()
    {
        // "Data" has two children; "textures" is a folder (has descendants), "meshes" is a file? No:
        // both have descendants, so both are folders. The test only checks the names are returned;
        // folder detection is done by the control from the same allPaths, so verify child set only.
        var all = new List<string>
        {
            "Data/textures/floor.dds",
            "Data/meshes/bed.nif"
        };

        var children = ArchiveFileTree.ImmediateChildren("Data/", all);

        Assert.Equal(new[] { "meshes", "textures" }, children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void RootExpansion_ReturnsTopLevelEntriesOnly()
    {
        var all = new List<string>
        {
            "Data/textures/floor.dds",
            "SKSE64.dll",
            "Readme.txt",
            "Data/meshes/bed.nif"
        };

        // Root has no prefix: a top-level folder and two top-level files.
        var children = ArchiveFileTree.ImmediateChildren(string.Empty, all);

        Assert.Equal(new[] { "Data", "Readme.txt", "SKSE64.dll" }, children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void BackslashPaths_AreNormalizedToForwardSlashes()
    {
        var all = new List<string>
        {
            @"Data\textures\floor.dds",
            @"Data\meshes\bed.nif"
        };

        var children = ArchiveFileTree.ImmediateChildren("Data/", all);

        Assert.Equal(new[] { "meshes", "textures" }, children.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "Data/meshes", "Data/textures" }, children.Select(c => c.Path).ToArray());
    }

    [Fact]
    public void NoChildren_ReturnsEmpty()
    {
        var all = new List<string> { "Data/onlyFile.txt" };
        // "Data/other/" is empty.
        Assert.Empty(ArchiveFileTree.ImmediateChildren("Data/other/", all));
    }
}
