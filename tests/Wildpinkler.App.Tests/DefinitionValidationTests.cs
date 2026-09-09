using System;
using System.IO;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Definition files are user-supplied and can be shared between machines, so these checks are the
/// boundary that stops a definition from reaching outside the folder it declares.
/// </summary>
public sealed class DefinitionValidationTests
{
    [Theory]
    [InlineData("skyrim-se")]
    [InlineData("fallout4")]
    [InlineData("tool.bodyslide")]
    public void IsDefinitionId_AcceptsOrdinaryIdentifiers(string id)
    {
        Assert.True(DefinitionValidation.IsDefinitionId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a..b")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    [InlineData("C:")]
    public void IsDefinitionId_RejectsAnythingThatCouldTraverse(string? id)
    {
        Assert.False(DefinitionValidation.IsDefinitionId(id));
    }

    [Theory]
    [InlineData("Data")]
    [InlineData("Data/Meshes")]
    [InlineData("Data\\Textures")]
    public void IsSafeRelativePath_AcceptsPathsInsideTheFolder(string path)
    {
        Assert.True(DefinitionValidation.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\Windows")]
    [InlineData("\\\\server\\share")]
    [InlineData("/rooted")]
    [InlineData("\\rooted")]
    [InlineData("has:colon")]
    public void IsSafeRelativePath_RejectsRootedOrQualifiedPaths(string path)
    {
        Assert.False(DefinitionValidation.IsSafeRelativePath(path));
    }

    [Fact]
    public void TryResolveUnder_KeepsTheResultInsideTheInstallFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp-resolve");

        Assert.True(DefinitionValidation.TryResolveUnder(root, "Data/Meshes", out var resolved));
        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("Data/../../escape")]
    [InlineData("C:\\Windows\\System32")]
    public void TryResolveUnder_RefusesToEscapeTheInstallFolder(string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "wp-resolve");

        Assert.False(DefinitionValidation.TryResolveUnder(root, relativePath, out _));
    }

    [Theory]
    [InlineData("GameInstallPath")]
    [InlineData("ToolInstallPath")]
    [InlineData("My_Variable")]
    public void IsVariableName_AcceptsIdentifierLikeNames(string name)
    {
        Assert.True(DefinitionValidation.IsVariableName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("${nested}")]
    public void IsVariableName_RejectsNamesThatWouldBreakExpansion(string? name)
    {
        Assert.False(DefinitionValidation.IsVariableName(name));
    }

    [Fact]
    public void IsBranchPath_RejectsEmptyValues()
    {
        Assert.False(DefinitionValidation.IsBranchPath(null));
        Assert.False(DefinitionValidation.IsBranchPath("   "));
    }
}
