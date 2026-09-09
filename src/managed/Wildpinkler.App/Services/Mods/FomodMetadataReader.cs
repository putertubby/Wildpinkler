using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Wildpinkler.App.Services;

public sealed record FomodMetadata(
    string Name,
    string? Author,
    string Version,
    string? Website,
    string? Description,
    IReadOnlyList<string> Groups,
    string? GameDependency,
    IReadOnlyList<string> InstallSteps,
    FomodState State);

public sealed class FomodMetadataReader
{
    private readonly IArchiveInspector _archiveInspector;

    public FomodMetadataReader(IArchiveInspector archiveInspector) => _archiveInspector = archiveInspector;

    public Task<FomodMetadata> ReadAsync(string archivePath) => Task.Run(() => Read(archivePath));

    private FomodMetadata Read(string archivePath)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(archivePath);
        var state = _archiveInspector.DetectFomod(archivePath);
        if (state != FomodState.Yes)
            return new FomodMetadata(fallbackName, null, GuessVersion(fallbackName), null, null, Array.Empty<string>(), null, Array.Empty<string>(), state);

        var files = _archiveInspector.ReadFomodFiles(archivePath);
        var info = files.TryGetValue("info.xml", out var infoText) ? Parse(infoText) : null;
        var config = files.TryGetValue("ModuleConfig.xml", out var configText) ? Parse(configText) : null;

        var name = Value(info, "Name") ?? Attribute(Element(config, "moduleName"), "name") ?? Text(Element(config, "moduleName")) ?? fallbackName;
        var version = Value(info, "Version") ?? GuessVersion(fallbackName);

        return new FomodMetadata(
            name,
            Value(info, "Author"),
            version,
            Value(info, "Website"),
            Value(info, "Description"),
            Element(info, "Groups")?.Elements().Select(item => item.Value.Trim()).Where(item => item.Length > 0).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Attribute(Element(config, "gameDependency"), "version"),
            Element(config, "installSteps")?.Elements().Select(step => Attribute(step, "name") ?? string.Empty).Where(item => item.Length > 0).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            state);
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

    private static XElement? Element(XElement? root, string name) =>
        root?.Descendants().FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(XElement? root, string name) => Text(Element(root, name));

    private static string? Text(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Attribute(XElement? element, string name)
    {
        var value = element?.Attributes().FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Only guesses when the file name carries an unambiguous dotted version; otherwise the user fills it in.
    private static string GuessVersion(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"[-_ ]v?(\d+(?:[._]\d+){1,3})(?:[-_ ]|$)");
        return match.Success ? match.Groups[1].Value.Replace('_', '.') : string.Empty;
    }
}
