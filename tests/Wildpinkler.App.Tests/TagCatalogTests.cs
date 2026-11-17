using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class TagCatalogTests
{
    [Fact]
    public void Ensure_NullSeedsDefaults()
    {
        var tags = TagCatalog.Ensure(null);
        Assert.Equal(TagCatalog.DefaultTags, tags);
    }

    [Fact]
    public void Ensure_EmptySeedsDefaults()
    {
        var tags = TagCatalog.Ensure(new List<string>());
        Assert.Equal(TagCatalog.DefaultTags, tags);
    }

    [Fact]
    public void Ensure_ExistingIsPreserved()
    {
        var existing = new List<string> { "UI", "MyTag" };
        var tags = TagCatalog.Ensure(existing);
        Assert.Same(existing, tags);
        Assert.Equal(new List<string> { "UI", "MyTag" }, tags);
    }

    [Fact]
    public void Ensure_NoSeedReturnsEmpty()
    {
        var tags = TagCatalog.Ensure(null, seedsDefaults: false);
        Assert.Empty(tags);
    }

    [Fact]
    public void Add_NewTagIsAddedAndReturnsTrue()
    {
        var tags = new List<string> { "UI" };
        Assert.True(TagCatalog.Add(tags, "Graphics"));
        Assert.Equal(new List<string> { "UI", "Graphics" }, tags);
    }

    [Fact]
    public void Add_DuplicateIsIgnoredCaseInsensitively()
    {
        var tags = new List<string> { "UI" };
        Assert.False(TagCatalog.Add(tags, "ui"));
        Assert.Single(tags);
    }

    [Fact]
    public void Add_TrimsWhitespaceAndRejectsEmpty()
    {
        var tags = new List<string>();
        Assert.True(TagCatalog.Add(tags, "  Performance  "));
        Assert.Equal(new List<string> { "Performance" }, tags);
        Assert.False(TagCatalog.Add(tags, "   "));
    }

    [Fact]
    public void Remove_ExistingRemovesAndReturnsTrue()
    {
        var tags = new List<string> { "UI", "Audio" };
        Assert.True(TagCatalog.Remove(tags, "ui"));
        Assert.Equal(new List<string> { "Audio" }, tags);
    }

    [Fact]
    public void Remove_MissingReturnsFalse()
    {
        var tags = new List<string> { "UI" };
        Assert.False(TagCatalog.Remove(tags, "Graphics"));
        Assert.Equal(new List<string> { "UI" }, tags);
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var tags = new List<string> { "UI", "Custom" };
        TagCatalog.Reset(tags);
        Assert.Equal(TagCatalog.DefaultTags, tags);
    }

    [Fact]
    public void MatchesAny_EmptyActiveMatchesAll()
    {
        Assert.True(TagCatalog.MatchesAny(new List<string> { "UI" }, new List<string>()));
        Assert.True(TagCatalog.MatchesAny(new List<string>(), new List<string>()));
    }

    [Fact]
    public void MatchesAny_OverlappingTagMatches()
    {
        Assert.True(TagCatalog.MatchesAny(new List<string> { "UI", "Audio" }, new List<string> { "audio" }));
        Assert.False(TagCatalog.MatchesAny(new List<string> { "UI" }, new List<string> { "Audio" }));
    }
}
