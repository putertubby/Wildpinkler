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
    public void ScanEffectivePlugins_SkipsGameInstallAndOverlayFolders()
    {
        var gameData = Path.Combine(_root, "game", "Data");
        var overlayData = Path.Combine(_root, "overlay", "Data");
        var modData = Path.Combine(_root, "mod-a", "Data");
        WritePlugin(gameData, "Skyrim.esm");
        WritePlugin(overlayData, "Overlay.esp");
        WritePlugin(modData, "Alpha.esp");

        var profile = BuildProfile((Path.Combine(_root, "mod-a"), "mod-a"));
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "overlay",
            Kind = ProfileFolderKind.Overlay,
            Path = Path.Combine(_root, "overlay"),
            IsLocked = true
        });

        var records = _service.ScanEffectivePlugins(profile, new GamePluginList());

        // Only installed mod folders are scanned; the vanilla game and the overlay never contribute.
        var record = Assert.Single(records);
        Assert.Equal("Alpha.esp", record.FileName);
        Assert.True(record.Enabled);
    }

    [Fact]
    public void ScanEffectivePlugins_ShadowsLowerBranchAndMapsModIds()
    {
        var branch0 = Path.Combine(_root, "mod-a");
        var branch1 = Path.Combine(_root, "mod-b");
        WritePlugin(Path.Combine(branch0, "Data"), "Alpha.esp");
        WritePlugin(Path.Combine(branch1, "Data"), "Alpha.esp"); // shadowed by branch 0
        WritePlugin(Path.Combine(branch1, "Data", "Plugins"), "Nested.esp");
        File.WriteAllText(Path.Combine(branch1, "Data", "notes.txt"), "not a plugin");

        var profile = BuildProfile((branch0, "mod-a"), (branch1, "mod-b"));

        var records = _service.ScanEffectivePlugins(profile, new GamePluginList());

        // Alpha.esp (first mod shadows the second) + Nested.esp = 2 effective plugins.
        Assert.Equal(2, records.Count);
        var alpha = Assert.Single(records, record => record.FileName == "Alpha.esp");
        Assert.Equal(branch0, alpha.WinningBranch);
        Assert.Equal("mod-a", alpha.SourceFolderId);
        Assert.Equal("Alpha.esp", alpha.RelativePath);
        Assert.Equal(0, alpha.BranchIndex);
        Assert.True(alpha.Enabled);

        var nested = Assert.Single(records, record => record.FileName == "Nested.esp");
        Assert.Equal("mod-b", nested.SourceFolderId);
        Assert.Equal(Path.Combine("Plugins", "Nested.esp"), nested.RelativePath);
    }

    [Fact]
    public void ScanEffectivePlugins_IncludesDisabledModsAndPrefersEnabledCopy()
    {
        var enabledBranch = Path.Combine(_root, "mod-a");
        var disabledBranch = Path.Combine(_root, "mod-b");
        WritePlugin(Path.Combine(enabledBranch, "Data"), "Alpha.esp");
        WritePlugin(Path.Combine(disabledBranch, "Data"), "Alpha.esp");
        WritePlugin(Path.Combine(disabledBranch, "Data"), "Beta.esp");

        var profile = BuildProfile((enabledBranch, "mod-a"), (disabledBranch, "mod-b"));
        profile.LoadOrder.Single(folder => folder.Id == "folder-mod-b").IsEnabled = false;

        var records = _service.ScanEffectivePlugins(profile, new GamePluginList());

        // Both mods appear in the list; the enabled copy of Alpha wins over the disabled one.
        var alpha = Assert.Single(records, record => record.FileName == "Alpha.esp");
        Assert.Equal(enabledBranch, alpha.WinningBranch);
        Assert.True(alpha.Enabled);

        var beta = Assert.Single(records, record => record.FileName == "Beta.esp");
        Assert.Equal(disabledBranch, beta.WinningBranch);
        Assert.False(beta.Enabled);
    }

    [Fact]
    public void ScanEffectivePlugins_ReadsMastersFromTes4Header()
    {
        var branch = Path.Combine(_root, "mod-a");
        Directory.CreateDirectory(Path.Combine(branch, "Data"));
        File.WriteAllBytes(Path.Combine(branch, "Data", "Alpha.esp"), MakeTes4("Skyrim.esm", "Update.esm"));

        var profile = BuildProfile((branch, "mod-a"));
        var record = Assert.Single(_service.ScanEffectivePlugins(profile, new GamePluginList()));
        Assert.Equal(new[] { "Skyrim.esm", "Update.esm" }, record.Masters);
    }

    [Fact]
    public void ScanEffectivePlugins_MissingDataFolder_ReturnsEmpty()
    {
        var emptyBranch = Path.Combine(_root, "empty");
        var profile = BuildProfile((emptyBranch, "mod-a"));
        Assert.Empty(_service.ScanEffectivePlugins(profile, new GamePluginList()));
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
            _service.ResolveOrder(plugins, new GamePluginList()).Select(record => record.FileName).ToArray());
    }

    [Fact]
    public void ResolveOrder_PutsMastersBeforeDependents()
    {
        // Base order would put Aaa.esp first, but it depends on Zzz.esp.
        var plugins = new[]
        {
            Record("Aaa.esp", 0, null, true, "Zzz.esp", "Other.esm"),
            Record("Zzz.esp"),
            Record("Other.esm")
        };

        Assert.Equal(
            new[] { "Other.esm", "Zzz.esp", "Aaa.esp" },
            _service.ResolveOrder(plugins, new GamePluginList()).Select(record => record.FileName).ToArray());
    }

    [Fact]
    public void ResolveOrder_IgnoresMissingMastersAndSelfMasters()
    {
        var plugins = new[]
        {
            Record("Aaa.esp", 0, null, true, "NotPresent.esp", "Aaa.esp")
        };

        Assert.Equal(
            new[] { "Aaa.esp" },
            _service.ResolveOrder(plugins, new GamePluginList()).Select(record => record.FileName).ToArray());
    }

    [Fact]
    public void ResolveOrder_CycleAppendsRemainderInBaseOrder()
    {
        var plugins = new[]
        {
            Record("One.esp", 0, null, true, "Two.esp"),
            Record("Two.esp", 0, null, true, "One.esp")
        };

        Assert.Equal(
            new[] { "One.esp", "Two.esp" },
            _service.ResolveOrder(plugins, new GamePluginList()).Select(record => record.FileName).ToArray());
    }

    [Fact]
    public void ResolveListDestination_MatchingViewMount_WritesToBranch0()
    {
        var mount = Path.Combine(_root, "appdata", "game");
        var branch = Path.Combine(_root, "profileA");
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = mount,
            Branches = { branch }
        });

        var (destination, error) = _service.ResolveListDestination(target, new VariableScope(), Path.Combine(mount, "lists", "plugins.txt"));

        Assert.Null(error);
        Assert.Equal(Path.Combine(branch, "lists", "plugins.txt"), destination);
    }

    [Fact]
    public void ResolveListDestination_DeepMountWins()
    {
        var shallowMount = Path.Combine(_root, "appdata");
        var deepMount = Path.Combine(_root, "appdata", "game");
        var shallowBranch = Path.Combine(_root, "shallow");
        var deepBranch = Path.Combine(_root, "deep");
        // The resolver orders the merged views deepest mount first.
        var target = GameTarget(_root,
            new MergedView { Name = "Deep", MountPath = deepMount, Branches = { deepBranch } },
            new MergedView { Name = "Shallow", MountPath = shallowMount, Branches = { shallowBranch } });

        var (destination, error) = _service.ResolveListDestination(target, new VariableScope(), Path.Combine(deepMount, "plugins.txt"));

        Assert.Null(error);
        Assert.Equal(Path.Combine(deepBranch, "plugins.txt"), destination);
    }

    [Fact]
    public void ResolveListDestination_SegmentBoundary_IsNotAMatch()
    {
        var target = GameTarget(_root, new MergedView
        {
            Name = "Data",
            MountPath = Path.Combine(_root, "ab"),
            Branches = { Path.Combine(_root, "profile") }
        });

        // The mount is a string prefix but not a path prefix: root\abc is a different folder.
        var (destination, error) = _service.ResolveListDestination(target, new VariableScope(), Path.Combine(_root, "abc", "plugins.txt"));

        Assert.Null(destination);
        Assert.Equal("No view's mount path matches the plugin list path.", error);
    }

    [Fact]
    public void ResolveListDestination_UnresolvedVariable_ReturnsError()
    {
        var target = GameTarget(_root, new MergedView
        {
            Name = "Data",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile") }
        });

        var (destination, error) = _service.ResolveListDestination(target, new VariableScope(), "${NOTTHERE}\\plugins.txt");

        Assert.Null(destination);
        Assert.Equal("The plugin list path contains an unknown variable.", error);
    }

    [Fact]
    public void ResolveListDestination_NotRooted_ReturnsError()
    {
        var target = GameTarget(_root, new MergedView
        {
            Name = "Data",
            MountPath = _root,
            Branches = { Path.Combine(_root, "profile") }
        });

        var (destination, error) = _service.ResolveListDestination(target, new VariableScope(), "plugins.txt");

        Assert.Null(destination);
        Assert.Equal("The plugin list path must be a full path to the plugin list file.", error);
    }

    [Fact]
    public async Task WriteAsync_PrefixesEnabledPluginsWithAsterisk()
    {
        var mount = Path.Combine(_root, "appdata", "game");
        var branch = Path.Combine(_root, "profileA");
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = mount,
            Branches = { branch }
        });
        var cfg = new GamePluginList { ListPath = Path.Combine(mount, "plugins.txt") };
        var game = Game(cfg);

        var destination = await _service.WriteAsync(target, game, cfg, new[]
        {
            Record("Alpha.esp"), // enabled mod
            Record("Beta.esp", 0, null, false) // disabled-only mod
        });

        Assert.Equal(Path.Combine(branch, "plugins.txt"), destination);
        Assert.Equal("*Alpha.esp\nBeta.esp\n", File.ReadAllText(destination!));
        Assert.False(File.Exists(destination! + ".bak"));
    }

    [Fact]
    public async Task WriteAsync_EmptyListWritesEmptyFile()
    {
        var mount = Path.Combine(_root, "appdata", "game");
        var branch = Path.Combine(_root, "profileA");
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = mount,
            Branches = { branch }
        });
        var cfg = new GamePluginList { ListPath = Path.Combine(mount, "plugins.txt") };

        var destination = await _service.WriteAsync(target, Game(cfg), cfg, Array.Empty<PluginRecord>());

        Assert.Equal(string.Empty, File.ReadAllText(destination!));
    }

    [Fact]
    public async Task WriteAsync_SecondWriteKeepsPreviousContentAsBackup()
    {
        var mount = Path.Combine(_root, "appdata", "game");
        var branch = Path.Combine(_root, "profileA");
        var target = GameTarget(_root, new MergedView
        {
            Name = "LocalAppData",
            MountPath = mount,
            Branches = { branch }
        });
        var cfg = new GamePluginList { ListPath = Path.Combine(mount, "plugins.txt") };
        var game = Game(cfg);

        var destination = await _service.WriteAsync(target, game, cfg, new[] { Record("Alpha.esp") });
        var second = await _service.WriteAsync(target, game, cfg, new[] { Record("Alpha.esp"), Record("Beta.esp", 0, null, false) });

        Assert.Equal(destination, second);
        Assert.Equal("*Alpha.esp\nBeta.esp\n", File.ReadAllText(destination!));
        Assert.Equal("*Alpha.esp\n", File.ReadAllText(destination! + ".bak"));
    }

    [Fact]
    public async Task WriteAsync_NoMatchingView_ReturnsNull()
    {
        var target = GameTarget(_root, new MergedView
        {
            Name = "Data",
            MountPath = Path.Combine(_root, "somewhere-else"),
            Branches = { Path.Combine(_root, "read-only") }
        });
        var cfg = new GamePluginList { ListPath = Path.Combine(_root, "plugins.txt") };

        Assert.Null(await _service.WriteAsync(target, Game(cfg), cfg, new[] { Record("Alpha.esp") }));
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

        var mount = Path.Combine(_root, "appdata", "game");
        var profile = BuildProfile((branch, "mod-a"));
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
                MountPath = mount,
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
                PluginList = new GamePluginList { ListPath = Path.Combine(mount, "plugins.txt") }
            }
        };

        await _service.EnsureUpToDateAsync(profile, target, game);

        // The mod folder is enabled, so every listed plugin carries the asterisk.
        Assert.Equal(
            "*Master.esm\n*Dependant.esp\n",
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
        var destination = Path.Combine(_root, "localappdata", "plugins.txt");

        // No plugin list descriptor on the definition.
        var game = new GameEntry { Id = "game", InstallPath = installPath, Definition = new GameDefinition { DefinitionId = "x", Name = "X" } };
        await _service.EnsureUpToDateAsync(profile, target, game);
        Assert.False(File.Exists(destination));

        // Descriptor present, but the target is a tool.
        var tool = target with { Kind = LaunchTargetKind.Tool };
        game.Definition!.PluginList = new GamePluginList { ListPath = destination };
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

    private static GameEntry Game(GamePluginList pluginList) => new()
    {
        Id = "game",
        Name = "Game",
        InstallPath = string.Empty,
        Definition = new GameDefinition
        {
            DefinitionId = "test-game",
            Name = "Test Game",
            PluginList = pluginList
        }
    };

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

    private static PluginRecord Record(string fileName, int branch = 0, string? modId = null, bool enabled = true, params string[] masters) => new()
    {
        FileName = fileName,
        RelativePath = fileName,
        WinningBranch = fileName,
        BranchIndex = branch,
        SourceFolderId = modId,
        Enabled = enabled,
        Masters = masters.ToList()
    };
}
