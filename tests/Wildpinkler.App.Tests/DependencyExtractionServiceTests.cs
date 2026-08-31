using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public class DependencyExtractionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-depextract-" + Guid.NewGuid().ToString("N"));
    private readonly DependencyExtractionService _service = new();

    public DependencyExtractionServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Extract_FomodFileDependencyMatchesAnotherModsFile_ReturnsRequires()
    {
        var requiredFolder = CreateInstalledFolder("required", "RequiredMod.esp");
        var knownMods = new List<ModEntry> { new() { Id = "required", Name = "Required Mod" } };
        var installations = new List<ModInstallation> { new() { ModId = "required", FolderPath = requiredFolder } };
        var module = ModuleWithFileDependency("RequiredMod.esp", FomodFileDependencyState.Active);

        var dependencies = _service.Extract(new ModEntry { Id = "source" }, module, CreateInstalledFolder("source"), knownMods, installations);

        var dependency = Assert.Single(dependencies);
        Assert.Equal(ModDependencyKind.Requires, dependency.Kind);
        Assert.Equal("fomod", dependency.Origin);
        Assert.Equal("required", dependency.Target!.ModId);
    }

    [Fact]
    public void Extract_FomodFileDependencyMissingState_ReturnsConflicts()
    {
        var conflictFolder = CreateInstalledFolder("conflict", "OldMod.esp");
        var knownMods = new List<ModEntry> { new() { Id = "conflict", Name = "Old Mod" } };
        var installations = new List<ModInstallation> { new() { ModId = "conflict", FolderPath = conflictFolder } };
        var module = ModuleWithFileDependency("OldMod.esp", FomodFileDependencyState.Missing);

        var dependency = Assert.Single(_service.Extract(new ModEntry { Id = "source" }, module, CreateInstalledFolder("source"), knownMods, installations));

        Assert.Equal(ModDependencyKind.Conflicts, dependency.Kind);
    }

    [Fact]
    public void Extract_FomodFileDependencyWithNoMatch_ReturnsNothing()
    {
        var module = ModuleWithFileDependency("Unowned.esp", FomodFileDependencyState.Active);

        var dependencies = _service.Extract(new ModEntry { Id = "source" }, module, CreateInstalledFolder("source"), Array.Empty<ModEntry>(), Array.Empty<ModInstallation>());

        Assert.Empty(dependencies);
    }

    [Fact]
    public void Extract_PluginMasterMatchesAnotherModsPlugin_ReturnsLoadAfter()
    {
        var masterFolder = CreateInstalledFolder("master");
        File.WriteAllBytes(Path.Combine(masterFolder, "MasterMod.esm"), BuildPluginWithMasters());
        var sourceFolder = CreateInstalledFolder("source");
        File.WriteAllBytes(Path.Combine(sourceFolder, "Patch.esp"), BuildPluginWithMasters("MasterMod.esm"));

        var knownMods = new List<ModEntry> { new() { Id = "master", Name = "Master Mod" } };
        var installations = new List<ModInstallation> { new() { ModId = "master", FolderPath = masterFolder } };

        var dependency = Assert.Single(_service.Extract(new ModEntry { Id = "source" }, fomodModule: null, sourceFolder, knownMods, installations));

        Assert.Equal(ModDependencyKind.LoadAfter, dependency.Kind);
        Assert.Equal("plugin-master", dependency.Origin);
        Assert.Equal("master", dependency.Target!.ModId);
    }

    [Fact]
    public void Extract_PluginMasterNotOwnedByAnyKnownMod_ReturnsNothing()
    {
        // Skyrim.esm is a base-game master, not tracked as a mod - must not be flagged.
        var sourceFolder = CreateInstalledFolder("source");
        File.WriteAllBytes(Path.Combine(sourceFolder, "Patch.esp"), BuildPluginWithMasters("Skyrim.esm"));

        var dependencies = _service.Extract(new ModEntry { Id = "source" }, fomodModule: null, sourceFolder, Array.Empty<ModEntry>(), Array.Empty<ModInstallation>());

        Assert.Empty(dependencies);
    }

    private string CreateInstalledFolder(string name, params string[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files)
            File.WriteAllText(Path.Combine(folder, file), string.Empty);
        return folder;
    }

    private static FomodModule ModuleWithFileDependency(string file, FomodFileDependencyState state) => new()
    {
        ModuleDependency = new FomodCompositeDependency
        {
            Children = { new FomodFileDependency { File = file, State = state } }
        }
    };

    private static byte[] BuildPluginWithMasters(params string[] masters)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var master in masters)
            {
                var bytes = Encoding.ASCII.GetBytes(master + "\0");
                writer.Write(Encoding.ASCII.GetBytes("MAST"));
                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
            }
        }

        var body = data.ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("TES4"));
            writer.Write((uint)body.Length);
            writer.Write(new byte[16]);
            writer.Write(body);
        }

        return stream.ToArray();
    }
}
