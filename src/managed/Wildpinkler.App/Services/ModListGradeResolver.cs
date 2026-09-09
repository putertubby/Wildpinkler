using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed record ModListGradeEvaluation(ModListGrade Grade, IReadOnlyList<string> Reasons);

public static class ModListGradeResolver
{
    public static ModListGradeEvaluation Evaluate(ModListManifest manifest)
    {
        var guided = new List<string>();
        var unavailable = new List<string>();

        foreach (var setting in manifest.Profile.ManualSettings)
            guided.Add($"Profile setting {setting.Name} must be configured manually.");

        foreach (var entry in manifest.Content)
        {
            switch (entry)
            {
                case ModListGuidedFolderEntry folder:
                    guided.Add($"{folder.Name} requires manual folder setup.");
                    break;
                case ModListModEntry mod when string.IsNullOrWhiteSpace(mod.Archive.Sha256):
                    unavailable.Add($"{mod.Name} has no verifiable archive.");
                    break;
                case ModListModEntry mod when mod.Source is null && string.IsNullOrWhiteSpace(mod.AcquisitionInstructions):
                    unavailable.Add($"{mod.Name} has no acquisition source or instructions.");
                    break;
                case ModListModEntry mod when mod.Source is null:
                    guided.Add($"{mod.Name} must be acquired manually.");
                    break;
                case ModListModEntry { Installation: GuidedInstallationRecipe } mod:
                    guided.Add($"{mod.Name} requires guided installation.");
                    break;
            }
        }

        foreach (var tool in manifest.Tools)
        {
            if (tool.DefinitionId is null && string.IsNullOrWhiteSpace(tool.AcquisitionInstructions))
                unavailable.Add($"{tool.Name} has no portable definition or setup instructions.");
            else if (tool.DefinitionId is null)
                guided.Add($"{tool.Name} must be configured manually.");
            else if (!string.IsNullOrWhiteSpace(tool.AcquisitionInstructions))
                guided.Add($"{tool.Name} has a manual prerequisite.");

        foreach (var invocation in tool.Invocations.Where(item => item.Mode == ModListToolInvocationMode.TrackedManual))
            guided.Add($"{tool.Name}: {invocation.Name} requires manual completion.");
        }

        return unavailable.Count > 0
            ? new ModListGradeEvaluation(ModListGrade.Unavailable, unavailable.Concat(guided).ToList())
            : guided.Count > 0
                ? new ModListGradeEvaluation(ModListGrade.Guided, guided)
                : new ModListGradeEvaluation(ModListGrade.Reproducible, new List<string>());
    }
}
