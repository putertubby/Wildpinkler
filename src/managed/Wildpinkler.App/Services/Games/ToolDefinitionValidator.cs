using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>Tool-specific definition rules layered on top of <see cref="DefinitionValidation"/>.</summary>
public static class ToolDefinitionValidator
{
    public const int MaxSupportedGames = 64;
    public const int MaxTextLength = DefinitionValidation.MaxTextLength;
    public const int MaxMarkers = DefinitionValidation.MaxMarkers;

    public static bool TryValidate(ToolDefinition definition, out string error)
    {
        var scope = new VariableScope();
        SystemVariables.AddTo(scope);
        scope.SetAll(definition.Variables);

        if (!DefinitionValidation.TryValidateCommon(definition, ToolDefinition.CurrentSchemaVersion, scope, out error))
            return false;

        if (definition.DefaultLaunchArguments.Length > MaxTextLength)
        {
            error = "One of the text fields exceeds the maximum length.";
            return false;
        }

        if (definition.SupportedGameDefinitions.Count > MaxSupportedGames)
        {
            error = $"A tool can declare at most {MaxSupportedGames} supported games.";
            return false;
        }

        foreach (var gameDefinitionId in definition.SupportedGameDefinitions)
        {
            if (!DefinitionValidation.IsDefinitionId(gameDefinitionId))
            {
                error = $"'{gameDefinitionId}' is not a valid game definition id.";
                return false;
            }
        }

        if (!DefinitionValidation.IsSafeRelativePath(definition.ExecutableRelativePath))
        {
            error = "The executable must be a safe path relative to the tool's install folder.";
            return false;
        }

        if (definition.DetectionMarkers.Count > MaxMarkers)
        {
            error = $"A definition can declare at most {MaxMarkers} detection markers.";
            return false;
        }

        foreach (var marker in definition.DetectionMarkers)
        {
            if (!DefinitionValidation.IsSafeRelativePath(marker))
            {
                error = $"'{marker}' is not a safe relative path.";
                return false;
            }
        }

        // A tool's own variables and working directory follow the same relative-by-default convention
        // as a game definition's (relative to the game/tool folder per its launch kind, or absolute via
        // a read-only system folder variable), so leftover placeholders must resolve from this same
        // definition's own local scope, exactly like the merged views validated above.
        if (definition.WorkingDirectory.Length > 0)
        {
            if (!DefinitionValidation.IsBranchPath(definition.WorkingDirectory))
            {
                error = "The working directory is too long.";
                return false;
            }

            if (!scope.TryExpand(definition.WorkingDirectory, out _, out var workingDirectoryError))
            {
                error = $"Working directory: {workingDirectoryError}";
                return false;
            }
        }

        foreach (var variable in definition.Variables)
        {
            if (!DefinitionValidation.IsBranchPath(variable.Value))
            {
                error = $"'{variable.Key}' has an empty or too-long value.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
