using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfilePlanStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wildpinkler-plan-test-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    public ProfilePlanStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ProfilePlanStore Store() => new(_root);

    [Fact]
    public async Task Roots_RoundTrip_PreservingOrder()
    {
        var store = Store();
        var snapshot = new ProfilePlanSnapshot { ProfileId = "p1" };
        snapshot.Roots.Add(new PlanRootRef { SiteId = "nexus", GameKey = "skse", ModKey = "1" });
        snapshot.Roots.Add(new PlanRootRef { SiteId = "nexus", GameKey = "skse", ModKey = "2", PageUrl = "https://example.invalid/mod" });
        await store.SaveAsync(snapshot, _token);

        var loaded = await store.LoadAsync("p1", _token);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Roots.Count);
        Assert.Equal("1", loaded.Roots[0].ModKey);
        Assert.Equal("2", loaded.Roots[1].ModKey);
        Assert.Equal("https://example.invalid/mod", loaded.Roots[1].PageUrl);

        // The ref mapping round-trips into a RemoteRef usable for resolution.
        var first = loaded.Roots[0].ToRef();
        Assert.Equal("nexus", first.SiteId);
        Assert.Equal("skse", first.GameKey);
        Assert.Equal("1", first.ModKey);
    }

    [Fact]
    public async Task LegacySnapshotWithoutRoots_LoadsWithEmptyRoots()
    {
        var store = Store();
        var snapshot = new ProfilePlanSnapshot { ProfileId = "p2" };
        await store.SaveAsync(snapshot, _token);

        var loaded = await store.LoadAsync("p2", _token);
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Roots);
    }

    [Fact]
    public async Task RowState_RoundTrips_SourceRoot()
    {
        var store = Store();
        var snapshot = new ProfilePlanSnapshot { ProfileId = "p6" };
        snapshot.Rows.Add(new ProfilePlanRowState
        {
            Key = "k1",
            Name = "Manual mod",
            Destination = "Data\\Mod",
            SourceRoot = "Mod\\files",
            RememberPath = true
        });
        // A second row with no source root (FOMOD) stays null on round-trip.
        snapshot.Rows.Add(new ProfilePlanRowState { Key = "k2", Name = "FOMOD mod", Destination = "Data" });
        await store.SaveAsync(snapshot, _token);

        var loaded = await store.LoadAsync("p6", _token);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Rows.Count);
        Assert.Equal("Data\\Mod", loaded.Rows[0].Destination);
        Assert.Equal("Mod\\files", loaded.Rows[0].SourceRoot);
        Assert.True(loaded.Rows[0].RememberPath);
        Assert.Equal("Data", loaded.Rows[1].Destination);
        Assert.Null(loaded.Rows[1].SourceRoot);
    }

    [Fact]
    public async Task LocalDecisions_RoundTrip()
    {
        var store = Store();
        var decision = new PlanDecision("nexus::skse::42", PlanDecisionKind.Exclude, null, "Some mod", null);
        await store.SaveLocalDecisionAsync("p3", decision, _token);

        var loaded = await store.LoadAsync("p3", _token);
        Assert.NotNull(loaded);
        Assert.True(loaded!.LocalDecisions.ContainsKey("nexus::skse::42"));
        Assert.Equal(PlanDecisionKind.Exclude, loaded.LocalDecisions["nexus::skse::42"].Kind);
    }

    [Fact]
    public async Task RemoveLocalDecision_RemovesOnlyThatKey()
    {
        var store = Store();
        await store.SaveLocalDecisionAsync("p4", new PlanDecision("a", PlanDecisionKind.Exclude), _token);
        await store.SaveLocalDecisionAsync("p4", new PlanDecision("b", PlanDecisionKind.Exclude), _token);
        var removed = await store.TryRemoveLocalDecisionAsync("p4", "a", _token);
        Assert.True(removed);
        Assert.False(await store.TryRemoveLocalDecisionAsync("p4", "missing", _token));

        var loaded = await store.LoadAsync("p4", _token);
        Assert.NotNull(loaded);
        Assert.False(loaded!.LocalDecisions.ContainsKey("a"));
        Assert.True(loaded.LocalDecisions.ContainsKey("b"));
    }

    [Fact]
    public async Task ClearLocalDecisions_KeepsRowsAndRoots()
    {
        var store = Store();
        var snapshot = new ProfilePlanSnapshot { ProfileId = "p5" };
        snapshot.Roots.Add(new PlanRootRef { SiteId = "nexus", GameKey = "g", ModKey = "9" });
        snapshot.Rows.Add(new ProfilePlanRowState { Key = "k1", Name = "Mod" });
        await store.SaveAsync(snapshot, _token);
        await store.SaveLocalDecisionAsync("p5", new PlanDecision("x", PlanDecisionKind.Exclude), _token);
        await store.ClearLocalDecisionsAsync("p5", _token);

        var loaded = await store.LoadAsync("p5", _token);
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.LocalDecisions);
        Assert.Single(loaded.Roots);
        Assert.Single(loaded.Rows);
    }
}
