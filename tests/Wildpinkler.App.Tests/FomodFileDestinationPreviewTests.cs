using System;
using System.Collections.Generic;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Pages;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class FomodFileDestinationPreviewTests
{
    private static FomodFileInstall File(string source, string destination)
        => new() { Source = source, Destination = destination };

    private static FomodFileInstall Folder(string path)
        => new() { Source = path, IsFolder = true };

    [Fact]
    public void EmptyYieldsNoFilesLine()
    {
        var lines = ProfileContentView.BuildFileDestinationPreview(Array.Empty<FomodFileInstall>());

        Assert.Equal(new[] { "(no files)" }, lines);
    }

    [Fact]
    public void FilesShowSourceAndDestination()
    {
        var files = new[]
        {
            File("esp\\loot.esp", "Data\\loot.esp"),
            File("README.txt", "")
        };

        var lines = ProfileContentView.BuildFileDestinationPreview(files);

        Assert.Equal(
            new[] { "  esp/loot.esp → Data/loot.esp", "  README.txt → " },
            lines);
    }

    [Fact]
    public void FoldersAreMarkedAndTrailingSlashed()
    {
        var files = new[] { Folder("meshes\\trees") };

        var lines = ProfileContentView.BuildFileDestinationPreview(files);

        Assert.Equal(new[] { "▸ meshes/trees/" }, lines);
    }

    [Fact]
    public void DuplicateFoldersAreListedOnce()
    {
        var files = new[]
        {
            Folder("meshes"),
            File("meshes\\tree.nif", "meshes\\tree.nif"),
            Folder("meshes")
        };

        var lines = ProfileContentView.BuildFileDestinationPreview(files);

        Assert.Contains("▸ meshes/", lines);
        Assert.Equal(2, lines.Count); // one folder + one file
    }

    [Fact]
    public void PreservesResolutionOrder()
    {
        var files = new[]
        {
            File("b.esp", "Data\\b.esp"),
            File("a.esp", "Data\\a.esp")
        };

        var lines = ProfileContentView.BuildFileDestinationPreview(files);

        Assert.StartsWith("  b.esp", lines[0]);
        Assert.StartsWith("  a.esp", lines[1]);
    }

    [Fact]
    public void OverflowsAreCappedWithMoreLine()
    {
        var files = new List<FomodFileInstall>();
        for (var i = 0; i < 5; i++)
            files.Add(File($"f{i}.esp", $"Data\\f{i}.esp"));

        var lines = ProfileContentView.BuildFileDestinationPreview(files, max: 3);

        Assert.Equal(4, lines.Count);
        Assert.Equal("and 2 more…", lines[^1]);
    }
}
