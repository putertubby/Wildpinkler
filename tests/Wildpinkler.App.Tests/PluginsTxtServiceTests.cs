using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class PluginsTxtServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-pluginstxt-" + Guid.NewGuid().ToString("N"));
    private readonly PluginsTxtService _service = new();

    public PluginsTxtServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ScanEffectivePlugins_ShadowsLowerBranchAndMapsModIds()
    {
        var installPath = Path.Combine(_root, "game");
        var branch0 = Path.Combine(_root, "mod-a");
        var branch1 = Path.Combine(_root, "mod-b");
        WritePlugin(Path.Combine(branch0, "Data"), "Alpha.esp");
        WritePlugin(Path.Combine(branch1, "Data"), "Alpha.esp"); // shadowed by branch 0
        WritePlugin(Path.Combine(branch1, "Data", "Plugins"), "Nested.esp");
        File.WriteAllText(Path.Combine(branch1, "Data", "notes.txt"), "not a plugin");

        var profile = BuildProfile((branch0, "mod-a"), (branch1, "mod-b"));
        var target = GameTarget(installPath, new MergedView
        {
            Name = "Data",
            MountPath = installPath,
            Branches = { branch0, branch1 },
            IsWritable = false
        });

        var records = _service.ScanEffectivePlugins(target, profile, new GamePluginList(), installPath);

        // Alpha.esp (branch 0 shadows branch 1) + Nested.esp = 2 effective plugins.
        Assert.Equal(2, records.Count);
        var alpha = Assert.Single(records, record => record.FileName == "Alpha.esp");
        Assert.Equal(0, alpha.BranchIndex);
        Assert.Equal(branch0, alpha.WinningBranch);
        Assert.Equal("mod-a", alpha.SourceFolderId);
        Assert.Equal("Alpha.esp", alpha.RelativePath);

        var nested = Assert.Single(records, record => record.FileName == "Nested.esp");
        Assert.Equal("mod-b", nested.SourceFolderId);
        Assert.Equal(Path.Combine("Plugins", "Nested.esp"), nested.RelativePath);
    }

    [Fact]
    public void ScanEffectivePlugins_ReadsMastersFromTes4Header()
    {
        var installPath = Path.Combine(_root, "game");
        var branch = Path.Combine(_root, "mod-a");
        Directory.CreateDirectory(Path.Combine(branch, "Data"));
        File.WriteAllBytes(Path.Combine(branch, "Data", "Alpha.esp"), MakeTes4("Skyrim.esm", "Update.esm"));

        var profile = BuildProfile((branch, "mod-a"));
        var target = GameTarget(installPath, new MergedView
        {
            Name = "Data",
            MountPath = installPath,
            Branches = { branch }
        });

        var record = Assert.Single(_service.ScanEffectivePlugins(target, profile, new GamePluginList(), installPath));
        Assert.Equal(new[] { "Skyrim.esm", "Update.esm" }, record.Masters);
    }

    [Fact]
    public void ScanEffectivePlugins_NoMatchingViewOrMissingDataFolder_ReturnsEmpty()
    {
        var installPath = Path.Combine(_root, "game");
        var otherPath = Path.Combine(_root, "elsewhere");
        var branch = Path.Combine(_root, "mod-a");
        WritePlugin(Path.Combine(branch, "Data"), "Alpha.esp");
        var profile = BuildProfile((branch, "mod-a"));
        var cfg = new GamePluginList();

        // The merged view is mounted somewhere else entirely.
        var target = GameTarget(installPath, new MergedView
        {
            Name = "Data",
            MountPath = otherPath,
            Branches = { branch }
        });
        Assert.Empty(_service.ScanEffectivePlugins(target, profile, cfg, installPath));

        // The branch exists but has no Data folder.
        var emptyBranch = Path.Combine(_root, "empty");
        var emptyTarget = GameTarget(installPath, new MergedView
        {
            Name = "Data",
            MountPath = installPath,
            Branches = { emptyBranch }
        });
        Assert.Empty(_service.ScanEffectivePlugins(emptyTarget, profile, cfg, installPath));
    }

    [Fact]
    public void ResolveOrder_SortsByBranchThenExtensionThenName()
    {
        var plugins = new[]
        {
            Record("B.esp", branch: 0),
            Record("A.esp", branch: 0),
            Record("Master.esm", branch: 0),
            Record("Lite.esl", branch: 0),
            Record("C.esp", branch: 1)
        };

        Assert.Equal(
            new[] { "Master.esm", "Lite.esl", "A.esp", "B.esp", "C.esp" },
            _service.ResolveOrder(plugins, new GamePluginList()));
    }

    [Fact]
    public void ResolveOrder_PutsMastersBeforeDependents()
    {
        // Base order would put Aaa.esp first, but it depends on Zzz.esp.
        var plugins = new[]
        {
            Record("Aaa.esp", 0, null, "Zzz.esp", "Other.esm"),
            Record("Zzz.esp"),
            Record("Other.esm")
        };

        Assert.Equal(new[] { "Other.esm", "Zzz.esp", "Aaa.esp" }, _service.ResolveOrder(plugins, new GamePluginList()));
    }

    [Fact]
    public void ResolveOrder_IgnoresMissingMastersAndSelfMasters()
    {
        var plugins = new[]
        {
            Record("Aaa.esp", 0, null, "NotPresent.esp", "Aaa.esp")
        };

        Assert.Equal(new[] { "Aaa.esp" }, _service.ResolveOrder(plugins, new GamePluginList()));
    }

    [Fact]
    public void ResolveOrder_CycleAppendsRemainderInBaseOrder()
    {
        var plugins = new[]
        {
            Record("One.esp", masters: "Two.esp"),
            Record("Two.esp", masters: "One.esp")
        };

        Assert.Equal(new[] { "One.esp", "Two.esp" }, _service.ResolveOrder(plugins, new GamePluginList()));
    }

    [Fact]
    public async Task WriteAsync_WritesOneNamePerLineToBranch0OfNamedWritableView()
    {
        var profile = BuildProfile();
        var namedView = new MergedView
        {
            Name = "LocalAppData",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        };
        var otherWritable = new MergedView
        {
            Name = "Data",
            MountPath = _root,
            Branches = { Path.Combine(_root, "elsewhere") },
            IsWritable = true
        };
        var target = GameTarget(_root, namedView, otherWritable);
        var cfg = new GamePluginList { ListViewVariable = "LocalAppData" };

        var destination = await _service.WriteAsync(target, profile, cfg, new[] { "Alpha.esp", "Beta.esp" });

        Assert.Equal(Path.Combine(_root, "profile", "localappdata", "plugins.txt"), destination);
        Assert.Equal("Alpha.esp\nBeta.esp\n", File.ReadAllText(destination!));
        Assert.False(File.Exists(destination! + ".bak"));
        Assert.False(File.Exists(Path.Combine(_root, "elsewhere", "plugins.txt")));
    }

    [Fact]
    public async Task WriteAsync_EmptyListWritesEmptyFile()
    {
        var profile = BuildProfile();
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        });
        var cfg = new GamePluginList { ListViewVariable = "LocalAppData" };

        var destination = await _service.WriteAsync(target, profile, cfg, Array.Empty<string>());

        Assert.Equal(string.Empty, File.ReadAllText(destination!));
    }

    [Fact]
    public async Task WriteAsync_SecondWriteKeepsPreviousContentAsBackup()
    {
        var profile = BuildProfile();
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        });
        var cfg = new GamePluginList { ListViewVariable = "LocalAppData" };

        var destination = await _service.WriteAsync(target, profile, cfg, new[] { "Alpha.esp" });
        var second = await _service.WriteAsync(target, profile, cfg, new[] { "Alpha.esp", "Beta.esp" });

        Assert.Equal(destination, second);
        Assert.Equal("Alpha.esp\nBeta.esp\n", File.ReadAllText(destination!));
        Assert.Equal("Alpha.esp\n", File.ReadAllText(destination! + ".bak"));
    }

    [Fact]
    public async Task WriteAsync_FallsBackToProfileRootedWritableViewWhenNoNamedView()
    {
        var profile = BuildProfile();
        var fallback = new MergedView
        {
            Name = "Renamed",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        };
        var readOnly = new MergedView
        {
            Name = "Other",
            MountPath = _root,
            Branches = { Path.Combine(_root, "read-only") },
            IsWritable = false
        };
        var target = GameTarget(_root, readOnly, fallback);

        var destination = await _service.WriteAsync(target, profile, new GamePluginList(), new[] { "Alpha.esp" });

        Assert.Equal(Path.Combine(_root, "profile", "localappdata", "plugins.txt"), destination);
    }

    [Fact]
    public async Task WriteAsync_NoSuitableView_ReturnsNull()
    {
        var profile = BuildProfile();
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = _root,
            Branches = { Path.Combine(_root, "read-only") },
            IsWritable = false
        });

        Assert.Null(await _service.WriteAsync(target, profile, new GamePluginList { ListViewVariable = "LocalAppData" }, new[] { "Alpha.esp" }));
        Assert.False(Directory.Exists(Path.Combine(_root, "read-only")));
    }

    [Fact]
    public async Task EnsureUpToDateAsync_RegeneratesListFromCurrentLoadOrder()
    {
        var installPath = Path.Combine(_root, "game");
        var branch = Path.Combine(_root, "mod-a");
        var dataFolder = Path.Combine(branch, "Data");
        Directory.CreateDirectory(dataFolder);
        File.WriteAllBytes(Path.Combine(dataFolder, "Dependant.esp"), MakeTes4("Master.esm"));
        File.WriteAllBytes(Path.Combine(dataFolder, "Master.esm"), Array.Empty<byte>());

        var profile = BuildProfile((branch, "mod-a"));
        profile.FolderPath = Path.Combine(_root, "profile");
        var target = GameTarget(installPath,
            new MergedView
            {
                Name = "Data",
                MountPath = installPath,
                Branches = { branch }
            },
            new MergedView
            {
                Name = "LocalAppData",
                MountPath = _root,
                Branches = { Path.Combine(_root, "profile", "localappdata") },
                IsWritable = true
            });
        var game = new GameEntry
        {
            Id = "game",
            Name = "Game",
            InstallPath = installPath,
            Definition = new GameDefinition
            {
                DefinitionId = "test-game",
                Name = "Test Game",
                PluginList = new GamePluginList { ListViewVariable = "LocalAppData" }
            }
        };

        await _service.EnsureUpToDateAsync(profile, target, game);

        Assert.Equal(
            "Master.esm\nDependant.esp\n",
            File.ReadAllText(Path.Combine(_root, "profile", "localappdata", "plugins.txt")));
    }

    [Fact]
    public async Task EnsureUpToDateAsync_NoPluginListDescriptorOrNonGameTarget_DoesNothing()
    {
        var installPath = Path.Combine(_root, "game");
        var profile = BuildProfile();
        var writable = new MergedView
        {
            Name = "LocalAppData",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        };
        var target = GameTarget(installPath, writable);
        var destination = Path.Combine(_root, "profile", "localappdata", "plugins.txt");

        // No plugin list descriptor on the definition.
        var game = new GameEntry { Id = "game", InstallPath = installPath, Definition = new GameDefinition { DefinitionId = "x", Name = "X" } };
        await _service.EnsureUpToDateAsync(profile, target, game);
        Assert.False(File.Exists(destination));

        // Descriptor present, but the target is a tool.
        var tool = target with { Kind = LaunchTargetKind.Tool };
        game.Definition!.PluginList = new GamePluginList { ListViewVariable = "LocalAppData" };
        await _service.EnsureUpToDateAsync(profile, tool, game);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void NeedsPluginListWarning_DependsOnDescriptorAndSortedFlag()
    {
        var profile = new Profile { Id = "p", FolderPath = _root };
        var noDescriptor = new GameEntry { Id = "game" };
        Assert.False(_service.NeedsPluginListWarning(profile, noDescriptor));

        var withDescriptor = new GameEntry
        {
            Id = "game",
            Definition = new GameDefinition { DefinitionId = "x", Name = "X", PluginList = new GamePluginList() }
        };
        Assert.True(_service.NeedsPluginListWarning(profile, withDescriptor));

        profile.PluginListSorted = true;
        Assert.False(_service.NeedsPluginListWarning(profile, withDescriptor));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Profile BuildProfile(params (string Path, string ModId)[] modBranches)
    {
        var profile = new Profile
        {
            Id = "profile",
            Name = "Test",
            FolderPath = Path.Combine(_root, "profile")
        };
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "game",
            Kind = ProfileFolderKind.GameInstall,
            Path = Path.Combine(_root, "game"),
            IsLocked = true
        });
        foreach (var (path, modId) in modBranches)
        {
            profile.LoadOrder.Add(new ProfileFolder
            {
                Id = "folder-" + modId,
                Kind = ProfileFolderKind.Mod,
                Path = path,
                ModId = modId
            });
        }

        return profile;
    }

    private static LaunchTarget GameTarget(string installPath, params MergedView[] views) => new(
        "game",
        "Game",
        LaunchTargetKind.Game,
        Path.Combine(installPath, "Game.exe"),
        string.Empty,
        installPath,
        string.Empty,
        string.Empty,
        views,
        new Dictionary<string, string>(),
        Array.Empty<string>(),
        "profile.json",
        false,
        string.Empty);

    private static void WritePlugin(string dataFolder, string fileName)
    {
        Directory.CreateDirectory(dataFolder);
        File.WriteAllBytes(Path.Combine(dataFolder, fileName), MakeTes4());
    }

    private static byte[] MakeTes4(params string[] masters)
    {
        var payload = new MemoryStream();
        var writer = new BinaryWriter(payload);
        foreach (var master in masters)
        {
            var bytes = Encoding.ASCII.GetBytes(master);
            writer.Write(Encoding.ASCII.GetBytes("MAST"));
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        using var stream = new MemoryStream();
        using var header = new BinaryWriter(stream);
        header.Write(Encoding.ASCII.GetBytes("TES4"));
        header.Write((uint)payload.Length);
        header.Write(new byte[16]);
        payload.Position = 0;
        payload.CopyTo(stream);

        return stream.ToArray();
    }

    private static PluginRecord Record(string fileName, int branch = 0, string? modId = null, params string[] masters) => new()
    {
        FileName = fileName,
        RelativePath = fileName,
        WinningBranch = fileName,
        BranchIndex = branch,
        SourceFolderId = modId,
        Masters = masters.ToList()
    };
}
