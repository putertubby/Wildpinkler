using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Rules shared by every definition kind. Definition files are untrusted input: relative-path fields
/// must stay inside the install folder and the id must be safe to use as a file name.
/// </summary>
public static class DefinitionValidation
{
    public const long MaxFileBytes = 256 * 1024;
    public const int MaxVariables = 128;
    public const int MaxMarkers = 64;
    public const int MaxMergedViews = 64;
    public const int MaxBranchesPerView = 16;
    public const int MaxTextLength = 512;

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex VariablePattern = new("^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.Compiled);

    /// <summary>Validates everything a game and a tool definition have in common.</summary>
    public static bool TryValidateCommon(IDefinition definition, int currentSchemaVersion, out string error) =>
        TryValidateCommon(definition, currentSchemaVersion, localScope: null, out error);

    /// <summary>
    /// Same as the parameterless overload, but when <paramref name="localScope"/> is supplied (the
    /// definition's own variables + read-only system folders), every merged view's mount path and
    /// branches must also fully expand through it - an unresolved placeholder is rejected here rather
    /// than deferred, since a definition can only ever see its own local scope.
    /// </summary>
    public static bool TryValidateCommon(IDefinition definition, int currentSchemaVersion, VariableScope? localScope, out string error)
    {
        if (definition.SchemaVersion is < 1 || definition.SchemaVersion > currentSchemaVersion)
        {
            error = $"Unsupported schema version {definition.SchemaVersion}.";
            return false;
        }

        if (!IsDefinitionId(definition.DefinitionId))
        {
            error = "The definition id must be 1-64 characters of a-z, 0-9, '.', '_' or '-' and must not contain '..'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > MaxTextLength)
        {
            error = "The definition name is missing or too long.";
            return false;
        }

        if (definition.DefinitionVersion < 1)
        {
            error = "The definition version must be 1 or greater.";
            return false;
        }

        if (definition.Author.Length > MaxTextLength || definition.Description.Length > MaxTextLength)
        {
            error = "One of the text fields exceeds the maximum length.";
            return false;
        }

        if (definition.Variables.Count > MaxVariables)
        {
            error = $"A definition can declare at most {MaxVariables} variables.";
            return false;
        }

        if (definition.MergedViews.Count > MaxMergedViews)
        {
            error = $"A definition can declare at most {MaxMergedViews} merged views.";
            return false;
        }

        foreach (var variable in definition.Variables)
        {
            if (!IsVariableName(variable.Key))
            {
                error = $"'{variable.Key}' is not a valid variable name (letters, 0-9 and '_', starting with a letter).";
                return false;
            }
        }

        return TryValidateMergedViews(definition.MergedViews, localScope, out error);
    }

    public static bool TryValidateMergedViews(IReadOnlyList<MergedView> views, out string error) =>
        TryValidateMergedViews(views, localScope: null, out error);

    public static bool TryValidateMergedViews(IReadOnlyList<MergedView> views, VariableScope? localScope, out string error)
    {
        var mountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            var label = view.Name.Length == 0 ? "(unnamed)" : view.Name;

            if (view.Name.Length > MaxTextLength)
            {
                error = $"'{label}' has a name that is too long.";
                return false;
            }

            if (!IsBranchPath(view.MountPath))
            {
                error = $"'{label}' has an empty or too-long mount path.";
                return false;
            }

            if (view.Branches.Count is < 1 or > MaxBranchesPerView)
            {
                error = $"'{label}' must declare between 1 and {MaxBranchesPerView} branches.";
                return false;
            }

            foreach (var branch in view.Branches)
            {
                if (!IsBranchPath(branch))
                {
                    error = $"'{label}' has an empty or too-long branch path.";
                    return false;
                }
            }

            var mountKey = view.MountPath.Trim();
            if (!mountKeys.Add(mountKey))
            {
                error = $"'{label}' declares the same mount path as another view in this list.";
                return false;
            }

            if (localScope is not null)
            {
                if (!localScope.TryExpand(view.MountPath, out _, out var mountError))
                {
                    error = $"'{label}': {mountError}";
                    return false;
                }

                foreach (var branch in view.Branches)
                {
                    if (!localScope.TryExpand(branch, out _, out var branchError))
                    {
                        error = $"'{label}': {branchError}";
                        return false;
                    }
                }
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool IsDefinitionId(string? id) =>
        IdPattern.IsMatch(id ?? string.Empty) && !id!.Contains("..");

    public static bool IsVariableName(string? name) => VariablePattern.IsMatch(name ?? string.Empty);

    /// <summary>
    /// A merged-view mount path or branch: relative to the owning entity's install/tool folder by
    /// default, or absolute when built from a read-only system folder variable. Only emptiness and
    /// length are checked here; full local-scope expansion is done separately (see the
    /// <see cref="VariableScope"/> overloads above), since a raw definition may still contain
    /// placeholders when only length/presence needs checking (e.g. a quick UI-side pre-check).
    /// </summary>
    public static bool IsBranchPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path!.Length <= MaxTextLength;

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxTextLength)
            return false;

        if (path.Contains(':') || path.StartsWith('\\') || path.StartsWith('/'))
            return false;

        if (Path.IsPathRooted(path) || Path.IsPathFullyQualified(path))
            return false;

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;

        var segments = path.Split('\\', '/');
        return segments.All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    /// <summary>
    /// Resolves a definition-supplied relative path against an install folder, re-checking containment
    /// because the relative path alone cannot prove where it lands.
    /// </summary>
    public static bool TryResolveUnder(string installPath, string relativePath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(installPath) || !IsSafeRelativePath(relativePath))
            return false;

        string root;
        string candidate;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
            candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        resolvedPath = candidate;
        return true;
    }
}
