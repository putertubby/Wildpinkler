using System;
using System.Text.RegularExpressions;

namespace Wildpinkler.App.Controls;

public static partial class AssistantMarkdownParser
{
    private const int MaxCharacters = 20_000;
    private const int MaxLines = 300;

    public static string Sanitize(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var value = markdown.Length > MaxCharacters ? markdown[..MaxCharacters] + "\n[truncated]" : markdown;
        value = ImagePattern().Replace(value, "[image: $1]");
        value = LinkPattern().Replace(value, "$1 ($2)");
        value = HtmlPattern().Replace(value, string.Empty);
        value = ControlPattern().Replace(value, string.Empty);

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > MaxLines)
            value = string.Join('\n', lines[..MaxLines]) + "\n[truncated]";

        return value;
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex HtmlPattern();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlPattern();
}
