using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>
/// Parses a FOMOD's full ModuleConfig.xml into a <see cref="FomodModule"/>. Untrusted-XML hardening
/// matches <see cref="FomodMetadataReader"/>. Any dependency node the parser cannot understand
/// degrades to an always-true leaf plus a collected warning, rather than failing the whole parse.
/// </summary>
public sealed class FomodInstallerParser
{
    public FomodModule? TryParse(string moduleConfigXml)
    {
        var root = Parse(moduleConfigXml);
        // A ModuleConfig.xml always has a <config> root; anything else is not a FOMOD installer.
        return root is null || !root.Name.LocalName.Equals("config", StringComparison.OrdinalIgnoreCase)
            ? null
            : ParseModule(root);
    }

    private static XElement? Parse(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private FomodModule ParseModule(XElement root)
    {
        var module = new FomodModule
        {
            Name = Child(root, "moduleName")?.Value.Trim() ?? string.Empty,
            ModuleImage = Attr(Child(root, "moduleImage"), "path")
        };

        if (Child(root, "moduleDependencies") is { } moduleDependencies)
            module.ModuleDependency = ParseDependencyContainer(moduleDependencies, module.Warnings);

        if (Child(root, "requiredInstallFiles") is { } required)
            module.RequiredInstallFiles = ParseFileInstalls(required);

        if (Child(root, "installSteps") is { } installSteps)
        {
            var order = ParseOrder(Attr(installSteps, "order"));
            module.InstallSteps = OrderBy(
                installSteps.Elements().Where(IsNamed("installStep")).Select(step => ParseStep(step, module.Warnings)).ToList(),
                order, step => step.Name);
        }

        if (Child(root, "conditionalFileInstalls") is { } conditional && Child(conditional, "patterns") is { } patterns)
        {
            module.ConditionalFileInstalls = patterns.Elements().Where(IsNamed("pattern"))
                .Select(pattern => ParseConditionalPattern(pattern, module.Warnings)).ToList();
        }

        return module;
    }

    private FomodInstallStep ParseStep(XElement stepElement, List<string> warnings)
    {
        var step = new FomodInstallStep { Name = Attr(stepElement, "name") ?? string.Empty };

        if (Child(stepElement, "visible") is { } visible)
            step.VisibilityDependency = ParseDependencyContainer(visible, warnings);

        if (Child(stepElement, "optionalFileGroups") is { } groups)
        {
            var order = ParseOrder(Attr(groups, "order"));
            step.Groups = OrderBy(
                groups.Elements().Where(IsNamed("group")).Select(group => ParseGroup(group, warnings)).ToList(),
                order, group => group.Name);
        }

        return step;
    }

    private FomodGroup ParseGroup(XElement groupElement, List<string> warnings)
    {
        var group = new FomodGroup
        {
            Name = Attr(groupElement, "name") ?? string.Empty,
            Type = ParseGroupType(Attr(groupElement, "type"))
        };

        if (Child(groupElement, "plugins") is { } plugins)
        {
            var order = ParseOrder(Attr(plugins, "order"));
            group.Plugins = OrderBy(
                plugins.Elements().Where(IsNamed("plugin")).Select(plugin => ParsePlugin(plugin, warnings)).ToList(),
                order, plugin => plugin.Name);
        }

        return group;
    }

    private FomodPlugin ParsePlugin(XElement pluginElement, List<string> warnings)
    {
        var plugin = new FomodPlugin
        {
            Name = Attr(pluginElement, "name") ?? string.Empty,
            Description = Child(pluginElement, "description")?.Value.Trim(),
            ImagePath = Attr(Child(pluginElement, "image"), "path")
        };

        if (Child(pluginElement, "files") is { } files)
            plugin.Files = ParseFileInstalls(files);

        if (Child(pluginElement, "conditionFlags") is { } flags)
        {
            foreach (var flag in flags.Elements().Where(IsNamed("flag")))
            {
                var name = Attr(flag, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    plugin.ConditionFlags[name] = flag.Value.Trim();
            }
        }

        if (Child(pluginElement, "typeDescriptor") is { } typeDescriptor)
            ParseTypeDescriptor(typeDescriptor, plugin, warnings);

        return plugin;
    }

    private void ParseTypeDescriptor(XElement typeDescriptor, FomodPlugin plugin, List<string> warnings)
    {
        if (Child(typeDescriptor, "type") is { } staticType)
        {
            plugin.StaticType = ParsePluginType(Attr(staticType, "name"));
            return;
        }

        if (Child(typeDescriptor, "dependencyType") is not { } dependencyType)
        {
            warnings.Add($"Plugin '{plugin.Name}' has no recognizable typeDescriptor; treated as Optional.");
            plugin.StaticType = FomodPluginType.Optional;
            return;
        }

        plugin.DependencyDefaultType = ParsePluginType(Attr(Child(dependencyType, "defaultType"), "name"));
        if (Child(dependencyType, "patterns") is not { } patterns)
            return;

        foreach (var pattern in patterns.Elements().Where(IsNamed("pattern")))
        {
            var dependency = Child(pattern, "dependencies") is { } dependencyElement
                ? ParseDependencyContainer(dependencyElement, warnings)
                : ParseDependencyContainer(pattern, warnings);
            var type = ParsePluginType(Attr(Child(pattern, "type"), "name"));
            plugin.DependencyPatterns.Add(new FomodTypePattern { Dependency = dependency, Type = type });
        }
    }

    private FomodConditionalPattern ParseConditionalPattern(XElement patternElement, List<string> warnings)
    {
        var dependency = Child(patternElement, "dependencies") is { } dependencyElement
            ? ParseDependencyContainer(dependencyElement, warnings)
            : ParseDependencyContainer(patternElement, warnings);

        var files = Child(patternElement, "files") is { } filesElement ? ParseFileInstalls(filesElement) : new List<FomodFileInstall>();
        return new FomodConditionalPattern { Dependency = dependency, Files = files };
    }

    /// <summary>
    /// Builds a composite dependency from a container element's direct dependency-leaf/nested-"dependencies"
    /// children. Handles both the common "wrapped in a &lt;dependencies&gt;" shape and containers whose
    /// dependency leaves are direct children (e.g. some authoring tools emit &lt;visible&gt; leaves directly).
    /// </summary>
    private FomodCompositeDependency ParseDependencyContainer(XElement container, List<string> warnings)
    {
        var composite = new FomodCompositeDependency { Operator = ParseOperator(Attr(container, "operator")) };
        foreach (var child in container.Elements())
            composite.Children.Add(ParseDependencyLeaf(child, warnings));
        return composite;
    }

    private FomodDependency ParseDependencyLeaf(XElement element, List<string> warnings)
    {
        var name = element.Name.LocalName;
        switch (name)
        {
            case "dependencies":
                return ParseDependencyContainer(element, warnings);
            case "fileDependency":
                return new FomodFileDependency
                {
                    File = Attr(element, "file") ?? string.Empty,
                    State = ParseFileState(Attr(element, "state"))
                };
            case "flagDependency":
                return new FomodFlagDependency
                {
                    Flag = Attr(element, "flag") ?? string.Empty,
                    Value = Attr(element, "value") ?? string.Empty
                };
            case "gameDependency":
                return new FomodGameDependency { Version = Attr(element, "version") ?? string.Empty };
            case "fommDependency":
                return new FomodFommDependency { Version = Attr(element, "version") ?? string.Empty };
            default:
                warnings.Add($"Unsupported dependency node '{name}' was ignored (treated as always satisfied).");
                return new FomodUnsupportedDependency { Reason = name };
        }
    }

    private List<FomodFileInstall> ParseFileInstalls(XElement container)
    {
        var result = new List<FomodFileInstall>();
        foreach (var element in container.Elements())
        {
            if (!IsNamed("file")(element) && !IsNamed("folder")(element))
                continue;

            result.Add(new FomodFileInstall
            {
                Source = Attr(element, "source") ?? string.Empty,
                Destination = Attr(element, "destination") ?? Attr(element, "source") ?? string.Empty,
                Priority = int.TryParse(Attr(element, "priority"), out var priority) ? priority : 0,
                IsFolder = IsNamed("folder")(element)
            });
        }

        return result;
    }

    private static List<T> OrderBy<T>(List<T> items, FomodOrder order, Func<T, string> nameSelector) => order switch
    {
        FomodOrder.Ascending => items.OrderBy(nameSelector, StringComparer.OrdinalIgnoreCase).ToList(),
        FomodOrder.Descending => items.OrderByDescending(nameSelector, StringComparer.OrdinalIgnoreCase).ToList(),
        _ => items
    };

    private static FomodOrder ParseOrder(string? value) => value?.ToLowerInvariant() switch
    {
        "ascending" => FomodOrder.Ascending,
        "descending" => FomodOrder.Descending,
        _ => FomodOrder.Explicit
    };

    private static FomodGroupType ParseGroupType(string? value) => value switch
    {
        "SelectAtMostOne" => FomodGroupType.SelectAtMostOne,
        "SelectExactlyOne" => FomodGroupType.SelectExactlyOne,
        "SelectAtLeastOne" => FomodGroupType.SelectAtLeastOne,
        "SelectAll" => FomodGroupType.SelectAll,
        _ => FomodGroupType.SelectAny
    };

    private static FomodPluginType ParsePluginType(string? value) => value switch
    {
        "Required" => FomodPluginType.Required,
        "Recommended" => FomodPluginType.Recommended,
        "NotUsable" => FomodPluginType.NotUsable,
        "CouldBeUsable" => FomodPluginType.CouldBeUsable,
        _ => FomodPluginType.Optional
    };

    private static FomodDependencyOperator ParseOperator(string? value) =>
        string.Equals(value, "Or", StringComparison.OrdinalIgnoreCase) ? FomodDependencyOperator.Or : FomodDependencyOperator.And;

    private static FomodFileDependencyState ParseFileState(string? value) => value switch
    {
        "Inactive" => FomodFileDependencyState.Inactive,
        "Missing" => FomodFileDependencyState.Missing,
        _ => FomodFileDependencyState.Active
    };

    private static Func<XElement, bool> IsNamed(string name) =>
        element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement? root, string name) =>
        root?.Elements().FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Attr(XElement? element, string name)
    {
        var value = element?.Attributes().FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
