using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public static partial class ModListManifestValidator
{
    public const int MaxContentEntries = 4096;
    public const int MaxTools = 128;

    public static IReadOnlyList<string> Validate(ModListManifest manifest)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion != ModListManifest.CurrentSchemaVersion)
            errors.Add($"Schema version {manifest.SchemaVersion} is not supported; expected {ModListManifest.CurrentSchemaVersion}.");
        if (!DefinitionValidation.IsDefinitionId(manifest.ListId))
            errors.Add("List id must be a safe portable identifier.");
        if (manifest.Revision < 1)
            errors.Add("Revision must be 1 or greater.");
        ValidateText(manifest.Name, "List name", required: true, errors);
        ValidateText(manifest.Author, "Author", required: false, errors);
        ValidateText(manifest.Description, "Description", required: false, errors);

        if (!DefinitionValidation.IsDefinitionId(manifest.Game.DefinitionId))
            errors.Add("Game definition id is invalid.");
        if (manifest.Game.MinimumDefinitionVersion < 1)
            errors.Add("Game definition version must be 1 or greater.");
        ValidateVersionRequirement(manifest.Game.ExecutableVersion, errors);
        ValidateProfile(manifest.Profile, errors);

        if (manifest.Content.Count > MaxContentEntries)
            errors.Add($"A mod list can contain at most {MaxContentEntries} content entries.");
        var entryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orders = new HashSet<int>();
        foreach (var entry in manifest.Content)
        {
            if (!DefinitionValidation.IsDefinitionId(entry.EntryId) || !entryIds.Add(entry.EntryId))
                errors.Add($"Content entry id '{entry.EntryId}' is invalid or duplicated.");
            if (entry.Order < 1 || !orders.Add(entry.Order))
                errors.Add($"Content order {entry.Order} is invalid or duplicated.");
            ValidateText(entry.Name, $"Content entry '{entry.EntryId}' name", required: true, errors);

            switch (entry)
            {
                case ModListModEntry mod:
                    ValidateMod(mod, errors);
                    break;
                case ModListGuidedFolderEntry guided:
                    ValidateText(guided.Instructions, $"Guided folder '{guided.EntryId}' instructions", required: true, errors);
                    break;
                default:
                    errors.Add($"Content entry '{entry.EntryId}' has an unsupported kind.");
                    break;
            }
        }

        if (orders.Count > 0 && !orders.Order().SequenceEqual(Enumerable.Range(1, orders.Count)))
            errors.Add("Content order must be contiguous and start at 1.");

        if (manifest.Tools.Count > MaxTools)
            errors.Add($"A mod list can require at most {MaxTools} tools.");
        var toolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in manifest.Tools)
            ValidateTool(tool, toolIds, errors);

        return errors;
    }

    public static void EnsureValid(ModListManifest manifest)
    {
        var errors = Validate(manifest);
        if (errors.Count > 0)
            throw new ModListValidationException(errors);
    }

    private static void ValidateMod(ModListModEntry mod, List<string> errors)
    {
        if (mod.Source is not null)
        {
            if (string.IsNullOrWhiteSpace(mod.Source.SiteId) || string.IsNullOrWhiteSpace(mod.Source.GameKey) ||
                string.IsNullOrWhiteSpace(mod.Source.ModKey) || string.IsNullOrWhiteSpace(mod.Source.FileKey))
                errors.Add($"Mod '{mod.EntryId}' remote source must identify an exact file.");
            if (mod.Source.PageUrl is { Length: > 0 } pageUrl && !IsStableHttpUrl(pageUrl))
                errors.Add($"Mod '{mod.EntryId}' page URL must be HTTP(S) without credentials, query, or fragment.");
        }
            ValidateText(mod.AcquisitionInstructions, $"Mod '{mod.EntryId}' acquisition instructions", required: false, errors);

        if (Path.GetFileName(mod.Archive.FileName) != mod.Archive.FileName || string.IsNullOrWhiteSpace(mod.Archive.FileName))
            errors.Add($"Mod '{mod.EntryId}' archive file name is invalid.");
        if (!IsHash(mod.Archive.Sha256, 64) && (mod.Source is not null || !string.IsNullOrWhiteSpace(mod.AcquisitionInstructions)))
            errors.Add($"Mod '{mod.EntryId}' archive SHA-256 is invalid.");
        if (mod.Archive.Md5 is { Length: > 0 } md5 && !IsHash(md5, 32))
            errors.Add($"Mod '{mod.EntryId}' archive MD5 is invalid.");
        if (mod.Archive.SizeInBytes is < 0)
            errors.Add($"Mod '{mod.EntryId}' archive size cannot be negative.");
        if (mod.LauncherExecutableRelativePath is { Length: > 0 } launcher && !DefinitionValidation.IsSafeRelativePath(launcher))
            errors.Add($"Mod '{mod.EntryId}' launcher path is unsafe.");

        ValidateRecipe(mod.EntryId, mod.Installation, errors);
    }

    private static void ValidateRecipe(string entryId, ModInstallationRecipe recipe, List<string> errors)
    {
        switch (recipe)
        {
            case FomodInstallationRecipe fomod:
                if (!IsHash(fomod.ModuleConfigSha256, 64))
                    errors.Add($"Mod '{entryId}' FOMOD configuration SHA-256 is invalid.");
                foreach (var choice in fomod.Selections)
                {
                    if (string.IsNullOrWhiteSpace(choice.Step) || string.IsNullOrWhiteSpace(choice.Group) ||
                        choice.Plugins.Count == 0 || choice.Plugins.Any(string.IsNullOrWhiteSpace))
                        errors.Add($"Mod '{entryId}' contains an incomplete FOMOD selection.");
                }
                break;
            case ManualInstallationRecipe manual:
                if (!IsEmptyOrSafeRelativePath(manual.SourceRoot) || !IsEmptyOrSafeRelativePath(manual.Destination))
                    errors.Add($"Mod '{entryId}' manual installation contains an unsafe path.");
                break;
            case GuidedInstallationRecipe guided:
                ValidateText(guided.Instructions, $"Mod '{entryId}' guided installation instructions", required: true, errors);
                break;
            default:
                errors.Add($"Mod '{entryId}' has an unsupported installation recipe.");
                break;
        }
    }

    private static void ValidateTool(ModListToolRequirement tool, HashSet<string> toolIds, List<string> errors)
    {
        if (!DefinitionValidation.IsDefinitionId(tool.RequirementId) || !toolIds.Add(tool.RequirementId))
            errors.Add($"Tool requirement id '{tool.RequirementId}' is invalid or duplicated.");
        ValidateText(tool.Name, $"Tool '{tool.RequirementId}' name", required: true, errors);
        ValidateText(tool.AcquisitionInstructions, $"Tool '{tool.RequirementId}' acquisition instructions", required: false, errors);
        if (tool.DefinitionId is not null && !DefinitionValidation.IsDefinitionId(tool.DefinitionId))
            errors.Add($"Tool '{tool.RequirementId}' definition id is invalid.");
        if (tool.MinimumDefinitionVersion < 1)
            errors.Add($"Tool '{tool.RequirementId}' definition version must be 1 or greater.");
        if (tool.LaunchArgumentsOverride.Length > 4096)
            errors.Add($"Tool '{tool.RequirementId}' launch arguments are too long.");
        ValidateVariables(tool.VariableOverrides, $"Tool '{tool.RequirementId}'", errors);
        ValidateMergedViews(tool.MergedViewOverrides, $"Tool '{tool.RequirementId}'", errors);
        if (tool.DefinitionId is null && tool.Invocations.Count > 0)
            errors.Add($"Custom tool '{tool.RequirementId}' cannot declare invocations without a registered definition.");

        var invocationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var invocation in tool.Invocations)
        {
            if (!DefinitionValidation.IsDefinitionId(invocation.InvocationId) || !invocationIds.Add(invocation.InvocationId))
                errors.Add($"Tool '{tool.RequirementId}' invocation id '{invocation.InvocationId}' is invalid or duplicated.");
            ValidateText(invocation.Name, $"Tool invocation '{invocation.InvocationId}' name", required: true, errors);
            if (invocation.Mode == ModListToolInvocationMode.TrackedManual)
                ValidateText(invocation.Instructions, $"Tool invocation '{invocation.InvocationId}' instructions", required: true, errors);
        }
    }

    private static void ValidateProfile(ModListProfileTemplate profile, List<string> errors)
    {
        ValidateVariables(profile.Variables, "Profile", errors);
        ValidateMergedViews(profile.MergedViews, "Profile", errors);
        foreach (var setting in profile.ManualSettings)
        {
            ValidateText(setting.Name, "Manual profile setting name", required: true, errors);
            ValidateText(setting.Instructions, $"Manual profile setting '{setting.Name}' instructions", required: true, errors);
        }
    }

    private static void ValidateVariables(IReadOnlyDictionary<string, string> variables, string owner, List<string> errors)
    {
        if (variables.Count > DefinitionValidation.MaxVariables)
            errors.Add($"{owner} has too many variables.");
        foreach (var variable in variables)
        {
            if (!DefinitionValidation.IsVariableName(variable.Key))
                errors.Add($"{owner} variable '{variable.Key}' has an invalid name.");
            if (!IsPortableValue(variable.Value))
                errors.Add($"{owner} variable '{variable.Key}' contains a machine-local absolute path.");
        }
    }

    private static void ValidateMergedViews(IReadOnlyList<MergedView> views, string owner, List<string> errors)
    {
        if (!DefinitionValidation.TryValidateMergedViews(views, out var error))
            errors.Add($"{owner} merged views are invalid: {error}");
        foreach (var path in views.SelectMany(view => view.Branches.Prepend(view.MountPath)))
        {
            if (!IsPortableValue(path))
                errors.Add($"{owner} merged views contain a machine-local absolute path.");
        }
    }

    private static void ValidateVersionRequirement(ModListVersionRequirement? requirement, List<string> errors)
    {
        if (requirement is null)
            return;
        if (requirement.ExactVersions.Any(string.IsNullOrWhiteSpace) ||
            (requirement.ExactVersions.Count > 0 && (requirement.Minimum is not null || requirement.Maximum is not null)))
            errors.Add("Game executable version requirement is inconsistent.");
    }

    private static void ValidateText(string? value, string label, bool required, List<string> errors)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > DefinitionValidation.MaxTextLength)
            errors.Add($"{label} is missing or too long.");
    }

    private static bool IsEmptyOrSafeRelativePath(string path) => path.Length == 0 || DefinitionValidation.IsSafeRelativePath(path);

    public static bool IsPortableValue(string value) =>
        value.Length <= DefinitionValidation.MaxTextLength && !Path.IsPathFullyQualified(value) &&
        !value.StartsWith('\\') && !value.StartsWith('/');

    private static bool IsStableHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsHash(string? value, int length) =>
        value?.Length == length && HexPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]+$", RegexOptions.IgnoreCase)]
    private static partial Regex HexPattern();
}

public sealed class ModListValidationException : Exception
{
    public ModListValidationException(IReadOnlyList<string> errors)
        : base($"The mod list is invalid: {string.Join(" ", errors)}") => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}
