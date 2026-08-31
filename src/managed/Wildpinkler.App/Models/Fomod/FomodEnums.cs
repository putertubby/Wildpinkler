namespace Wildpinkler.App.Models.Fomod;

/// <summary>Author-declared ordering for install steps, groups or plugins.</summary>
public enum FomodOrder
{
    Explicit,
    Ascending,
    Descending
}

/// <summary>Cardinality rule for the plugins inside a <see cref="FomodGroup"/>.</summary>
public enum FomodGroupType
{
    SelectAny,
    SelectAtMostOne,
    SelectExactlyOne,
    SelectAtLeastOne,
    SelectAll
}

/// <summary>Static plugin desirability, used when a plugin declares a plain (non dependency-pattern) type.</summary>
public enum FomodPluginType
{
    Required,
    Recommended,
    Optional,
    NotUsable,
    CouldBeUsable
}

public enum FomodDependencyOperator
{
    And,
    Or
}

public enum FomodFileDependencyState
{
    Active,
    Inactive,
    Missing
}
