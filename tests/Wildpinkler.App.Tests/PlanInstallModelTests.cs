using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the dependency-plan install records on a profile: <see cref="Profile.LatestPlanInstall"/>
/// picks the most recent run even when the list is not in chronological order, and defaults to null
/// when the profile has no runs yet. The rollback affordance depends on this selection.
/// </summary>
public class PlanInstallModelTests
{
    private static PlanInstall Run(string root, DateTimeOffset when, int modCount = 1)
    {
        var record = new PlanInstall
        {
            RootModName = root,
            InstalledAt = when
        };
        for (var i = 0; i < modCount; i++)
            record.ModIds.Add($"mod-{i}");
        return record;
    }

    [Fact]
    public void LatestPlanInstallReturnsMostRecent()
    {
        var profile = new Profile { Name = "Test" };
        var older = Run("Old", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Run("New", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));
        profile.PlanInstalls.Add(older);
        profile.PlanInstalls.Add(newer);

        Assert.Same(newer, profile.LatestPlanInstall);
    }

    [Fact]
    public void LatestPlanInstallIsTolerantOfUnorderedRecords()
    {
        var profile = new Profile { Name = "Test" };
        var oldest = Run("Oldest", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var middle = Run("Middle", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var newest = Run("Newest", new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero));
        profile.PlanInstalls.Add(middle);
        profile.PlanInstalls.Add(newest);
        profile.PlanInstalls.Add(oldest);

        Assert.Same(newest, profile.LatestPlanInstall);
    }

    [Fact]
    public void LatestPlanInstallIsNullWhenNoRuns()
    {
        var profile = new Profile { Name = "Test" };

        Assert.Null(profile.LatestPlanInstall);
    }

    [Fact]
    public void TiedTimestampsReturnTheLastRecorded()
    {
        // Strictly-newer wins; on a tie the later record in the list wins (the >= comparison),
        // so the rollback affordance is deterministic.
        var profile = new Profile { Name = "Test" };
        var first = Run("First", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var second = Run("Second", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
        profile.PlanInstalls.Add(first);
        profile.PlanInstalls.Add(second);

        Assert.Same(second, profile.LatestPlanInstall);
    }

    [Fact]
    public void PlanInstallCarriesItsDependencyEdges()
    {
        var dependency = new PlanInstallDependency
        {
            TargetKey = "2",
            TargetName = "Base",
            Kind = "Required",
            Notes = "needed",
            ModKey = "2",
            FileKey = "20"
        };
        var record = new PlanInstall
        {
            RootModName = "Root",
            Dependencies = new List<PlanInstallDependency> { dependency }
        };

        Assert.Single(record.Dependencies);
        Assert.Equal("needed", record.Dependencies[0].Notes);
        Assert.Equal("Required", record.Dependencies[0].Kind);
    }
}
