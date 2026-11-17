using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies archive-derived requirement extraction (the remote-first policy's fallback path): FOMOD
/// file/visibility/conditional dependencies and plugin-master reads, plus dedup behaviour. Uses a
/// hand-built fake inspector so no real archives are required.
/// </summary>
public class ArchiveDependencyExtractorTests
{
    private readonly CancellationToken CancellationToken = TestContext.Current.CancellationToken;

    private sealed class FakeInspector : IArchiveInspector
    {
        public List<string> PluginEntries { get; } = new();
        public List<string> EntryPaths { get; } = new();
        public Dictionary<string, byte[]> EntryBytes { get; } = new();

        public FomodState DetectFomod(string path, IProgress<AnalysisProgressReport>? p = null, CancellationToken ct = default) => FomodState.No;
        public IReadOnlyDictionary<string, string> ReadFomodFiles(string path, IProgress<AnalysisProgressReport>? p = null, CancellationToken ct = default) => new Dictionary<string, string>();
        public ArchiveLayout InspectLayout(string path, IProgress<AnalysisProgressReport>? p = null, CancellationToken ct = default) => new(new List<string>(), null, true);
        public byte[]? ReadEntryBytes(string path, string entryName, CancellationToken ct = default) => EntryBytes.GetValueOrDefault(entryName);
        public IReadOnlyList<string> ListPluginEntries(string path, IProgress<AnalysisProgressReport>? p = null, CancellationToken ct = default) => PluginEntries;
        public IReadOnlyList<string> ListEntryPaths(string path, IProgress<AnalysisProgressReport>? p = null, CancellationToken ct = default) => EntryPaths;

        public byte[]?[]? ReadEntryBytesBulk(string path, string[] entryNames, CancellationToken ct = default) =>
            entryNames.Select(name => EntryBytes.GetValueOrDefault(name)).ToArray();
    }

    private static ModEntry Mod(string path) => new() { Id = "test", Name = "Test Mod", ArchivePath = path };

    /// <summary>Builds a minimal TES4 plugin header declaring the given master filenames.</summary>
    private static byte[] BuildPluginBytes(params string[] masters)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("TES4"));

        // Measure the records first so we can write the header's data size.
        using var recordStream = new MemoryStream();
        using var recordWriter = new BinaryWriter(recordStream, Encoding.ASCII, leaveOpen: true);
        foreach (var master in masters)
        {
            var data = Encoding.ASCII.GetBytes(master + "\0");
            recordWriter.Write(Encoding.ASCII.GetBytes("MAST"));
            recordWriter.Write((ushort)data.Length);
            recordWriter.Write(data);
        }
        var recordBytes = recordStream.ToArray();

        writer.Write(recordBytes.Length);
        writer.Write(new byte[16]); // reserved header fields
        writer.Write(recordBytes);
        writer.Flush();
        return memory.ToArray();
    }

    [Fact]
    public async Task PlainArchive_NoPlugins_NoFomod_ReturnsEmpty()
    {
        var inspector = new FakeInspector();
        var extractor = new ArchiveDependencyExtractor(inspector);

        var result = await extractor.ExtractAsync(Mod("mod.7z"), CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task PlainArchive_PluginMasters_AreRequired()
    {
        var inspector = new FakeInspector();
        inspector.PluginEntries.Add("Data\\SkyrimMod.esp");
        inspector.EntryBytes["Data\\SkyrimMod.esp"] = BuildPluginBytes("Skyrim.esm", "Update.esm");
        var extractor = new ArchiveDependencyExtractor(inspector);

        var result = await extractor.ExtractAsync(Mod("mod.7z"), CancellationToken);

        var names = result.Select(r => r.TargetName).OrderBy(n => n).ToList();
        Assert.Equal(["Skyrim.esm", "Update.esm"], names);
        Assert.All(result, r => Assert.Equal(RemoteRequirementKind.Required, r.Kind));
    }

    [Fact]
    public async Task Fomod_FileDependency_MissingState_IsIncompatible()
    {
        var inspector = new FakeInspector();
        var extractor = new ArchiveDependencyExtractor(inspector);

        var module = new FomodModule
        {
            ModuleDependency = new FomodCompositeDependency { Children = {
                new FomodFileDependency { File = "Data\\Required.esm", State = FomodFileDependencyState.Active },
                new FomodFileDependency { File = "Data\\Conflicting.esp", State = FomodFileDependencyState.Missing }
            } }
        };

        var result = await extractor.ExtractAsync(Mod("mod.7z"), module, CancellationToken);

        Assert.Contains(result, r => r.TargetName == "Data\\Required.esm" && r.Kind == RemoteRequirementKind.Required);
        Assert.Contains(result, r => r.TargetName == "Data\\Conflicting.esp" && r.Kind == RemoteRequirementKind.Incompatible);
    }

    [Fact]
    public async Task Fomod_StepVisibilityAndPluginPattern_Dependencies_AreCollected()
    {
        var inspector = new FakeInspector();
        var extractor = new ArchiveDependencyExtractor(inspector);

        var module = new FomodModule
        {
            InstallSteps =
            {
                new FomodInstallStep
                {
                    Name = "Step 1",
                    VisibilityDependency = new FomodFileDependency { File = "Data\\StepGate.esm", State = FomodFileDependencyState.Active },
                    Groups =
                    {
                        new FomodGroup
                        {
                            Name = "Group 1",
                            Plugins =
                            {
                                new FomodPlugin
                                {
                                    Name = "Option",
                                    DependencyPatterns =
                                    {
                                        new FomodTypePattern { Dependency = new FomodFileDependency { File = "Data\\OptionGate.esp", State = FomodFileDependencyState.Active } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = await extractor.ExtractAsync(Mod("mod.7z"), module, CancellationToken);

        Assert.Contains(result, r => r.TargetName == "Data\\StepGate.esm");
        Assert.Contains(result, r => r.TargetName == "Data\\OptionGate.esp");
    }

    [Fact]
    public async Task Fomod_ConditionalFileInstall_Dependencies_AreCollected()
    {
        var inspector = new FakeInspector();
        var extractor = new ArchiveDependencyExtractor(inspector);

        var module = new FomodModule
        {
            ConditionalFileInstalls =
            {
                new FomodConditionalPattern { Dependency = new FomodFileDependency { File = "Data\\Conditional.esm", State = FomodFileDependencyState.Active } }
            }
        };

        var result = await extractor.ExtractAsync(Mod("mod.7z"), module, CancellationToken);

        Assert.Contains(result, r => r.TargetName == "Data\\Conditional.esm" && r.Kind == RemoteRequirementKind.Required);
    }

    [Fact]
    public async Task Duplicate_WithinCategory_Names_AreDeduped()
    {
        // Within a single category duplicates collapse to one row: two identical FOMOD file
        // dependencies and two identical masters declared by one plugin both reduce to one.
        var inspector = new FakeInspector();
        inspector.PluginEntries.Add("Data\\A.esp");
        inspector.EntryBytes["Data\\A.esp"] = BuildPluginBytes("Update.esm", "Update.esm");
        var extractor = new ArchiveDependencyExtractor(inspector);

        var module = new FomodModule
        {
            ModuleDependency = new FomodCompositeDependency { Children = {
                new FomodFileDependency { File = "Skyrim.esm", State = FomodFileDependencyState.Active },
                new FomodFileDependency { File = "Skyrim.esm", State = FomodFileDependencyState.Active }
            } }
        };

        var result = await extractor.ExtractAsync(Mod("mod.7z"), module, CancellationToken);

        Assert.Single(result, r => r.TargetName == "Skyrim.esm");
        Assert.Single(result, r => r.TargetName == "Update.esm");
    }
}
