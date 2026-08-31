using System.Collections.Generic;

namespace Wildpinkler.App.Models;

public enum DefinitionSource
{
    BuiltIn,
    User
}

/// <summary>
/// The portable, machine-independent part shared by game and tool definitions. Local paths never
/// belong here; a definition contributes variables and merged views that a profile export resolves.
/// </summary>
public interface IDefinition
{
    int SchemaVersion { get; set; }
    string DefinitionId { get; set; }
    string Name { get; set; }
    int DefinitionVersion { get; set; }
    string Author { get; set; }
    string Description { get; set; }
    Dictionary<string, string> Variables { get; }
    List<MergedView> MergedViews { get; }
    DefinitionSource Source { get; set; }
    string? FilePath { get; set; }
    string DisplayName { get; }
    string SourceText { get; }
}
