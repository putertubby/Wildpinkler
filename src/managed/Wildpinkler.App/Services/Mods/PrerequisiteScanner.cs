using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services.Mods;

/// <summary>
/// Extracts the non-Nexus prerequisites a mod declares in its description. The Nexus GraphQL
/// API has no "prerequisites" field - the website's Prerequisites panel is derived from the
/// description text - so this scans for the conventional "Prerequisites" heading and the
/// bullets under it. Each bullet becomes a name-only
/// <see cref="RemoteRequirementKind.Prerequisite"/>; these are chips only and never plan rows.
/// </summary>
public static class PrerequisiteScanner
{
    private static readonly Regex HeadingWord = new(@"prerequisites\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A bullet item: "[*]", "[•]", a "-", or a "•" starting the line.
    private static readonly Regex BulletMarker = new(
        @"^\s*\[\s*(?:\*|•)\s*\]\s*|^\s*[-•]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<RemoteRequirement> Extract(string description)
    {
        var names = new List<string>();
        var headingFound = false;
        var collecting = false;
        foreach (var rawLine in SplitLines(description))
        {
            if (!headingFound)
            {
                var text = StripTags(rawLine);
                // A heading line is short, contains the word, and carries some markup (the
                // website renders it inside [size]/[b]/[center] tags).
                if (text.Length > 0 && text.Length <= 60
                    && (rawLine.Contains('[', StringComparison.Ordinal) || rawLine.Contains('<', StringComparison.Ordinal))
                    && HeadingWord.IsMatch(text))
                {
                    headingFound = true;
                    continue;
                }
                continue;
            }

            var line = StripTags(rawLine);
            if (line.Length == 0)
                continue;
            if (line.StartsWith("[/list]", StringComparison.OrdinalIgnoreCase))
            {
                if (collecting)
                    break;
                continue;
            }
            var bullet = BulletMarker.Match(rawLine);
            if (bullet.Success)
            {
                collecting = true;
                var text = StripTags(rawLine[bullet.Length..]).Trim();
                if (text.Length > 0 && names.All(existing => !existing.Equals(text, StringComparison.OrdinalIgnoreCase)))
                    names.Add(text);
                continue;
            }
            // A non-bullet, markup-only fragment (e.g. a dangling [/size]) is skipped until a
            // bullet or real text appears.
            if (Regex.IsMatch(rawLine, @"^[^\w\s]*$"))
                continue;
            // Any other non-empty line after the list started ends the prerequisite block.
            if (collecting)
                break;
        }

        return names.Select(name => new RemoteRequirement(RemoteRequirementKind.Prerequisite, name)).ToList();
    }

    // A line break is a real newline or a "<br ...>" tag (Nexus BBCode uses the latter).
    private static readonly Regex LineBreakPattern = new(@"\r\n|\n|<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (var part in LineBreakPattern.Split(text))
            yield return part;
    }

    /// <summary>Removes BBCode and HTML tags, then decodes the common entities.</summary>
    private static string StripTags(string raw)
    {
        var withoutBb = Regex.Replace(raw, @"\[[^\[\]]{0,60}\]", " ", RegexOptions.Compiled);
        var withoutHtml = Regex.Replace(withoutBb, "</?(?:br|/br|center|div|span|p|img|a|b|u|i|s|font|table|tr|td|th|h[1-6])[^>]*>", " ", RegexOptions.IgnoreCase);
        var result = withoutHtml;
        foreach (var (entity, replacement) in Entities)
            result = result.Replace(entity, replacement, StringComparison.OrdinalIgnoreCase);
        // The Nexus description uses a zero-width no-break space after the bullet marker; the
        // ordinary whitespace trim does not remove it.
        result = result
            .Replace("\uFEFF", " ", StringComparison.Ordinal)
            .Replace("\u200B", " ", StringComparison.Ordinal)
            .Replace("\u200E", " ", StringComparison.Ordinal);
        return string.Join(' ', result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static readonly (string Entity, string Replacement)[] Entities =
    [
        ("&amp;", "&"),
        ("&#43;", "+"),
        ("&nbsp;", " "),
        ("&quot;", "\""),
        ("&#39;", "'"),
    ];
}
