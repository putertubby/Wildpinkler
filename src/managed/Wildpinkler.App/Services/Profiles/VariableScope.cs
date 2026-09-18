using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Wildpinkler.App.Services;

/// <summary>
/// One entity's own isolated set of variable definitions plus recursive <c>${name}</c> expansion.
/// Scopes are never chained across entities: a game definition, a tool definition, a profile and a
/// per-profile tool override each build their own scope from only their own variables (plus the
/// read-only system folders every scope gets via <see cref="SystemVariables.AddTo"/>), used solely to
/// expand strings owned by that same entity before handing the result to the next stage as plain text.
/// Variable names are case-sensitive: <c>localappdata</c> and <c>LocalAppData</c> are distinct names,
/// so a local variable may use the case variant of a built-in name and shadows the system value
/// within this scope. Only a case-exact match with a built-in name is reserved (see
/// <see cref="SystemVariables.IsNameReserved"/>).
/// </summary>
public sealed class VariableScope
{
    private const int MaxExpansionDepth = 32;

    private static readonly Regex PlaceholderPattern =
        new(@"\$\{([A-Za-z][A-Za-z0-9_\-\.]*)\}", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _definitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnly = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Definitions => _definitions;

    /// <summary>Names that cannot be overridden and are preserved as literal ${name} placeholders when a resolved path is exported (see <see cref="Symbolize"/>).</summary>
    public IReadOnlyCollection<string> ReadOnlyNames => _readOnly;

    /// <summary>Adds or overrides a variable; read-only system variables cannot be overridden (a case-exact match is silently ignored as a backstop - validation should reject such names up front).</summary>
    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || _readOnly.Contains(name))
            return;

        _definitions[name] = value ?? string.Empty;
    }

    public void SetAll(IEnumerable<KeyValuePair<string, string>> variables)
    {
        foreach (var variable in variables)
            Set(variable.Key, variable.Value);
    }

    public void SetReadOnly(string name, string value)
    {
        _definitions[name] = value ?? string.Empty;
        _readOnly.Add(name);
    }

    /// <summary>Expands every placeholder in <paramref name="value"/>, recursively.</summary>
    public string Expand(string value) => Expand(value, new HashSet<string>(StringComparer.Ordinal), 0);

    /// <summary>Non-throwing form of <see cref="Expand(string)"/> for validation, where an unresolved placeholder is a user-facing error, not a bug.</summary>
    public bool TryExpand(string value, out string expanded, out string? error)
    {
        try
        {
            expanded = Expand(value);
            error = null;
            return true;
        }
        catch (InvalidOperationException exception)
        {
            expanded = string.Empty;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Fully resolves every definition, so a caller can hand the flat set to the exporter.</summary>
    public IReadOnlyDictionary<string, string> ResolveAll()
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in _definitions)
            resolved[name] = Expand(value);
        return resolved;
    }

    /// <summary>
    /// Given an already fully-resolved absolute path, re-introduces a ${name} placeholder for the
    /// longest read-only (built-in) value that is a full path-segment prefix of it - the inverse of
    /// expansion. A value only matches when the path equals it exactly or a directory separator
    /// follows it, so a path like <c>…\AppData\LocalLow</c> is never symbolized against the
    /// <c>…\AppData\Local</c> value. Built-in variables must never be baked into an exported merged
    /// view as literal text; only local/user variables are. <paramref name="resolved"/> is normally
    /// one of this scope's own <see cref="ResolveAll"/> values, so the consumer can always look the
    /// placeholder back up.
    /// </summary>
    public static string Symbolize(string resolvedPath, IReadOnlyDictionary<string, string> resolvedVariables, IReadOnlyCollection<string> readOnlyNames)
    {
        string? bestName = null;
        string? bestValue = null;
        foreach (var name in readOnlyNames)
        {
            if (!resolvedVariables.TryGetValue(name, out var value) || value.Length == 0)
                continue;

            bool matches = string.Equals(resolvedPath, value, StringComparison.OrdinalIgnoreCase)
                || resolvedPath.StartsWith(value + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (matches && (bestValue is null || value.Length > bestValue.Length))
            {
                bestName = name;
                bestValue = value;
            }
        }

        return bestName is null ? resolvedPath : $"${{{bestName}}}{resolvedPath[bestValue!.Length..]}";
    }

    private string Expand(string value, HashSet<string> visiting, int depth)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (depth > MaxExpansionDepth)
            throw new InvalidOperationException($"'{value}' nests variables more than {MaxExpansionDepth} levels deep.");

        return PlaceholderPattern.Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (!_definitions.TryGetValue(name, out var replacement))
                throw new InvalidOperationException($"'{name}' is not defined.");

            // uufs64 has no cycle protection of its own, so the cycle has to be caught here.
            if (!visiting.Add(name))
                throw new InvalidOperationException($"'{name}' refers to itself through a cycle.");

            try
            {
                return Expand(replacement, visiting, depth + 1);
            }
            finally
            {
                visiting.Remove(name);
            }
        });
    }
}

public static class SystemVariables
{
    /// <summary>
    /// Names <see cref="AddTo"/> registers as read-only, plus the per-profile read-only names every
    /// scope also gets (<c>InstallPath</c>, <c>ProfilePath</c>). Variable names are case-sensitive,
    /// so a case variant such as <c>localappdata</c> is a legal, distinct local variable; only a
    /// case-exact match is reserved. Validation should reject reserved names up front -
    /// <see cref="VariableScope.Set"/> silently ignores them as a backstop.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedNames = new[]
    {
        "Documents", "LocalAppData", "RoamingAppData", "Profile", "LocalAppDataLow"
    };

    /// <summary>The per-profile read-only names registered by callers in addition to <see cref="ReservedNames"/>.</summary>
    public static readonly IReadOnlyList<string> ProfileReservedNames = new[]
    {
        "InstallPath", "ProfilePath"
    };

    /// <summary>True when <paramref name="name"/> is case-exact equal to any built-in (read-only) variable name.</summary>
    public static bool IsNameReserved(string name)
    {
        foreach (var reserved in ReservedNames)
            if (string.Equals(name, reserved, StringComparison.Ordinal))
                return true;
        foreach (var reserved in ProfileReservedNames)
            if (string.Equals(name, reserved, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The read-only known-folder variables every profile and tool can reference.</summary>
    public static void AddTo(VariableScope scope)
    {
        scope.SetReadOnly("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        scope.SetReadOnly("LocalAppData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        scope.SetReadOnly("RoamingAppData", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        scope.SetReadOnly("Profile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        // FOLDERID_LocalAppDataLow has no SpecialFolder equivalent.
        scope.SetReadOnly("LocalAppDataLow",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"));
    }
}
