using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class GameDefinitionStore : DefinitionStore<GameDefinition>
{
    protected override string ShortExtension => ".wpgame";

    protected override string BuiltInDirectoryName => "GameDefinitions";

    protected override string UserDirectoryName => "game-definitions";

    protected override bool Validate(GameDefinition definition, out string errorMessage) =>
        GameDefinitionValidator.TryValidate(definition, out errorMessage);
}
