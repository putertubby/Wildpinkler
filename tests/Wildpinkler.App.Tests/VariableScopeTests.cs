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
}
