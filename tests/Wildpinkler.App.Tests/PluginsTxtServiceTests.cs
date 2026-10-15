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
        profile.LoadOrder.Single(folder => folder.Id == "mod-b").IsEnabled = false;

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
    public void ScanEffectivePlugins_IncludesToolOutputAndUnmanagedFolders()
    {
        var modData = Path.Combine(_root, "mod-a", "Data");
        var toolData = Path.Combine(_root, "tool-out", "Data");
        var unmanagedData = Path.Combine(_root, "unmanaged", "Data");
        WritePlugin(modData, "Alpha.esp");
        WritePlugin(toolData, "ToolPlugin.esp");
        WritePlugin(unmanagedData, "Handmade.esp");

        var profile = BuildProfile((Path.Combine(_root, "mod-a"), "mod-a"));
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "tool-folder",
            Name = "Tool output",
            Kind = ProfileFolderKind.ToolOutput,
            Path = Path.Combine(_root, "tool-out")
        });
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "unmanaged-folder",
            Name = "Handmade",
            Kind = ProfileFolderKind.Unmanaged,
            Path = Path.Combine(_root, "unmanaged")
        });

        var records = _service.ScanEffectivePlugins(profile, new GamePluginList());

        // All three branches contribute; SourceFolderId is the folder's Id (not a mod id).
        Assert.Equal(3, records.Count);
        Assert.Equal("tool-folder", Assert.Single(records, record => record.FileName == "ToolPlugin.esp").SourceFolderId);
        Assert.Equal("unmanaged-folder", Assert.Single(records, record => record.FileName == "Handmade.esp").SourceFolderId);
        Assert.Equal("mod-a", Assert.Single(records, record => record.FileName == "Alpha.esp").SourceFolderId);
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
        var tool = GameTarget(installPath, LaunchTargetKind.Tool, new[] { writable });
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

    [Fact]
    public void SeedFromEffectiveOrder_MapsEnabledStateAndModIds()
    {
        var branch0 = Path.Combine(_root, "mod-a");
        var branch1 = Path.Combine(_root, "mod-b");
        WritePlugin(Path.Combine(branch0, "Data"), "Alpha.esp");
        WritePlugin(Path.Combine(branch1, "Data"), "Beta.esp");

        var profile = BuildProfile((branch0, "mod-a"), (branch1, "mod-b"));
        profile.LoadOrder.Single(folder => folder.Id == "mod-b").IsEnabled = false;

        var seeded = _service.SeedFromEffectiveOrder(profile, new GamePluginList());

        Assert.Equal(2, seeded.Count);
        var alpha = Assert.Single(seeded, entry => entry.FileName == "Alpha.esp");
        Assert.True(alpha.IsEnabled);
        Assert.Equal("mod-a", alpha.SourceFolderId);

        var beta = Assert.Single(seeded, entry => entry.FileName == "Beta.esp");
        Assert.False(beta.IsEnabled);
        Assert.Equal("mod-b", beta.SourceFolderId);
    }

    [Fact]
    public void ReadBack_PreservesOrderAndEnabledState()
    {
        var path = Path.Combine(_root, "plugins.txt");
        File.WriteAllText(path, "*Alpha.esp\nBeta.esp\n*Gamma.esp\n\n", new System.Text.UTF8Encoding(false));

        var read = _service.ReadBack(path);

        Assert.Equal(new[] { "Alpha.esp", "Beta.esp", "Gamma.esp" }, read.Select(entry => entry.FileName));
        Assert.Equal(new[] { true, false, true }, read.Select(entry => entry.IsEnabled));
    }

    [Fact]
    public void ReadBack_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(_service.ReadBack(Path.Combine(_root, "missing", "plugins.txt")));
    }

    [Fact]
    public async Task WriteAuthoritativeAsync_WritesAsterisksAndOrder()
    {
        var installPath = Path.Combine(_root, "game");
        var mount = Path.Combine(_root, "appdata", "game");
        var writable = new MergedView
        {
            Name = "LocalAppData",
            MountPath = mount,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        };
        var target = GameTarget(installPath, writable);
        var destination = Path.Combine(_root, "profile", "localappdata", "plugins.txt");
        var game = new GameEntry
        {
            Id = "game",
            InstallPath = installPath,
            Definition = new GameDefinition
            {
                DefinitionId = "x",
                Name = "X",
                PluginList = new GamePluginList { ListPath = Path.Combine(mount, "plugins.txt") }
            }
        };

        // Order is preserved exactly as given; the leading asterisk marks the enabled plugin.
        var entries = new List<PluginEntry>
        {
            new() { FileName = "Alpha.esp", IsEnabled = true },
            new() { FileName = "Beta.esp", IsEnabled = false }
        };

        var written = await _service.WriteAuthoritativeAsync(target, game, game.Definition!.PluginList!, entries);
        Assert.Equal(destination, written);

        var lines = File.ReadAllLines(destination).Where(line => line.Length > 0).ToList();
        Assert.Equal(new[] { "*Alpha.esp", "Beta.esp" }, lines);
    }

    [Fact]
    public void ApplyBranchState_DisabledBranch_DisablesPluginAndRemembersState()
    {
        var mod = new ProfileFolder
        {
            Id = "folder-a",
            Name = "Mod A",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod-a",
            IsEnabled = false
        };

        var entry = new PluginEntry
        {
            FileName = "Alpha.esp",
            IsEnabled = true,
            SourceFolderId = "folder-a"
        };

        _service.ApplyBranchState(new[] { entry }, new[] { mod });

        Assert.False(entry.IsEnabled);
        Assert.False(entry.BranchEnabled);
        Assert.Equal(true, entry.RememberedEnabled);
    }

    [Fact]
    public void ApplyBranchState_LegacyModId_DisablesPluginAndSelfHealsFolderId()
    {
        // A profile seeded by an earlier build stores the mod id, not the folder id.
        var mod = new ProfileFolder
        {
            Id = "folder-a",
            Name = "Mod A",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod-a",
            IsEnabled = false
        };

        var entry = new PluginEntry
        {
            FileName = "Alpha.esp",
            IsEnabled = true,
            SourceFolderId = "mod-a"
        };

        _service.ApplyBranchState(new[] { entry }, new[] { mod });

        Assert.False(entry.IsEnabled);
        Assert.False(entry.BranchEnabled);
        Assert.Equal(true, entry.RememberedEnabled);
        Assert.Equal("folder-a", entry.SourceFolderId);
    }

    [Fact]
    public void ApplyBranchState_ReenabledBranch_RestoresRememberedStateAndUnlocks()
    {
        var mod = new ProfileFolder
        {
            Id = "folder-a",
            Name = "Mod A",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod-a",
            IsEnabled = true
        };

        var wasEnabled = new PluginEntry
        {
            FileName = "Alpha.esp",
            IsEnabled = false,
            SourceFolderId = "folder-a",
            RememberedEnabled = true
        };
        var wasDisabled = new PluginEntry
        {
            FileName = "Beta.esp",
            IsEnabled = false,
            SourceFolderId = "folder-a",
            RememberedEnabled = false
        };

        _service.ApplyBranchState(new[] { wasEnabled, wasDisabled }, new[] { mod });

        Assert.True(wasEnabled.IsEnabled);
        Assert.Null(wasEnabled.RememberedEnabled);
        Assert.False(wasDisabled.IsEnabled);
        Assert.Null(wasDisabled.RememberedEnabled);
        Assert.True(wasEnabled.BranchEnabled);
    }

    [Fact]
    public void ApplyBranchState_RerunDoesNotRecaptureFalse()
    {
        var mod = new ProfileFolder
        {
            Id = "folder-a",
            Name = "Mod A",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod-a",
            IsEnabled = false
        };

        var entry = new PluginEntry
        {
            FileName = "Alpha.esp",
            IsEnabled = true,
            SourceFolderId = "folder-a"
        };

        _service.ApplyBranchState(new[] { entry }, new[] { mod });
        _service.ApplyBranchState(new[] { entry }, new[] { mod });

        // The original enabled choice survives the second run.
        Assert.Equal(true, entry.RememberedEnabled);
    }

    [Fact]
    public void ApplyBranchState_MissingFolder_LeavesEntryUngated()
    {
        var entry = new PluginEntry
        {
            FileName = "Orphan.esp",
            IsEnabled = true,
            SourceFolderId = "gone"
        };

        _service.ApplyBranchState(new[] { entry }, Array.Empty<ProfileFolder>());

        Assert.True(entry.IsEnabled);
        Assert.True(entry.BranchEnabled);
        Assert.Null(entry.RememberedEnabled);
    }

    [Fact]
    public void Reconcile_KeptUserOrder_DropsRemoved_AddsNew()
    {
        var authoritative = new List<PluginEntry>
        {
            new() { FileName = "Alpha.esp", IsEnabled = true, SourceFolderId = "mod-a" },
            new() { FileName = "Old.esp", IsEnabled = true },
            new() { FileName = "Beta.esp", IsEnabled = false }
        };

        // Effective set: Alpha and Beta remain (Beta now enabled because its mod was re-enabled);
        // Old is gone; New appears.
        var effective = new List<PluginRecord>
        {
            new() { FileName = "Alpha.esp", BranchIndex = 0, SourceFolderId = "mod-a", Enabled = true },
            new() { FileName = "Beta.esp", BranchIndex = 1, SourceFolderId = "mod-b", Enabled = true },
            new() { FileName = "New.esp", BranchIndex = 2, SourceFolderId = "mod-c", Enabled = true }
        };

        var reconciled = _service.Reconcile(authoritative, effective);

        // Old is dropped; the user's order for Alpha (first) and Beta is kept; New is appended.
        Assert.Equal(new[] { "Alpha.esp", "Beta.esp", "New.esp" }, reconciled.Select(entry => entry.FileName));
        Assert.Equal(new[] { true, false, true }, reconciled.Select(entry => entry.IsEnabled));
    }

    [Fact]
    public void Reconcile_PreservesRememberedEnabledForKeptEntries()
    {
        // Alpha's branch was disabled, so its enabled state is remembered (RememberedEnabled = true)
        // and the entry is force-disabled. Reconcile must carry the remembered state through.
        var authoritative = new List<PluginEntry>
        {
            new() { FileName = "Alpha.esp", IsEnabled = false, SourceFolderId = "mod-a", RememberedEnabled = true }
        };

        var effective = new List<PluginRecord>
        {
            new() { FileName = "Alpha.esp", BranchIndex = 0, SourceFolderId = "mod-a", Enabled = true }
        };

        var reconciled = _service.Reconcile(authoritative, effective);

        var alpha = Assert.Single(reconciled, entry => entry.FileName == "Alpha.esp");
        Assert.False(alpha.IsEnabled);
        Assert.Equal(true, alpha.RememberedEnabled);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_UsesAuthoritativeListWhenPresent()
    {
        var installPath = Path.Combine(_root, "game");
        var mount = Path.Combine(_root, "appdata", "game");
        var writable = new MergedView
        {
            Name = "LocalAppData",
            MountPath = mount,
            Branches = { Path.Combine(_root, "profile", "localappdata") },
            IsWritable = true
        };
        var target = GameTarget(installPath, writable);

        // A mod that would produce "Derived.esp" if the authoritative list were ignored.
        var branch = Path.Combine(_root, "mod-a");
        WritePlugin(Path.Combine(branch, "Data"), "Derived.esp");
        var profile = BuildProfile((branch, "mod-a"));
        profile.PluginOrder.Add(new PluginEntry { FileName = "Authoritative.esp", IsEnabled = true });

        var game = new GameEntry
        {
            Id = "game",
            InstallPath = installPath,
            Definition = new GameDefinition
            {
                DefinitionId = "x",
                Name = "X",
                PluginList = new GamePluginList { ListPath = Path.Combine(mount, "plugins.txt") }
            }
        };

        await _service.EnsureUpToDateAsync(profile, target, game);

        // The authoritative entry wins over the derived mod plugin.
        var lines = File.ReadAllLines(Path.Combine(_root, "profile", "localappdata", "plugins.txt"))
            .Where(line => line.Length > 0).ToList();
        Assert.Single(lines);
        Assert.Equal("*Authoritative.esp", lines[0]);
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
                Id = modId,
                Kind = ProfileFolderKind.Mod,
                Path = path,
                ModId = modId
            });
        }

        return profile;
    }

    private static LaunchTarget GameTarget(string installPath, params MergedView[] views) =>
        GameTarget(installPath, LaunchTargetKind.Game, views);

    private static LaunchTarget GameTarget(string installPath, LaunchTargetKind kind, MergedView[] views) => new(
        "game",
        "Game",
        kind,
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
        string.Empty,
        null,
        string.Empty,
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
