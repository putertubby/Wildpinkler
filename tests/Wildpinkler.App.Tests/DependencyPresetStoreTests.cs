using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DependencyPresetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-presets-" + Guid.NewGuid().ToString("N"));
    private readonly DependencyPresetStore _store;

    public DependencyPresetStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new DependencyPresetStore(_root);
    }

    private static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoadAsync_WithNoFile_ReturnsEmpty()
    {
        Assert.Empty(await _store.LoadAsync(Token));
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAndSortsByName()
    {
        await _store.SaveAsync(new DependencyPreset("Zeta", "{}"), Token);
        await _store.SaveAsync(new DependencyPreset("Alpha", "{}"), Token);

        var presets = await _store.LoadAsync(Token);

        Assert.Equal(new[] { "Alpha", "Zeta" }, presets.Select(preset => preset.Name));
    }

    [Fact]
    public async Task SaveAsync_SameNameReplacesRatherThanDuplicates()
    {
        await _store.SaveAsync(new DependencyPreset("Stack", "first"), Token);
        await _store.SaveAsync(new DependencyPreset("stack", "second"), Token);

        var preset = Assert.Single(await _store.LoadAsync(Token));
        Assert.Equal("second", preset.SubgraphJson);
    }

    [Fact]
    public async Task SaveAsync_BlankName_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(new DependencyPreset("  ", "{}"), Token));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheNamedPreset()
    {
        await _store.SaveAsync(new DependencyPreset("Keep", "{}"), Token);
        await _store.SaveAsync(new DependencyPreset("Drop", "{}"), Token);

        var remaining = await _store.DeleteAsync("Drop", Token);

        Assert.Equal(new[] { "Keep" }, remaining.Select(preset => preset.Name));
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_FallsBackToEmpty()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dependency-presets.json"), "{ not json", Token);

        Assert.Empty(await _store.LoadAsync(Token));
    }

    [Fact]
    public async Task SaveAsync_WhenFull_RejectsInsteadOfSilentlyDroppingAPreset()
    {
        for (var index = 0; index < DependencyPresetStore.MaxPresets; index++)
            await _store.SaveAsync(new DependencyPreset($"preset-{index:D3}", "{}"), Token);

        var seeded = await _store.LoadAsync(Token);
        var missing = Enumerable.Range(0, DependencyPresetStore.MaxPresets)
            .Select(index => $"preset-{index:D3}")
            .Where(name => !seeded.Any(preset => preset.Name == name))
            .ToList();
        Assert.True(missing.Count == 0, $"Every save must persist. Missing: {string.Join(",", missing)}");

        // "zzz" sorts last, which the previous cap-after-sort logic discarded without telling anyone.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(new DependencyPreset("zzz", "{}"), Token));

        var presets = await _store.LoadAsync(Token);
        Assert.Equal(DependencyPresetStore.MaxPresets, presets.Count);
        Assert.DoesNotContain(presets, preset => preset.Name == "zzz");
    }

    [Fact]
    public async Task SaveAsync_WhenFull_StillAllowsReplacingAnExistingPreset()
    {
        for (var index = 0; index < DependencyPresetStore.MaxPresets; index++)
            await _store.SaveAsync(new DependencyPreset($"preset-{index:D3}", "{}"), Token);

        Assert.Equal(DependencyPresetStore.MaxPresets, (await _store.LoadAsync(Token)).Count);

        var presets = await _store.SaveAsync(new DependencyPreset("preset-000", "replaced"), Token);

        Assert.Equal(DependencyPresetStore.MaxPresets, presets.Count);
        Assert.Equal("replaced", Assert.Single(presets, preset => preset.Name == "preset-000").SubgraphJson);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
