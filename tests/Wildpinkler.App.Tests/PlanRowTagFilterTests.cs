using System;
using System.Collections.Generic;
using Wildpinkler.App.Pages;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class PlanRowTagFilterTests
{
    private static UnifiedPlanRow RowWithTags(params string[] tags)
        => new(key: "plan:test", null, null) { Tags = new List<string>(tags) };

    [Fact]
    public void RowMatchesTag_NoActiveTagMatchesAll()
    {
        Assert.True(ProfileContentView.RowMatchesTag(RowWithTags("UI"), null));
        Assert.True(ProfileContentView.RowMatchesTag(RowWithTags("UI"), ""));
    }

    [Fact]
    public void RowMatchesTag_ExactTagMatchesCaseInsensitively()
    {
        var row = RowWithTags("UI", "Audio");
        Assert.True(ProfileContentView.RowMatchesTag(row, "audio"));
        Assert.True(ProfileContentView.RowMatchesTag(row, "UI"));
    }

    [Fact]
    public void RowMatchesTag_NoMatchingTagDoesNotMatch()
    {
        var row = RowWithTags("UI", "Audio");
        Assert.False(ProfileContentView.RowMatchesTag(row, "Graphics"));
    }

    [Fact]
    public void RowMatchesTag_UntaggedRowOnlyMatchesWhenNoActiveTag()
    {
        var row = RowWithTags();
        Assert.True(ProfileContentView.RowMatchesTag(row, null));
        Assert.False(ProfileContentView.RowMatchesTag(row, "UI"));
    }
}
