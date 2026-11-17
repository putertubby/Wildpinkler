using System;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class PathProvenanceTests
{
    [Fact]
    public void Derive_TouchedIsAlwaysCustom()
    {
        Assert.Equal(PathProvenance.Custom, PathProvenanceResolver.Derive(true, "Data"));
        Assert.Equal(PathProvenance.Custom, PathProvenanceResolver.Derive(true, null));
        Assert.Equal(PathProvenance.Custom, PathProvenanceResolver.Derive(true, ""));
    }

    [Fact]
    public void Derive_NotTouchedWithRememberedIsRemembered()
    {
        Assert.Equal(PathProvenance.Remembered, PathProvenanceResolver.Derive(false, "Data"));
    }

    [Fact]
    public void Derive_NotTouchedWithoutRememberedIsDefault()
    {
        Assert.Equal(PathProvenance.Default, PathProvenanceResolver.Derive(false, null));
        Assert.Equal(PathProvenance.Default, PathProvenanceResolver.Derive(false, ""));
        Assert.Equal(PathProvenance.Default, PathProvenanceResolver.Derive(false, "   "));
    }
}
