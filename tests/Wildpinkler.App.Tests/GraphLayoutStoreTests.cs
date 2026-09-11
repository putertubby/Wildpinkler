using System;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class GraphLayoutStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-graph-" + Guid.NewGuid().ToString("N"));
    private readonly GraphLayoutStore _store;

    public GraphLayoutStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new GraphLayoutStore(_root);
    }

    private static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoadAsync_WithNoFile_ReturnsEmptySnapshot()
    {
        var snapshot = await _store.LoadAsync(Token);

        Assert.Empty(snapshot.Nodes);
        Assert.Empty(snapshot.Bends);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsPositionsAndBends()
    {
        var snapshot = new GraphLayoutSnapshot();
        snapshot.Nodes["mod"] = new GraphLayoutPoint(12.5, 34.5);
        snapshot.Bends["edge"] = new GraphLayoutPoint(7, 8);

        await _store.SaveAsync(snapshot, Token);
        var reloaded = await _store.LoadAsync(Token);

        Assert.Equal(12.5, reloaded.Nodes["mod"].X);
        Assert.Equal(34.5, reloaded.Nodes["mod"].Y);
        Assert.Equal(7, reloaded.Bends["edge"].X);
        Assert.Equal(8, reloaded.Bends["edge"].Y);
    }

    [Fact]
    public async Task LoadAsync_FutureSchemaVersion_FallsBackToEmpty()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "graph-layout.json"), """
            { "SchemaVersion": 99, "Nodes": { "mod": { "X": 1, "Y": 2 } }, "Bends": {} }
            """, Token);

        Assert.Empty((await _store.LoadAsync(Token)).Nodes);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_FallsBackToEmptyInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "graph-layout.json"), "{ not json", Token);

        Assert.Empty((await _store.LoadAsync(Token)).Nodes);
    }

    [Fact]
    public async Task SaveAsync_OverwritesAPreviousLayout()
    {
        var first = new GraphLayoutSnapshot();
        first.Nodes["mod"] = new GraphLayoutPoint(1, 1);
        await _store.SaveAsync(first, Token);

        var second = new GraphLayoutSnapshot();
        second.Nodes["other"] = new GraphLayoutPoint(2, 2);
        await _store.SaveAsync(second, Token);

        var reloaded = await _store.LoadAsync(Token);
        Assert.False(reloaded.Nodes.ContainsKey("mod"));
        Assert.True(reloaded.Nodes.ContainsKey("other"));
    }

    [Fact]
    public async Task LoadAsync_DropsPlacementsACanvasCannotUse()
    {
        var json = "{ \"SchemaVersion\": 1, \"Nodes\": { \"sane\": { \"X\": 10, \"Y\": 20 }, \"absurd\": { \"X\": 1e300, \"Y\": 5 } },"
                   + " \"Bends\": { \"huge\": { \"X\": 5, \"Y\": -1e300 } } }";
        await File.WriteAllTextAsync(Path.Combine(_root, "graph-layout.json"), json, Token);

        var snapshot = await _store.LoadAsync(Token);

        Assert.Equal(new[] { "sane" }, snapshot.Nodes.Keys);
        Assert.Empty(snapshot.Bends);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
