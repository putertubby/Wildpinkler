using System;
using System.Collections.Generic;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>The three states a FOMOD fileDependency can require of a file (see XSD fileDependency).</summary>
public enum FomodFileState
{
    /// <summary>The file exists and is loaded (its mod folder is enabled in the load order).</summary>
    Active,

    /// <summary>The file exists in a known folder but that folder is disabled (installed, not loaded).</summary>
    Inactive,

    /// <summary>The file is not present in any known folder.</summary>
    Missing
}

/// <summary>Answers a FOMOD fileDependency check against the profile's current (pre-this-mod) merged content.</summary>
public interface IFomodFileStateProvider
{
    /// <summary>The file's state: loaded, installed-but-inactive, or missing.</summary>
    FomodFileState GetState(string relativePath);

    /// <summary>Convenience predicate for providers that only track presence.</summary>
    bool Exists(string relativePath) => GetState(relativePath) != FomodFileState.Missing;
}

/// <summary>A file-state provider that treats every path as missing - used when no profile context is available.</summary>
public sealed class NullFomodFileStateProvider : IFomodFileStateProvider
{
    public static readonly NullFomodFileStateProvider Instance = new();
    public FomodFileState GetState(string relativePath) => FomodFileState.Missing;
}

/// <summary>A parsed gameDependency/fommDependency version requirement (e.g. "<= 1.6.1130.0").</summary>
public sealed class FomodVersionSpec
{
    public FomodVersionOperator Operator { get; init; } = FomodVersionOperator.Equal;
    public required string RawValue { get; init; }
}

public enum FomodVersionOperator
{
    Equal,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual
}

/// <summary>Supplies the real game and mod-manager versions a FOMOD version gate is checked against.</summary>
public interface IFomodVersionProvider
{
    /// <summary>True when the required version is satisfied; a null spec is always satisfied.</summary>
    bool Satisfies(FomodVersionSpec? required, string subject);
}

/// <summary>
/// A version provider that knows nothing - every gate fails closed (unsatisfied). Used when no
/// game context is available; version-gated options then stay disabled, matching the
/// "no version registry" safe default.
/// </summary>
public sealed class NullFomodVersionProvider : IFomodVersionProvider
{
    public static readonly NullFomodVersionProvider Instance = new();
    public bool Satisfies(FomodVersionSpec? required, string subject) => required is null;
}

/// <summary>
/// Compares a FOMOD gameDependency version requirement against the real version of the profile's
/// game executable (read via <see cref="GameVersionInspector"/>). Versions compare numerically
/// component-wise when both sides parse as dotted numbers, and as ordinal strings otherwise.
/// A missing or unreadable game version fails the gate (fails closed).
/// </summary>
public sealed class FomodVersionProvider : IFomodVersionProvider
{
    private readonly string? _gameVersion;

    public FomodVersionProvider(string? gameExecutablePath)
    {
        _gameVersion = string.IsNullOrWhiteSpace(gameExecutablePath) ? null : GameVersionInspector.ReadVersion(gameExecutablePath);
    }

    public bool Satisfies(FomodVersionSpec? required, string subject)
    {
        if (required is null)
            return true;

        if (subject != "game")
            // fommDependency refers to the Bethesda mod managers (FO3Edit/FO4Edit), which this
            // app is not; there is no registry to check against, so the gate fails closed.
            return false;

        if (string.IsNullOrWhiteSpace(_gameVersion))
            return false;

        var comparison = CompareVersions(_gameVersion, required.RawValue);
        return required.Operator switch
        {
            FomodVersionOperator.Equal => comparison == 0,
            FomodVersionOperator.Greater => comparison > 0,
            FomodVersionOperator.GreaterOrEqual => comparison >= 0,
            FomodVersionOperator.Less => comparison < 0,
            FomodVersionOperator.LessOrEqual => comparison <= 0,
            _ => comparison == 0
        };
    }

    /// <summary>Component-wise numeric compare for dotted versions; ordinal string compare otherwise.</summary>
    internal static int CompareVersions(string left, string right)
    {
        if (System.Version.TryParse(left, out var lv) && System.Version.TryParse(right, out var rv))
            return lv.CompareTo(rv);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Evaluates a parsed <see cref="FomodDependency"/> tree against the current flag set, file state, and version context.</summary>
public sealed class FomodDependencyResolver
{
    public bool Evaluate(FomodDependency? dependency, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files, IFomodVersionProvider? versions = null)
    {
        versions ??= NullFomodVersionProvider.Instance;
        switch (dependency)
        {
            case null:
                return true;

            case FomodCompositeDependency composite:
                if (composite.Children.Count == 0)
                    return true;
                return composite.Operator == FomodDependencyOperator.And
                    ? AllMatch(composite.Children, flags, files, versions)
                    : AnyMatch(composite.Children, flags, files, versions);

            case FomodFlagDependency flag:
                return flags.TryGetValue(flag.Flag, out var value) && value == flag.Value;

            case FomodFileDependency file:
                // The three states map one-to-one onto the provider's tristate:
                // Active requires the file to be loaded; Inactive requires it installed-but-not-loaded;
                // Missing requires it absent.
                var state = files.GetState(file.File);
                return file.State switch
                {
                    FomodFileDependencyState.Active => state == FomodFileState.Active,
                    FomodFileDependencyState.Inactive => state == FomodFileState.Inactive,
                    FomodFileDependencyState.Missing => state == FomodFileState.Missing,
                    _ => true
                };

            case FomodGameDependency game:
                return versions.Satisfies(game.VersionSpec, "game");

            case FomodFommDependency fomm:
                return versions.Satisfies(fomm.VersionSpec, "fomm");

            case FomodUnsupportedDependency:
            default:
                return true;
        }
    }

    private bool AllMatch(IReadOnlyList<FomodDependency> children, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files, IFomodVersionProvider versions)
    {
        foreach (var child in children)
        {
            if (!Evaluate(child, flags, files, versions))
                return false;
        }

        return true;
    }

    private bool AnyMatch(IReadOnlyList<FomodDependency> children, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files, IFomodVersionProvider versions)
    {
        foreach (var child in children)
        {
            if (Evaluate(child, flags, files, versions))
                return true;
        }

        return children.Count == 0;
    }
}
