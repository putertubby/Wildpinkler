using System;
using System.Linq;
using Wildpinkler.App.Controls;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the archive preview tree's top-level computation: the initial rows are the distinct
/// first-segment directories plus loose files at the archive root, so a folder never appears
/// both as a row and inside its own expansion (the old list+tree duplication).
/// </summary>
public class ArchiveFileTreeTopLevelTests
{
    [Fact]
    public void TopLevelEntries_ReturnsTopDirsAndLooseRootFiles_EachOnce()
    {
        // "a" and "b" are top dirs (each seen twice via nested files); "loose" is a root file.
        var entries = ArchiveFileTree.TopLevelEntries(
            new[] { "a", "a/sub", "b" },
            new[] { "a/x.zip", "a/sub/y.zip", "b/one.zip", "loose.txt" });

        Assert.Equal(3, entries.Count);
        Assert.Equal("a", entries[0].Name);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("b", entries[1].Name);
        Assert.True(entries[1].IsDirectory);
        Assert.Equal("loose.txt", entries[2].Name);
        Assert.False(entries[2].IsDirectory);
    }

    [Fact]
    public void TopLevelEntries_FoldersSortBeforeFiles_CaseInsensitive()
    {
        var entries = ArchiveFileTree.TopLevelEntries(
            new[] { "mod", "beta" },
            new[] { "mod/a.zip", "beta/b.zip", "apple.txt", "zebra.txt" });

        Assert.Equal(new[] { "beta", "mod" }, entries.Where(e => e.IsDirectory).Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "apple.txt", "zebra.txt" }, entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToArray());
    }

    [Fact]
    public void TopLevelEntries_SkipsFilesWhoseNameMatchesATopDirectory()
    {
        // A file named exactly "a" would collide with the folder row — the folder wins.
        var entries = ArchiveFileTree.TopLevelEntries(new[] { "a" }, new[] { "a/x.zip", "a" });

        Assert.Equal(new[] { ("a", true) }, entries.Select(e => (e.Name, e.IsDirectory)).ToArray());
    }

    [Fact]
    public void TopLevelEmptyEntries_WithNoLayoutAndNoFiles_ReturnsEmpty()
    {
        Assert.Empty(ArchiveFileTree.TopLevelEntries(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void TopLevelEntries_AcceptsBackslashSeparatedPaths()
    {
        var entries = ArchiveFileTree.TopLevelEntries(
            ["a\\x.zip", "b\\y.zip"],
            ["a\\x.zip", "loose.txt"]);

        Assert.Equal(new[] { ("a", true), ("b", true), ("loose.txt", false) },
            entries.Select(e => (e.Name, e.IsDirectory)).ToArray());
    }
}
