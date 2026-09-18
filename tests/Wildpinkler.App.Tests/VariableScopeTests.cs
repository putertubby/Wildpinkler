using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class VariableScopeTests
{
    [Fact]
    public void Expand_SubstitutesADefinedVariable()
    {
        var scope = new VariableScope();
        scope.Set("GameInstallPath", @"C:\Games\Skyrim");

        Assert.Equal(@"C:\Games\Skyrim\Data", scope.Expand("${GameInstallPath}\\Data"));
    }

    [Fact]
    public void Expand_ResolvesAVariableThatReferencesAnother()
    {
        var scope = new VariableScope();
        scope.Set("Root", @"C:\Games");
        scope.Set("Game", "${Root}\\Skyrim");

        Assert.Equal(@"C:\Games\Skyrim", scope.Expand("${Game}"));
    }

    [Fact]
    public void TryExpand_UnknownVariable_ReportsAnErrorInsteadOfSubstitutingNothing()
    {
        var scope = new VariableScope();

        Assert.False(scope.TryExpand("${Missing}\\Data", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryExpand_SelfReferencingVariable_FailsInsteadOfLooping()
    {
        var scope = new VariableScope();
        scope.Set("Loop", "${Loop}");

        Assert.False(scope.TryExpand("${Loop}", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryExpand_MutuallyRecursiveVariables_FailInsteadOfLooping()
    {
        var scope = new VariableScope();
        scope.Set("A", "${B}");
        scope.Set("B", "${A}");

        Assert.False(scope.TryExpand("${A}", out _, out _));
    }

    [Fact]
    public void SetReadOnly_MarksTheNameAsNotOverridable()
    {
        var scope = new VariableScope();
        scope.SetReadOnly("SystemRoot", @"C:\Windows");

        Assert.Contains("SystemRoot", scope.ReadOnlyNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_SettingACaseExactReadOnlyName_IsIgnoredAsABackstop()
    {
        var scope = new VariableScope();
        scope.SetReadOnly("SystemRoot", @"C:\Windows");

        // Validation should reject such a name up front; this is only the backstop.
        scope.Set("SystemRoot", @"C:\Elsewhere");

        Assert.Equal(@"C:\Windows", scope.Expand("${SystemRoot}"));
    }

    [Fact]
    public void LocalVariable_ACaseVariantOfASystemName_IsDistinctFromTheSystemVariable()
    {
        var scope = new VariableScope();
        scope.SetReadOnly("LocalAppData", @"C:\Users\test\AppData\Local");
        scope.Set("localappdata", @"C:\Users\test\AppData\Local\Skyrim Special Edition");

        Assert.Equal(@"C:\Users\test\AppData\Local\Skyrim Special Edition", scope.Expand("${localappdata}"));
        Assert.Equal(@"C:\Users\test\AppData\Local", scope.Expand("${LocalAppData}"));

        var resolved = scope.ResolveAll();
        Assert.True(resolved.ContainsKey("localappdata"));
        Assert.True(resolved.ContainsKey("LocalAppData"));
        Assert.Equal(@"C:\Users\test\AppData\Local\Skyrim Special Edition", resolved["localappdata"]);
        Assert.Equal(@"C:\Users\test\AppData\Local", resolved["LocalAppData"]);
    }

    [Theory]
    [InlineData("LocalAppData", true)]
    [InlineData("InstallPath", true)]
    [InlineData("ProfilePath", true)]
    [InlineData("Documents", true)]
    [InlineData("LocalAppDataLow", true)]
    [InlineData("localappdata", false)]
    [InlineData("Installpath", false)]
    [InlineData("MyLocalAppData", false)]
    public void SystemVariables_IsNameReserved_MatchesCaseExactly(string name, bool expected)
    {
        Assert.Equal(expected, SystemVariables.IsNameReserved(name));
    }

    [Fact]
    public void SetAll_AppliesEveryPairAndLastWriteWins()
    {
        var scope = new VariableScope();
        scope.SetAll(new[]
        {
            new KeyValuePair<string, string>("One", "1"),
            new KeyValuePair<string, string>("Two", "2"),
            new KeyValuePair<string, string>("One", "overwritten")
        });

        Assert.Equal("overwritten", scope.Expand("${One}"));
        Assert.Equal("2", scope.Expand("${Two}"));
    }

    [Fact]
    public void ResolveAll_ExpandsEveryDefinition()
    {
        var scope = new VariableScope();
        scope.Set("Root", @"C:\Games");
        scope.Set("Game", "${Root}\\Skyrim");

        var resolved = scope.ResolveAll();

        Assert.Equal(@"C:\Games\Skyrim", resolved["Game"]);
    }

    [Fact]
    public void Expand_ValueWithoutPlaceholders_IsReturnedUnchanged()
    {
        Assert.Equal(@"C:\literal\path", new VariableScope().Expand(@"C:\literal\path"));
    }

    [Fact]
    public void SystemVariables_AddTo_ProvidesReservedReadOnlyNames()
    {
        var scope = new VariableScope();
        SystemVariables.AddTo(scope);

        Assert.NotEmpty(SystemVariables.ReservedNames);
        foreach (var name in SystemVariables.ReservedNames)
            Assert.Contains(name, scope.ReadOnlyNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Symbolize_ReplacesTheLongestMatchingReadOnlyPrefix()
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows",
            ["System32"] = @"C:\Windows\System32"
        };

        var symbolized = VariableScope.Symbolize(@"C:\Windows\System32\drivers", resolved, resolved.Keys.ToList());

        Assert.Contains("System32", symbolized, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Windows\System32", symbolized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Symbolize_RequiresADirectorySeparatorBoundary()
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppDataLocal"] = @"C:\Users\test\AppData\Local"
        };

        var symbolized = VariableScope.Symbolize(@"C:\Users\test\AppData\LocalLow\game", resolved, new[] { "AppDataLocal" });

        // `…\AppData\Local` is a textual prefix of `…\AppData\LocalLow` but not a full path segment,
        // so no placeholder may be introduced.
        Assert.Equal(@"C:\Users\test\AppData\LocalLow\game", symbolized);
    }

    [Fact]
    public void Symbolize_ExactMatch_YieldsTheBarePlaceholder()
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppDataLocal"] = @"C:\Users\test\AppData\Local"
        };

        var symbolized = VariableScope.Symbolize(@"C:\Users\test\AppData\Local", resolved, new[] { "AppDataLocal" });

        Assert.Equal("${AppDataLocal}", symbolized);
    }

    [Fact]
    public void Symbolize_KeepsTheSuffixAfterThePlaceholder()
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppDataLocal"] = @"C:\Users\test\AppData\Local"
        };

        var symbolized = VariableScope.Symbolize(@"C:\Users\test\AppData\Local\Skyrim Special Edition", resolved, new[] { "AppDataLocal" });

        Assert.Equal("${AppDataLocal}\\Skyrim Special Edition", symbolized);
    }
}
