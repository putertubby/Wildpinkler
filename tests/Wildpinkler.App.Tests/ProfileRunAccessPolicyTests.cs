using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileRunAccessPolicyTests
{
    [Fact]
    public void CanModify_WhenProfileIsIdle_ReturnsTrue()
    {
        var policy = new ProfileRunAccessPolicy(new ActiveRunRegistry());

        Assert.True(policy.CanModify(CreateProfile()));
    }

    [Fact]
    public void EnsureCanModify_WhenProfileHasReservedRun_Throws()
    {
        var registry = new ActiveRunRegistry();
        var profile = CreateProfile();
        Assert.True(registry.TryReserve(profile, CreateTarget(), out _));
        var policy = new ProfileRunAccessPolicy(registry);

        var exception = Assert.Throws<InvalidOperationException>(() => policy.EnsureCanModify(profile));

        Assert.Contains(profile.Name, exception.Message, StringComparison.Ordinal);
    }

    private static Profile CreateProfile() => new() { Id = "profile", Name = "Profile" };

    private static LaunchTarget CreateTarget() => new(
        "game", "Game", LaunchTargetKind.Game, "game.exe", string.Empty, string.Empty,
        "game.exe", string.Empty, Array.Empty<MergedView>(), new Dictionary<string, string>(),
        Array.Empty<string>(), "profile.json", false, string.Empty);
}