using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ActiveRunRegistryTests
{
    [Fact]
    public void TryReserve_RejectsSecondTargetForSameProfile()
    {
        var registry = new ActiveRunRegistry();
        var profile = CreateProfile("profile-a");

        Assert.True(registry.TryReserve(profile, CreateTarget("game"), out var first));
        Assert.False(registry.TryReserve(profile, CreateTarget("tool"), out var second));
        Assert.Same(first, second);
        Assert.True(registry.HasRun(profile.Id));
        Assert.True(registry.HasRun(profile.Id, "game"));
        Assert.False(registry.HasRun(profile.Id, "tool"));
    }

    [Fact]
    public void MarkRunning_UpdatesReservationAndRaisesStarted()
    {
        var registry = new ActiveRunRegistry();
        var profile = CreateProfile("profile-a");
        ActiveRun? started = null;
        registry.RunStarted += (_, run) => started = run;
        Assert.True(registry.TryReserve(profile, CreateTarget("game"), out var reservation));

        Assert.True(registry.MarkRunning(reservation));

        Assert.NotNull(started);
        Assert.Equal(ActiveRunState.Running, started.State);
        Assert.True(registry.TryGetRun(profile.Id, out var current));
        Assert.Equal(ActiveRunState.Running, current!.State);
    }

    [Fact]
    public void Release_RaisesEndedOnceAndAllowsNextReservation()
    {
        var registry = new ActiveRunRegistry();
        var profile = CreateProfile("profile-a");
        var ended = new List<ActiveRun>();
        registry.RunEnded += (_, run) => ended.Add(run);
        Assert.True(registry.TryReserve(profile, CreateTarget("game"), out var reservation));
        Assert.True(registry.MarkRunning(reservation));

        Assert.True(registry.Release(reservation));
        Assert.False(registry.Release(reservation));
        Assert.Single(ended);
        Assert.False(registry.HasRun(profile.Id));
        Assert.True(registry.TryReserve(profile, CreateTarget("tool"), out _));
    }

    [Fact]
    public void Release_WithStaleRun_DoesNotReleaseNewReservation()
    {
        var registry = new ActiveRunRegistry();
        var profile = CreateProfile("profile-a");
        Assert.True(registry.TryReserve(profile, CreateTarget("game"), out var first));
        Assert.True(registry.Release(first));
        Assert.True(registry.TryReserve(profile, CreateTarget("tool"), out var second));

        Assert.False(registry.Release(first));
        Assert.True(registry.HasRun(profile.Id, second.TargetId));
    }

    private static Profile CreateProfile(string id) => new() { Id = id, Name = id };

    private static LaunchTarget CreateTarget(string id) => new(
        id,
        id,
        id == "game" ? LaunchTargetKind.Game : LaunchTargetKind.Tool,
        "target.exe",
        string.Empty,
        string.Empty,
        "target.exe",
        string.Empty,
        Array.Empty<MergedView>(),
        new Dictionary<string, string>(),
        Array.Empty<string>(),
        "profile.json",
        false,
        string.Empty);
}