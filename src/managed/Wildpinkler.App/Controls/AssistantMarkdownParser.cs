using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Wildpinkler.App.Controls;

[Flags]
public enum MarkdownInlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strikethrough = 4,
    Code = 8,
    LineBreak = 16,
}

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    BulletItem,
    OrderedItem,
    Quote,
    CodeBlock,
    Rule,
    Table,
}

public sealed record MarkdownInline(string Text, MarkdownInlineStyle Style);

public sealed record MarkdownTableRow(IReadOnlyList<IReadOnlyList<MarkdownInline>> Cells, bool IsHeader);

public sealed record MarkdownBlock
{
    public MarkdownBlockKind Kind { get; init; }

    public IReadOnlyList<MarkdownInline> Inlines { get; init; } = [];

    public IReadOnlyList<MarkdownTableRow> Rows { get; init; } = [];

    public string Literal { get; init; } = string.Empty;

    public string? Language { get; init; }

    public int Indent { get; init; }

    public int Level { get; init; }

    public string Marker { get; init; } = string.Empty;
}

public static partial class AssistantMarkdownParser
{
    private const int MaxCharacters = 20_000;
    private const int MaxLines = 300;
    private const int MaxBlocks = 300;
    private const int MaxInlineDepth = 3;
    private const int MaxInlinesPerBlock = 512;
    private const int MaxListIndent = 3;
    private const int MaxLanguageCharacters = 32;
    private const int MaxTableColumns = 8;
    private const int MaxTableRows = 50;

    private static readonly MarkdownInline LineBreakInline = new(string.Empty, MarkdownInlineStyle.LineBreak);

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

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var sanitized = Sanitize(markdown);
        if (sanitized.Length == 0)
            return blocks;

        var lines = sanitized.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();

        for (var index = 0; index < lines.Length && blocks.Count < MaxBlocks; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (TryReadFence(trimmed, out var fence, out var fenceLength, out var language))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(ReadCodeBlock(lines, ref index, fence, fenceLength, language));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph(blocks, paragraph);
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Rule });
                continue;
            }

            var table = TryReadTable(lines, ref index);
            if (table is not null)
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(table);
                continue;
            }

            var heading = CountRun(trimmed, 0, '#');
            if (heading is >= 1 and <= 6 && heading < trimmed.Length && trimmed[heading] == ' ')
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Heading,
                    Level = heading,
                    Inlines = ParseInlines(trimmed[heading..].Trim().TrimEnd('#').TrimEnd()),
                });
                continue;
            }

            if (trimmed[0] == '>')
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Quote,
                    Inlines = ParseInlines(trimmed[1..].TrimStart()),
                });
                continue;
            }

            if (IsBulletItem(trimmed))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.BulletItem,
                    Indent = IndentLevel(line),
                    Inlines = ParseInlines(trimmed[2..].TrimStart()),
                });
                continue;
            }

            if (TryReadOrderedMarker(trimmed, out var marker))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.OrderedItem,
                    Indent = IndentLevel(line),
                    Marker = marker,
                    Inlines = ParseInlines(trimmed[marker.Length..].TrimStart()),
                });
                continue;
            }

            paragraph.Add(line);
        }

        if (blocks.Count < MaxBlocks)
            FlushParagraph(blocks, paragraph);

        return blocks;
    }

    public static IReadOnlyList<MarkdownInline> ParseInlines(string? text)
    {
        var inlines = new List<MarkdownInline>();
        if (!string.IsNullOrEmpty(text))
            AppendSpan(inlines, text.AsSpan(), MarkdownInlineStyle.None, 0);
        return inlines;
    }

    private static void FlushParagraph(List<MarkdownBlock> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0)
            return;

        var inlines = new List<MarkdownInline>();
        for (var index = 0; index < paragraph.Count; index++)
        {
            if (index > 0)
                inlines.Add(LineBreakInline);
            inlines.AddRange(ParseInlines(paragraph[index].Trim()));
        }

        paragraph.Clear();
        blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Paragraph, Inlines = inlines });
    }

    private static MarkdownBlock ReadCodeBlock(string[] lines, ref int index, char fence, int fenceLength, string? language)
    {
        var code = new StringBuilder();
        index++;

        // An unterminated fence still produces a block so streaming answers render as they arrive.
        while (index < lines.Length && !IsClosingFence(lines[index], fence, fenceLength))
        {
            if (code.Length > 0)
                code.Append('\n');
            code.Append(lines[index]);
            index++;
        }

        return new MarkdownBlock
        {
            Kind = MarkdownBlockKind.CodeBlock,
            Literal = code.ToString(),
            Language = language,
        };
    }

    private static bool TryReadFence(string trimmed, out char fence, out int length, out string? language)
    {
        fence = '\0';
        length = 0;
        language = null;

        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
            return false;

        fence = trimmed[0];
        length = CountRun(trimmed, 0, fence);
        if (length < 3)
            return false;

        language = NormalizeLanguage(trimmed[length..]);
        return true;
    }

    private static bool IsClosingFence(string line, char fence, int length)
    {
        var trimmed = line.TrimStart();
        return CountRun(trimmed, 0, fence) >= length && trimmed.TrimEnd(fence).Trim().Length == 0;
    }

    private static string? NormalizeLanguage(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length is 0 or > MaxLanguageCharacters)
            return null;

        foreach (var character in candidate)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('+' or '-' or '#' or '.' or '_'))
                return null;
        }

        return candidate;
    }

    private static MarkdownBlock? TryReadTable(string[] lines, ref int index)
    {
        if (index + 1 >= lines.Length || !lines[index].Contains('|', StringComparison.Ordinal))
            return null;
        if (!IsTableDelimiterRow(lines[index + 1]))
            return null;

        var headers = SplitCells(lines[index]);
        if (headers.Count is < 2 or > MaxTableColumns)
            return null;

        var rows = new List<MarkdownTableRow> { BuildRow(headers, isHeader: true) };
        var cursor = index + 2;
        while (cursor < lines.Length
               && rows.Count < MaxTableRows
               && lines[cursor].Contains('|', StringComparison.Ordinal)
               && lines[cursor].Trim().Length > 0)
        {
            var cells = SplitCells(lines[cursor]);
            if (cells.Count > MaxTableColumns)
                break;
            rows.Add(BuildRow(cells, isHeader: false));
            cursor++;
        }

        index = cursor - 1;
        return new MarkdownBlock { Kind = MarkdownBlockKind.Table, Rows = rows };
    }

    private static MarkdownTableRow BuildRow(List<string> cells, bool isHeader)
    {
        var parsed = new List<IReadOnlyList<MarkdownInline>>(cells.Count);
        foreach (var cell in cells)
            parsed.Add(ParseInlines(cell));
        return new MarkdownTableRow(parsed, isHeader);
    }

    private static bool IsTableDelimiterRow(string line)
    {
        if (!line.Contains('|', StringComparison.Ordinal))
            return false;

        var cells = SplitCells(line);
        if (cells.Count < 2)
            return false;

        foreach (var cell in cells)
        {
            var value = cell.Trim();
            if (value.StartsWith(':'))
                value = value[1..];
            if (value.EndsWith(':'))
                value = value[..^1];
            if (value.Length == 0)
                return false;
            foreach (var character in value)
            {
                if (character != '-')
                    return false;
            }
        }

        return true;
    }

    private static List<string> SplitCells(string line)
    {
        var value = line.Trim();
        if (value.StartsWith('|'))
            value = value[1..];
        if (value.EndsWith('|') && !value.EndsWith("\\|", StringComparison.Ordinal))
            value = value[..^1];

        var cells = new List<string>();
        var builder = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] == '|')
            {
                builder.Append('|');
                index++;
                continue;
            }

            if (value[index] == '|')
            {
                cells.Add(builder.ToString().Trim());
                builder.Clear();
                continue;
            }

            builder.Append(value[index]);
        }

        cells.Add(builder.ToString().Trim());
        return cells;
    }

    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3)
            return false;

        var marker = trimmed[0];
        if (marker is not ('-' or '*' or '_'))
            return false;

        var count = 0;
        foreach (var character in trimmed)
        {
            if (character == marker)
                count++;
            else if (character != ' ')
                return false;
        }

        return count >= 3;
    }

    private static bool IsBulletItem(string trimmed) =>
        trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ';

    private static bool TryReadOrderedMarker(string trimmed, out string marker)
    {
        marker = string.Empty;
        var digits = 0;
        while (digits < trimmed.Length && digits < 3 && char.IsAsciiDigit(trimmed[digits]))
            digits++;

        if (digits == 0 || digits + 1 >= trimmed.Length)
            return false;
        if (trimmed[digits] is not ('.' or ')') || trimmed[digits + 1] != ' ')
            return false;

        marker = trimmed[..(digits + 1)];
        return true;
    }

    private static int IndentLevel(string line)
    {
        var spaces = 0;
        foreach (var character in line)
        {
            if (character == ' ')
                spaces++;
            else if (character == '\t')
                spaces += 4;
            else
                break;
        }

        return Math.Min(spaces / 2, MaxListIndent);
    }

    private static void AppendSpan(List<MarkdownInline> output, ReadOnlySpan<char> text, MarkdownInlineStyle style, int depth)
    {
        var literal = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\' && index + 1 < text.Length && IsEscapable(text[index + 1]))
            {
                literal.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (current == '`')
            {
                var ticks = CountRun(text, index, '`');
                var close = FindClose(text, index + ticks, '`', ticks);
                if (close > index + ticks)
                {
                    Flush(output, literal, style);
                    Add(output, new MarkdownInline(text[(index + ticks)..close].ToString(), style | MarkdownInlineStyle.Code));
                    index = close + ticks;
                    continue;
                }
            }
            else if (current is '*' or '_' or '~' && depth < MaxInlineDepth)
            {
                var run = CountRun(text, index, current);
                var length = current == '~' ? (run >= 2 ? 2 : 0) : Math.Min(run, 3);
                if (length > 0 && CanOpen(text, index, length, current))
                {
                    var close = FindClose(text, index + length, current, length);
                    if (close > index + length)
                    {
                        Flush(output, literal, style);
                        AppendSpan(output, text[(index + length)..close], style | StyleFor(current, length), depth + 1);
                        index = close + length;
                        continue;
                    }
                }
            }

            literal.Append(current);
            index++;
        }

        Flush(output, literal, style);
    }

    private static MarkdownInlineStyle StyleFor(char marker, int length) => marker switch
    {
        '~' => MarkdownInlineStyle.Strikethrough,
        _ => length switch
        {
            1 => MarkdownInlineStyle.Italic,
            2 => MarkdownInlineStyle.Bold,
            _ => MarkdownInlineStyle.Bold | MarkdownInlineStyle.Italic,
        },
    };

    private static void Flush(List<MarkdownInline> output, StringBuilder literal, MarkdownInlineStyle style)
    {
        if (literal.Length == 0)
            return;

        Add(output, new MarkdownInline(literal.ToString(), style));
        literal.Clear();
    }

    private static void Add(List<MarkdownInline> output, MarkdownInline inline)
    {
        if (output.Count < MaxInlinesPerBlock)
            output.Add(inline);
    }

    private static bool IsEscapable(char character) =>
        character is '*' or '_' or '~' or '`' or '\\' or '[' or ']' or '(' or ')' or '#' or '|' or '-' or '.' or '>';

    private static bool CanOpen(ReadOnlySpan<char> text, int index, int length, char marker)
    {
        var after = index + length;
        if (after >= text.Length || char.IsWhiteSpace(text[after]))
            return false;
        if (marker != '_')
            return true;

        // Keeps identifiers such as snake_case_name and My_File_v2.esp literal.
        return index == 0 || !char.IsLetterOrDigit(text[index - 1]);
    }

    private static bool CanClose(ReadOnlySpan<char> text, int index, int run, char marker)
    {
        if (marker == '`')
            return true;
        if (index == 0 || char.IsWhiteSpace(text[index - 1]))
            return false;
        if (marker != '_')
            return true;

        var after = index + run;
        return after >= text.Length || !char.IsLetterOrDigit(text[after]);
    }

    private static int FindClose(ReadOnlySpan<char> text, int start, char marker, int length)
    {
        var exact = FindClose(text, start, marker, length, exact: true);
        return exact >= 0 ? exact : FindClose(text, start, marker, length, exact: false);
    }

    private static int FindClose(ReadOnlySpan<char> text, int start, char marker, int length, bool exact)
    {
        var index = start;
        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == '`' && marker != '`')
            {
                var ticks = CountRun(text, index, '`');
                var end = IndexOfRun(text, index + ticks, '`', ticks);
                index = end < 0 ? index + ticks : end + ticks;
                continue;
            }

            if (current == marker)
            {
                var run = CountRun(text, index, marker);
                if ((exact ? run == length : run >= length) && CanClose(text, index, run, marker))
                    return index;
                index += run;
                continue;
            }

            index++;
        }

        return -1;
    }

    private static int IndexOfRun(ReadOnlySpan<char> text, int start, char marker, int length)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == marker && CountRun(text, index, marker) >= length)
                return index;
        }

        return -1;
    }

    private static int CountRun(ReadOnlySpan<char> text, int index, char marker)
    {
        var count = 0;
        while (index + count < text.Length && text[index + count] == marker)
            count++;
        return count;
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
