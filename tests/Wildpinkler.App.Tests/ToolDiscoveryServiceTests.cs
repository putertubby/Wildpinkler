using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services.Games;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ToolDiscoveryServiceTests
{
    [Fact]
    public void DiscoverForProfile_FindsExecutablesInEnabledModFolders()
    {
        var service = new ToolDiscoveryService();
        using var root = MakeTempFolder();
        Directory.CreateDirectory(Path.Combine(root.Path, "bin"));
        var exePath = Path.Combine(root.Path, "bin", "sorter.exe");
        File.WriteAllText(exePath, "");
        // A non-executable file must be ignored.
        File.WriteAllText(Path.Combine(root.Path, "readme.txt"), "");

        var profile = CreateProfile(new[]
        {
            CreateModFolder("mod-1", "Sorter mod", root.Path)
        });

        var candidates = service.DiscoverForProfile(profile, Array.Empty<ToolEntry>());

        var candidate = Assert.Single(candidates);
        Assert.Equal("sorter", candidate.SuggestedName);
        Assert.Equal(exePath, candidate.ExecutablePath);
        Assert.Equal(Path.Combine("bin", "sorter.exe"), candidate.RelativePath);
        Assert.Equal("Sorter mod", candidate.OriginModName);
        Assert.Equal("mod-1", candidate.OriginFolderId);
        Assert.Equal(root.Path, candidate.InstallPath);
    }

    [Fact]
    public void DiscoverForProfile_SkipsExecutablesAlreadyRepresented()
    {
        var service = new ToolDiscoveryService();
        using var root = MakeTempFolder();
        var exePath = Path.Combine(root.Path, "sorter.exe");
        File.WriteAllText(exePath, "");

        var profile = CreateProfile(new[]
        {
            CreateModFolder("mod-1", "Sorter mod", root.Path)
        });
        var existing = new ToolEntry
        {
            Id = "tool-1",
            Name = "Sorter",
            SourceKind = ToolSourceKind.Discovered,
            InstallPath = root.Path,
            ExecutableRelativePath = "sorter.exe"
        };

        var candidates = service.DiscoverForProfile(profile, new[] { existing });

        Assert.Empty(candidates);
    }

    [Fact]
    public void DiscoverForProfile_IgnoresDisabledAndNonModFolders()
    {
        var service = new ToolDiscoveryService();
        using var enabledRoot = MakeTempFolder();
        using var disabledRoot = MakeTempFolder();
        using var otherRoot = MakeTempFolder();
        File.WriteAllText(Path.Combine(enabledRoot.Path, "enabled.exe"), "");
        File.WriteAllText(Path.Combine(disabledRoot.Path, "disabled.exe"), "");
        File.WriteAllText(Path.Combine(otherRoot.Path, "other.exe"), "");

        var profile = CreateProfile(new[]
        {
            CreateModFolder("mod-on", "On mod", enabledRoot.Path),
            CreateModFolder("mod-off", "Off mod", disabledRoot.Path, isEnabled: false),
            CreateModFolder("overlay", "Overlay", otherRoot.Path, kind: ProfileFolderKind.Overlay)
        });

        var candidates = service.DiscoverForProfile(profile, Array.Empty<ToolEntry>());

        var candidate = Assert.Single(candidates);
        Assert.Equal("enabled", candidate.SuggestedName);
    }

    [Fact]
    public void DiscoverForProfile_RecursesIntoSubdirectories()
    {
        var service = new ToolDiscoveryService();
        using var root = MakeTempFolder();
        var nested = Path.Combine(root.Path, "a", "b", "deep.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "");

        var profile = CreateProfile(new[]
        {
            CreateModFolder("mod-1", "Sorter mod", root.Path)
        });

        var candidates = service.DiscoverForProfile(profile, Array.Empty<ToolEntry>());

        var candidate = Assert.Single(candidates);
        Assert.Equal("deep", candidate.SuggestedName);
        Assert.Equal(Path.Combine("a", "b", "deep.exe"), candidate.RelativePath);
    }

    [Fact]
    public void DiscoverInFolder_FindsOnlyThatFolder()
    {
        var service = new ToolDiscoveryService();
        using var root = MakeTempFolder();
        using var otherRoot = MakeTempFolder();
        var exePath = Path.Combine(root.Path, "bin", "sorter.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "");
        File.WriteAllText(Path.Combine(otherRoot.Path, "other.exe"), "");

        var folder = CreateModFolder("mod-1", "Sorter mod", root.Path);

        var candidates = service.DiscoverInFolder(folder, Array.Empty<ToolEntry>());

        var candidate = Assert.Single(candidates);
        Assert.Equal("sorter", candidate.SuggestedName);
        Assert.Equal(exePath, candidate.ExecutablePath);
        Assert.Equal(Path.Combine("bin", "sorter.exe"), candidate.RelativePath);
        Assert.Equal("Sorter mod", candidate.OriginModName);
        Assert.Equal("mod-1", candidate.OriginFolderId);
        Assert.Equal(root.Path, candidate.InstallPath);
    }

    [Fact]
    public void DiscoverInFolder_KeepsOriginMetadataForDisabledFolder()
    {
        var service = new ToolDiscoveryService();
        using var root = MakeTempFolder();
        var exePath = Path.Combine(root.Path, "sorter.exe");
        File.WriteAllText(exePath, "");

        var folder = CreateModFolder("mod-1", "Sorter mod", root.Path, isEnabled: false);

        var candidates = service.DiscoverInFolder(folder, Array.Empty<ToolEntry>());

        var candidate = Assert.Single(candidates);
        Assert.Equal("Sorter mod", candidate.OriginModName);
        Assert.Equal("mod-1", candidate.OriginFolderId);
        Assert.Equal(root.Path, candidate.InstallPath);
    }

    [Fact]
    public void DiscoverInFolder_SkipsExecutablesAlreadyRepresented()
    {
        var service = new ToolDiscoveryService();
        using var root = MakeTempFolder();
        File.WriteAllText(Path.Combine(root.Path, "sorter.exe"), "");

        var folder = CreateModFolder("mod-1", "Sorter mod", root.Path);
        var existing = new ToolEntry
        {
            Id = "tool-1",
            Name = "Sorter",
            SourceKind = ToolSourceKind.Discovered,
            InstallPath = root.Path,
            ExecutableRelativePath = "sorter.exe"
        };

        var candidates = service.DiscoverInFolder(folder, new[] { existing });

        Assert.Empty(candidates);
    }

    private static Profile CreateProfile(ProfileFolder[] loadOrder)
    {
        var profile = new Profile
        {
            Id = "profile",
            Name = "Profile",
            GameId = "game-id",
            FolderPath = "C:\\profiles\\profile"
        };
        foreach (var folder in loadOrder)
            profile.LoadOrder.Add(folder);
        return profile;
    }

    private static ProfileFolder CreateModFolder(string id, string name, string path, bool isEnabled = true, ProfileFolderKind kind = ProfileFolderKind.Mod) => new()
    {
        Id = id,
        Name = name,
        Path = path,
        Kind = kind,
        IsEnabled = isEnabled
    };

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpdisco-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static TempFolder MakeTempFolder() => new();
}
