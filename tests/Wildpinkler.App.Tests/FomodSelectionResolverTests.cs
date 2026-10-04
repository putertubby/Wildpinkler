using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Covers the FOMOD conditional-dependency helpers: plugin visibility, flag-driven type
/// resolution, and group validation that rejects NotUsable options.
/// </summary>
public sealed class FomodSelectionResolverTests
{
    private static readonly FomodDependencyResolver Evaluator = new();

    private static FomodPlugin Plugin(string name, FomodPluginType? staticType = null,
        FomodPluginType? defaultType = null, params (FomodDependency Dependency, FomodPluginType Type)[] patterns)
    {
        var plugin = new FomodPlugin { Name = name, StaticType = staticType };
        plugin.DependencyDefaultType = defaultType;
        foreach (var (dependency, type) in patterns)
            plugin.DependencyPatterns.Add(new FomodTypePattern { Dependency = dependency, Type = type });
        return plugin;
    }

    private static FomodFlagDependency Flag(string name, string value) => new() { Flag = name, Value = value };

    [Fact]
    public void IsPluginVisible_NoDependency_AlwaysVisible()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("A");
        Assert.True(engine.IsPluginVisible(plugin, new Dictionary<string, string>(), NullFomodFileStateProvider.Instance));
        Assert.True(engine.IsPluginVisible(plugin, new Dictionary<string, string> { ["preset"] = "4k" }, NullFomodFileStateProvider.Instance));
    }

    [Fact]
    public void IsPluginVisible_FlagDependency_MatchesCurrentFlags()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("A");
        plugin.VisibilityDependency = Flag("preset", "4k");

        Assert.False(engine.IsPluginVisible(plugin, new Dictionary<string, string>(), NullFomodFileStateProvider.Instance));
        Assert.True(engine.IsPluginVisible(plugin, new Dictionary<string, string> { ["preset"] = "4k" }, NullFomodFileStateProvider.Instance));
        Assert.False(engine.IsPluginVisible(plugin, new Dictionary<string, string> { ["preset"] = "2k" }, NullFomodFileStateProvider.Instance));
    }

    [Fact]
    public void ResolvePluginType_NoPatterns_ReturnsStaticType()
    {
        var engine = new FomodSelectionResolver();
        var flags = new Dictionary<string, string>();
        Assert.Equal(FomodPluginType.Recommended, engine.ResolvePluginType(Plugin("A", FomodPluginType.Recommended), flags, NullFomodFileStateProvider.Instance));
        Assert.Equal(FomodPluginType.Optional, engine.ResolvePluginType(Plugin("A"), flags, NullFomodFileStateProvider.Instance));
    }

    [Fact]
    public void ResolvePluginType_FirstMatchingPattern_WinsOverDefaultAndStatic()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("A", FomodPluginType.Optional, FomodPluginType.Optional,
            (Flag("preset", "4k"), FomodPluginType.Recommended),
            (Flag("preset", "1080p"), FomodPluginType.NotUsable));

        Assert.Equal(FomodPluginType.Recommended, engine.ResolvePluginType(plugin, new Dictionary<string, string> { ["preset"] = "4k" }, NullFomodFileStateProvider.Instance));
        Assert.Equal(FomodPluginType.NotUsable, engine.ResolvePluginType(plugin, new Dictionary<string, string> { ["preset"] = "1080p" }, NullFomodFileStateProvider.Instance));
    }

    [Fact]
    public void ResolvePluginType_NoPatternMatches_ReturnsDependencyDefaultType()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("A", FomodPluginType.Optional, FomodPluginType.CouldBeUsable,
            (Flag("preset", "4k"), FomodPluginType.Recommended));

        Assert.Equal(FomodPluginType.CouldBeUsable, engine.ResolvePluginType(plugin, new Dictionary<string, string> { ["preset"] = "2k" }, NullFomodFileStateProvider.Instance));
    }

    private static FomodGroup Group(FomodGroupType type, params FomodPlugin[] plugins) =>
        new() { Name = "Group", Type = type, Plugins = plugins.ToList() };

    [Fact]
    public void ValidateGroup_CardinalityStillWins()
    {
        var engine = new FomodSelectionResolver();
        var flags = new Dictionary<string, string>();
        var group = Group(FomodGroupType.SelectExactlyOne, Plugin("A"), Plugin("B"));
        Assert.NotNull(engine.ValidateGroup(group, new List<FomodPlugin>(), flags, NullFomodFileStateProvider.Instance));
    }

    [Fact]
    public void ValidateGroup_RejectsNotUsableSelection()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("4K textures", FomodPluginType.Optional, FomodPluginType.Optional,
            (Flag("preset", "2k"), FomodPluginType.NotUsable));
        var group = Group(FomodGroupType.SelectAny, plugin, Plugin("2K textures"));

        var error = engine.ValidateGroup(group, new List<FomodPlugin> { plugin },
            new Dictionary<string, string> { ["preset"] = "2k" }, NullFomodFileStateProvider.Instance);

        Assert.NotNull(error);
        Assert.Contains("4K textures", error);
        Assert.Contains("not usable", error!.ToLowerInvariant());
    }

    [Fact]
    public void ValidateGroup_AcceptsUsableSelection()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("4K textures", FomodPluginType.Optional, FomodPluginType.Optional,
            (Flag("preset", "2k"), FomodPluginType.NotUsable));
        var group = Group(FomodGroupType.SelectAny, plugin);

        Assert.Null(engine.ValidateGroup(group, new List<FomodPlugin> { plugin },
            new Dictionary<string, string> { ["preset"] = "4k" }, NullFomodFileStateProvider.Instance));
    }

    // ---- G1: SelectAll validation only counts visible plugins ----

    [Fact]
    public void ValidateGroup_SelectAll_IgnoresHiddenPlugins()
    {
        var engine = new FomodSelectionResolver();
        var visible = Plugin("A");
        var hidden = Plugin("B");
        hidden.VisibilityDependency = Flag("preset", "4k");
        var group = Group(FomodGroupType.SelectAll, visible, hidden);
        var flags = new Dictionary<string, string> { ["preset"] = "2k" };

        // Hidden plugin doesn't count; selecting only the visible one is valid.
        Assert.Null(engine.ValidateGroup(group, new List<FomodPlugin> { visible }, flags, NullFomodFileStateProvider.Instance));

        // If the hidden plugin is now visible, the same selection is no longer valid.
        var flags4k = new Dictionary<string, string> { ["preset"] = "4k" };
        Assert.NotNull(engine.ValidateGroup(group, new List<FomodPlugin> { visible }, flags4k, NullFomodFileStateProvider.Instance));
        Assert.Null(engine.ValidateGroup(group, new List<FomodPlugin> { visible, hidden }, flags4k, NullFomodFileStateProvider.Instance));
    }

    // ---- G2: alwaysInstall / installIfUsable ----

    private static FomodFileInstall File(string source, bool alwaysInstall = false, bool installIfUsable = false) =>
        new() { Source = source, AlwaysInstall = alwaysInstall, InstallIfUsable = installIfUsable };

    private static FomodModule ModuleForResolve(params FomodGroup[] groups)
    {
        var step = new FomodInstallStep { Name = "Step", Groups = groups.ToList() };
        return new FomodModule
        {
            Name = "Test",
            RequiredInstallFiles = new List<FomodFileInstall> { File("required.txt") },
            InstallSteps = new List<FomodInstallStep> { step }
        };
    }

    private static FomodStepSelection SelectionOf(FomodGroup group, params FomodPlugin[] selected) =>
        new()
        {
            Step = new FomodInstallStep { Name = "Step" },
            Groups = { new FomodGroupSelection { Group = group, SelectedPlugins = selected.ToList() } }
        };

    [Fact]
    public void ResolveFileInstalls_AlwaysInstall_HonoredRegardlessOfSelection()
    {
        var engine = new FomodSelectionResolver();
        var plugin = Plugin("A", null, null);
        plugin.Files.Add(File("optional.txt", alwaysInstall: true));
        var group = Group(FomodGroupType.SelectAny, plugin);
        var module = ModuleForResolve(group);

        var result = engine.ResolveFileInstalls(module, new[] { SelectionOf(group) }, NullFomodFileStateProvider.Instance)
            .Select(install => install.Source).ToList();

        Assert.Contains("required.txt", result);
        Assert.Contains("optional.txt", result);
    }

    [Fact]
    public void ResolveFileInstalls_InstallIfUsable_SkippedWhenNotUsable()
    {
        var engine = new FomodSelectionResolver();

        // NotUsable static type: installIfUsable must NOT install, and the plugin is not selected.
        var notUsablePlugin = Plugin("A", FomodPluginType.NotUsable);
        notUsablePlugin.Files.Add(File("notusable.txt", installIfUsable: true));
        var notUsableModule = ModuleForResolve(Group(FomodGroupType.SelectAny, notUsablePlugin));
        Assert.DoesNotContain("notusable.txt",
            engine.ResolveFileInstalls(notUsableModule, new[] { SelectionOf(Group(FomodGroupType.SelectAny, notUsablePlugin)) },
                NullFomodFileStateProvider.Instance).Select(install => install.Source));

        // Usable static type: installIfUsable installs even though the plugin is not selected.
        var usablePlugin = Plugin("B", FomodPluginType.Optional);
        usablePlugin.Files.Add(File("usable.txt", installIfUsable: true));
        var usableModule = ModuleForResolve(Group(FomodGroupType.SelectAny, usablePlugin));
        Assert.Contains("usable.txt",
            engine.ResolveFileInstalls(usableModule, new[] { SelectionOf(Group(FomodGroupType.SelectAny, usablePlugin)) },
                NullFomodFileStateProvider.Instance).Select(install => install.Source));
    }

    // ---- G3: tri-state file dependencies ----

    private sealed class FakeFileProvider : IFomodFileStateProvider
    {
        private readonly Dictionary<string, FomodFileState> _states;
        public FakeFileProvider(Dictionary<string, FomodFileState>? states = null)
        {
            _states = states ?? new();
        }
        public FomodFileState GetState(string relativePath) =>
            _states.TryGetValue(relativePath, out var s) ? s : FomodFileState.Missing;
    }

    private sealed class FakeVersionProvider : IFomodVersionProvider
    {
        private readonly bool _result;
        public FakeVersionProvider(bool result = true) => _result = result;
        public bool Satisfies(FomodVersionSpec? required, string subject) => required is null || _result;
    }

    [Fact]
    public void Evaluate_FileDependency_MapsTriStateOneToOne()
    {
        var evaluator = new FomodDependencyResolver();
        var files = new FakeFileProvider(new Dictionary<string, FomodFileState>
        {
            ["active.txt"] = FomodFileState.Active,
            ["inactive.txt"] = FomodFileState.Inactive,
        });

        Assert.True(evaluator.Evaluate(new FomodFileDependency { File = "active.txt", State = FomodFileDependencyState.Active },
            new Dictionary<string, string>(), files));
        Assert.False(evaluator.Evaluate(new FomodFileDependency { File = "inactive.txt", State = FomodFileDependencyState.Active },
            new Dictionary<string, string>(), files));

        Assert.True(evaluator.Evaluate(new FomodFileDependency { File = "inactive.txt", State = FomodFileDependencyState.Inactive },
            new Dictionary<string, string>(), files));
        Assert.False(evaluator.Evaluate(new FomodFileDependency { File = "active.txt", State = FomodFileDependencyState.Inactive },
            new Dictionary<string, string>(), files));

        Assert.True(evaluator.Evaluate(new FomodFileDependency { File = "missing.txt", State = FomodFileDependencyState.Missing },
            new Dictionary<string, string>(), files));
        Assert.False(evaluator.Evaluate(new FomodFileDependency { File = "active.txt", State = FomodFileDependencyState.Missing },
            new Dictionary<string, string>(), files));
    }

    // ---- G4: version gates ----

    [Fact]
    public void Evaluate_GameDependency_UsesVersionProvider()
    {
        var evaluator = new FomodDependencyResolver();
        var dep = new FomodGameDependency { VersionSpec = new FomodVersionSpec { Operator = FomodVersionOperator.GreaterOrEqual, RawValue = "1.2.3" } };
        var flags = new Dictionary<string, string>();

        Assert.True(evaluator.Evaluate(dep, flags, NullFomodFileStateProvider.Instance, new FakeVersionProvider(true)));
        Assert.False(evaluator.Evaluate(dep, flags, NullFomodFileStateProvider.Instance, new FakeVersionProvider(false)));

        // Null spec (no version required) is always satisfied even by a strict provider.
        Assert.True(evaluator.Evaluate(new FomodGameDependency { VersionSpec = null }, flags, NullFomodFileStateProvider.Instance, new FakeVersionProvider(false)));
    }

    [Fact]
    public void FomodVersionProvider_FailsClosedWithoutGameExecutable()
    {
        // A FomodVersionProvider with no game executable path cannot satisfy any gate,
        // for both the "game" and the "fomm" subjects.
        var provider = new FomodVersionProvider(null);
        var spec = new FomodVersionSpec { Operator = FomodVersionOperator.GreaterOrEqual, RawValue = "1.0" };
        Assert.True(provider.Satisfies(null, "game"));
        Assert.False(provider.Satisfies(spec, "game"));
        Assert.False(provider.Satisfies(spec, "fomm"));
    }

    private sealed class RecordingVersionProvider : IFomodVersionProvider
    {
        public string? LastSubject;
        public bool Satisfies(FomodVersionSpec? required, string subject)
        {
            LastSubject = subject;
            return required is null;
        }
    }

    [Fact]
    public void Evaluate_FommDependency_RoutedToVersionProviderWithFommSubject()
    {
        // The resolver must ask the version provider about the "fomm" subject (not "game"),
        // which is what makes the real FomodVersionProvider fail fomm gates closed.
        var evaluator = new FomodDependencyResolver();
        var recording = new RecordingVersionProvider();
        var fomm = new FomodFommDependency { VersionSpec = new FomodVersionSpec { Operator = FomodVersionOperator.Equal, RawValue = "1.0" } };
        evaluator.Evaluate(fomm, new Dictionary<string, string>(), NullFomodFileStateProvider.Instance, recording);
        Assert.Equal("fomm", recording.LastSubject);
    }

    [Fact]
    public void CompareVersions_NumericComponentWise()
    {
        Assert.True(FomodVersionProvider.CompareVersions("1.2.3", "1.2.10") < 0);
        Assert.True(FomodVersionProvider.CompareVersions("1.10.0", "1.2.0") > 0);
        Assert.Equal(0, FomodVersionProvider.CompareVersions("1.2.3.4", "1.2.3.4"));
    }

    [Fact]
    public void CompareVersions_FallsBackToOrdinalForNonNumeric()
    {
        Assert.True(FomodVersionProvider.CompareVersions("beta", "stable") < 0);
        Assert.True(FomodVersionProvider.CompareVersions("stable", "beta") > 0);
    }
}
