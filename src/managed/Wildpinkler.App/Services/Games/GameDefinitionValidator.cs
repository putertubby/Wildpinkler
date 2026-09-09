using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>Game-specific definition rules layered on top of <see cref="DefinitionValidation"/>.</summary>
public static class GameDefinitionValidator
{
    public const long MaxFileBytes = DefinitionValidation.MaxFileBytes;
    public const int MaxVariables = DefinitionValidation.MaxVariables;
    public const int MaxMarkers = DefinitionValidation.MaxMarkers;
    public const int MaxMergedViews = DefinitionValidation.MaxMergedViews;
    public const int MaxBranchesPerView = DefinitionValidation.MaxBranchesPerView;
    public const int MaxTextLength = DefinitionValidation.MaxTextLength;

    public static bool TryValidate(GameDefinition definition, out string error)
    {
        var scope = new VariableScope();
        SystemVariables.AddTo(scope);
        scope.SetAll(definition.Variables);

        if (!DefinitionValidation.TryValidateCommon(definition, GameDefinition.CurrentSchemaVersion, scope, out error))
            return false;

        if (definition.DefaultLaunchArguments.Length > MaxTextLength)
        {
            error = "One of the text fields exceeds the maximum length.";
            return false;
        }

        if (definition.SteamAppId.Length > 0 && !definition.SteamAppId.All(char.IsAsciiDigit))
        {
            error = "The steam id must contain digits only.";
            return false;
        }

        if (definition.DetectionMarkers.Count > MaxMarkers)
        {
            error = $"A definition can declare at most {MaxMarkers} detection markers.";
            return false;
        }

        var paths = new List<string>(definition.DetectionMarkers);
        if (definition.ExecutableRelativePath.Length > 0)
            paths.Add(definition.ExecutableRelativePath);
        paths.AddRange(definition.Variables.Values);

        foreach (var path in paths)
        {
            if (!DefinitionValidation.IsSafeRelativePath(path))
            {
                error = $"'{path}' is not a safe relative path.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool IsVariableName(string name) => DefinitionValidation.IsVariableName(name);

    public static bool IsSafeRelativePath(string path) => DefinitionValidation.IsSafeRelativePath(path);

    public static bool TryResolveUnder(string installPath, string relativePath, out string resolvedPath) =>
        DefinitionValidation.TryResolveUnder(installPath, relativePath, out resolvedPath);
}
