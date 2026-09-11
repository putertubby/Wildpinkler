using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Commands;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DependencyCommandsTests : IDisposable
{
    private readonly string _root;
    private readonly ModStore _mods;
    private readonly ProfileStore _profiles;

    public DependencyCommandsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wp-dependency-commands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _mods = new ModStore(_root);
        _profiles = new ProfileStore(_root);
    }

    public void Dispose()
    {
        _mods.Dispose();
        _profiles.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task GetDependencies_ReturnsEdgesForADisabledMod()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies =
                {
                    new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") }
                }
            },
            new ModEntry { Id = "b", Name = "Mod B" }
        });

        var handler = new GetModDependenciesHandler(_mods, _profiles);
        var result = await handler.HandleAsync(new GetModDependenciesCommand("a"), TestContext.Current.CancellationToken);

        var dto = Assert.Single(result);
        Assert.Equal("b", dto.TargetModId);
        Assert.True(dto.TargetKnownLocally);
        Assert.Null(dto.TargetEnabledInProfile);
    }

    [Fact]
    public async Task AddDependency_AppendsManualEdge()
    {
        await _mods.SaveAsync(new[] { new ModEntry { Id = "a", Name = "Mod A" } });

        var handler = new AddModDependencyHandler(_mods);
        var dto = await handler.HandleAsync(
            new AddModDependencyCommand("a", ModDependencyKind.Conflicts, "c", "Mod C", VersionConstraint: null),
            TestContext.Current.CancellationToken);

        Assert.Equal("c", dto.TargetModId);
        Assert.Equal("manual", dto.Origin);

        var saved = (await _mods.LoadAsync()).Single(mod => mod.Id == "a");
        Assert.Single(saved.Dependencies);
    }

    [Fact]
    public async Task AddDependency_SelfReference_Throws()
    {
        await _mods.SaveAsync(new[] { new ModEntry { Id = "a", Name = "Mod A" } });

        var handler = new AddModDependencyHandler(_mods);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AddModDependencyCommand("a", ModDependencyKind.Requires, "a", "Mod A", VersionConstraint: null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveDependency_DeletesById()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies = { new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") } }
            }
        });

        var handler = new RemoveModDependencyHandler(_mods);
        var removed = await handler.HandleAsync(new RemoveModDependencyCommand("a", "d1"), TestContext.Current.CancellationToken);

        Assert.True(removed);
        var saved = (await _mods.LoadAsync()).Single(mod => mod.Id == "a");
        Assert.Empty(saved.Dependencies);
    }

    [Fact]
    public async Task UpdateDependency_ChangesTarget()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies = { new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") } }
            }
        });

        var handler = new UpdateModDependencyHandler(_mods);
        var dto = await handler.HandleAsync(
            new UpdateModDependencyCommand("a", "d1", ModDependencyKind.LoadAfter, "c", "Mod C", VersionConstraint: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModDependencyKind.LoadAfter, dto.Kind);
        Assert.Equal("c", dto.TargetModId);
    }

    [Fact]
    public async Task RemoveDependency_UnknownId_Throws()
    {
        await _mods.SaveAsync(new[] { new ModEntry { Id = "a", Name = "Mod A" } });

        var handler = new RemoveModDependencyHandler(_mods);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new RemoveModDependencyCommand("a", "missing"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddDependency_DuplicateEdge_Throws()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies = { new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") } }
            },
            new ModEntry { Id = "b", Name = "Mod B" }
        });

        var handler = new AddModDependencyHandler(_mods);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AddModDependencyCommand("a", ModDependencyKind.Requires, "b", "Mod B", VersionConstraint: null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddDependency_ContradictsExistingRequires_Throws()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies = { new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") } }
            },
            new ModEntry { Id = "b", Name = "Mod B" }
        });

        var handler = new AddModDependencyHandler(_mods);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AddModDependencyCommand("a", ModDependencyKind.Conflicts, "b", "Mod B", VersionConstraint: null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddDependency_ContradictsExistingLoadOrder_Throws()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies = { new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.LoadAfter, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") } }
            },
            new ModEntry { Id = "b", Name = "Mod B" }
        });

        var handler = new AddModDependencyHandler(_mods);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new AddModDependencyCommand("a", ModDependencyKind.LoadBefore, "b", "Mod B", VersionConstraint: null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateDependency_ToDuplicateOfAnotherEdge_Throws()
    {
        await _mods.SaveAsync(new[]
        {
            new ModEntry
            {
                Id = "a",
                Name = "Mod A",
                Dependencies =
                {
                    new ModDependency { Id = "d1", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("b", null, "Mod B") },
                    new ModDependency { Id = "d2", SourceModId = "a", Kind = ModDependencyKind.Requires, Origin = "manual", Target = new ModDependencyTarget("c", null, "Mod C") }
                }
            },
            new ModEntry { Id = "b", Name = "Mod B" },
            new ModEntry { Id = "c", Name = "Mod C" }
        });

        var handler = new UpdateModDependencyHandler(_mods);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new UpdateModDependencyCommand("a", "d2", ModDependencyKind.Requires, "b", "Mod B", VersionConstraint: null),
            TestContext.Current.CancellationToken));
    }
}
