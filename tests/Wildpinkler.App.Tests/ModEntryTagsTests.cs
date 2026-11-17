using System;
using System.Collections.Generic;
using System.Text.Json;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModEntryTagsTests
{
    [Fact]
    public void Tags_RoundTripsThroughJson()
    {
        var entry = new ModEntry { Id = "m1", Name = "Mod", Tags = ["UI", "Graphics"] };
        var json = JsonSerializer.Serialize(entry);
        var back = JsonSerializer.Deserialize<ModEntry>(json)!;
        Assert.Equal(new List<string> { "UI", "Graphics" }, back.Tags);
    }

    [Fact]
    public void Tags_DefaultsToEmpty()
    {
        var entry = new ModEntry { Id = "m2" };
        Assert.Empty(entry.Tags);
        Assert.Equal(string.Empty, entry.TagText);
    }

    [Fact]
    public void TagText_JoinsWithComma()
    {
        var entry = new ModEntry { Id = "m3", Tags = ["UI", "Audio"] };
        Assert.Equal("UI, Audio", entry.TagText);
    }

    [Fact]
    public void UpdateFrom_CopiesTags()
    {
        var source = new ModEntry { Id = "s", Tags = ["Framework"] };
        var target = new ModEntry { Id = "t" };
        target.UpdateFrom(source);
        Assert.Equal(new List<string> { "Framework" }, target.Tags);
    }
}
