using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListDependencyMapperTests
{
    private readonly ModListDependencyMapper _mapper = new();

    [Fact]
    public void Apply_MapsPortableEntriesOntoLocalMods()
    {
        var source = Mod("local-source");
        var target = Mod("local-target");
        var manifest = Manifest(("entry-source", "entry-target", ModDependencyKind.Requires));
        var map = Map(("entry-source", "local-source"), ("entry-target", "local-target"));

        var result = _mapper.Apply(manifest, map, new[] { source, target });

        Assert.Equal(1, result.AddedDependencies);
        Assert.Equal(new[] { "local-source" }, result.ChangedModIds);
        Assert.Equal("local-target", Assert.Single(source.Dependencies).Target?.ModId);
        Assert.Equal("mod-list", source.Dependencies[0].Origin);
    }

    [Fact]
    public void Apply_UnmappedEntry_IsSkippedRatherThanGuessed()
    {
        var source = Mod("local-source");
        var manifest = Manifest(("entry-source", "entry-missing", ModDependencyKind.Requires));
        var map = Map(("entry-source", "local-source"));

        var result = _mapper.Apply(manifest, map, new[] { source });

        Assert.Equal(0, result.AddedDependencies);
        Assert.Equal(1, result.SkippedDependencies);
        Assert.Empty(source.Dependencies);
    }

    [Fact]
    public void Apply_ExistingEdge_IsNotDuplicated()
    {
        var source = Mod("local-source");
        var target = Mod("local-target");
        source.Dependencies.Add(new ModDependency
        {
            Id = "existing",
            Kind = ModDependencyKind.Requires,
            Target = new ModDependencyTarget("local-target", null, "Target")
        });
        var manifest = Manifest(("entry-source", "entry-target", ModDependencyKind.Requires));
        var map = Map(("entry-source", "local-source"), ("entry-target", "local-target"));

        var result = _mapper.Apply(manifest, map, new[] { source, target });

        Assert.Equal(0, result.AddedDependencies);
        Assert.Single(source.Dependencies);
    }

    [Fact]
    public void Apply_EntriesMappedToTheSameMod_DoNotCreateSelfReference()
    {
        var mod = Mod("only");
        var manifest = Manifest(("entry-a", "entry-b", ModDependencyKind.LoadAfter));
        var map = Map(("entry-a", "only"), ("entry-b", "only"));

        var result = _mapper.Apply(manifest, map, new[] { mod });

        Assert.Equal(0, result.AddedDependencies);
        Assert.Empty(mod.Dependencies);
    }

    private static ModListManifest Manifest(params (string Source, string Target, ModDependencyKind Kind)[] edges)
    {
        var manifest = new ModListManifest();
        foreach (var (source, target, kind) in edges)
            manifest.Dependencies.Add(new ModListDependency { SourceEntryId = source, TargetEntryId = target, Kind = kind });
        return manifest;
    }

    private static Dictionary<string, string> Map(params (string EntryId, string ModId)[] pairs) =>
        pairs.ToDictionary(pair => pair.EntryId, pair => pair.ModId);

    private static ModEntry Mod(string id) => new() { Id = id, Name = id, Dependencies = new List<ModDependency>() };
}
