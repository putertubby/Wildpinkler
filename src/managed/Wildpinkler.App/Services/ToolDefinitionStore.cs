using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class ToolDefinitionStore : DefinitionStore<ToolDefinition>
{
    protected override string ShortExtension => ".wptool";

    protected override string BuiltInDirectoryName => "ToolDefinitions";

    protected override string UserDirectoryName => "tool-definitions";

    protected override bool Validate(ToolDefinition definition, out string error) =>
        ToolDefinitionValidator.TryValidate(definition, out error);
}
