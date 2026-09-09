using System.Collections.Generic;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>Answers a FOMOD fileDependency check against the profile's current (pre-this-mod) merged content.</summary>
public interface IFomodFileStateProvider
{
    bool Exists(string relativePath);
}

/// <summary>A file-state provider that treats every path as missing - used when no profile context is available.</summary>
public sealed class NullFomodFileStateProvider : IFomodFileStateProvider
{
    public static readonly NullFomodFileStateProvider Instance = new();
    public bool Exists(string relativePath) => false;
}

/// <summary>Evaluates a parsed <see cref="FomodDependency"/> tree against the current flag set and file state.</summary>
public sealed class FomodDependencyResolver
{
    public bool Evaluate(FomodDependency? dependency, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files)
    {
        switch (dependency)
        {
            case null:
                return true;

            case FomodCompositeDependency composite:
                if (composite.Children.Count == 0)
                    return true;
                return composite.Operator == FomodDependencyOperator.And
                    ? AllMatch(composite.Children, flags, files)
                    : AnyMatch(composite.Children, flags, files);

            case FomodFlagDependency flag:
                return flags.TryGetValue(flag.Flag, out var value) && value == flag.Value;

            case FomodFileDependency file:
                var exists = files.Exists(file.File);
                return file.State switch
                {
                    FomodFileDependencyState.Active => exists,
                    FomodFileDependencyState.Missing => !exists,
                    FomodFileDependencyState.Inactive => !exists,
                    _ => true
                };

            // Game/Fomm version gates and unparsable nodes are never blocking - no version registry exists yet.
            case FomodGameDependency:
            case FomodFommDependency:
            case FomodUnsupportedDependency:
            default:
                return true;
        }
    }

    private bool AllMatch(IReadOnlyList<FomodDependency> children, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files)
    {
        foreach (var child in children)
        {
            if (!Evaluate(child, flags, files))
                return false;
        }

        return true;
    }

    private bool AnyMatch(IReadOnlyList<FomodDependency> children, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files)
    {
        foreach (var child in children)
        {
            if (Evaluate(child, flags, files))
                return true;
        }

        return children.Count == 0;
    }
}
