using System.Linq;
using Wildpinkler.App.Controls;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AssistantMarkdownParserTests
{
    [Fact]
    public void Sanitize_ImageSyntax_ReplacesItWithInertText()
    {
        var result = AssistantMarkdownParser.Sanitize("![logo](https://attacker.example/a.png)");

        Assert.Equal("[image: logo]", result);
    }

    [Fact]
    public void Sanitize_LinkSyntax_ShowsTextAndTargetWithoutMakingItClickable()
    {
        var result = AssistantMarkdownParser.Sanitize("[open](nxm://evil/file)");

        Assert.Equal("open (nxm://evil/file)", result);
    }

    [Fact]
    public void Sanitize_Html_IsRemoved()
    {
        var result = AssistantMarkdownParser.Sanitize("<script>alert(1)</script>safe");

        Assert.Equal("alert(1)safe", result);
    }

    [Fact]
    public void Sanitize_OversizedInput_IsCapped()
    {
        var result = AssistantMarkdownParser.Sanitize(new string('x', 50_000));

        Assert.True(result.Length < 20_100);
        Assert.EndsWith("[truncated]", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_PreservesSafeMarkdown()
    {
        var result = AssistantMarkdownParser.Sanitize("# Title\n- **important**\n`code`");

        Assert.Equal("# Title\n- **important**\n`code`", result);
    }

    [Theory]
    [InlineData("**bold**", MarkdownInlineStyle.Bold, "bold")]
    [InlineData("__bold__", MarkdownInlineStyle.Bold, "bold")]
    [InlineData("*slanted*", MarkdownInlineStyle.Italic, "slanted")]
    [InlineData("~~gone~~", MarkdownInlineStyle.Strikethrough, "gone")]
    [InlineData("`code`", MarkdownInlineStyle.Code, "code")]
    public void ParseInlines_SingleMarker_AppliesStyle(string source, MarkdownInlineStyle expected, string text)
    {
        var inline = Assert.Single(AssistantMarkdownParser.ParseInlines(source));

        Assert.Equal(expected, inline.Style);
        Assert.Equal(text, inline.Text);
    }

    [Fact]
    public void ParseInlines_NestedMarkers_CombineStyles()
    {
        var inline = Assert.Single(AssistantMarkdownParser.ParseInlines("***both***"));

        Assert.Equal(MarkdownInlineStyle.Bold | MarkdownInlineStyle.Italic, inline.Style);
        Assert.Equal("both", inline.Text);
    }

    [Fact]
    public void ParseInlines_UnterminatedBold_KeepsMarkersLiteral()
    {
        var inline = Assert.Single(AssistantMarkdownParser.ParseInlines("**still typing"));

        Assert.Equal(MarkdownInlineStyle.None, inline.Style);
        Assert.Equal("**still typing", inline.Text);
    }

    [Fact]
    public void ParseInlines_UnderscoreInsideWord_IsNotItalic()
    {
        var inline = Assert.Single(AssistantMarkdownParser.ParseInlines("My_File_v2.esp"));

        Assert.Equal(MarkdownInlineStyle.None, inline.Style);
        Assert.Equal("My_File_v2.esp", inline.Text);
    }

    [Fact]
    public void ParseInlines_MarkersInsideCodeSpan_StayLiteral()
    {
        var inline = Assert.Single(AssistantMarkdownParser.ParseInlines("`a ** b`"));

        Assert.Equal(MarkdownInlineStyle.Code, inline.Style);
        Assert.Equal("a ** b", inline.Text);
    }

    [Fact]
    public void ParseInlines_EscapedMarker_IsLiteral()
    {
        var inline = Assert.Single(AssistantMarkdownParser.ParseInlines(@"\*not italic\*"));

        Assert.Equal(MarkdownInlineStyle.None, inline.Style);
        Assert.Equal("*not italic*", inline.Text);
    }

    [Fact]
    public void ParseInlines_MultiplicationSigns_AreNotItalic()
    {
        var inlines = AssistantMarkdownParser.ParseInlines("5 * 3 * 2");

        Assert.All(inlines, inline => Assert.Equal(MarkdownInlineStyle.None, inline.Style));
    }

    [Fact]
    public void ParseInlines_BoldInsideSentence_SplitsRuns()
    {
        var inlines = AssistantMarkdownParser.ParseInlines("run **now** please");

        Assert.Collection(
            inlines,
            first => Assert.Equal("run ", first.Text),
            second => Assert.Equal(MarkdownInlineStyle.Bold, second.Style),
            third => Assert.Equal(" please", third.Text));
    }

    [Fact]
    public void Parse_Heading_CapturesLevelAndText()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("### Load order"));

        Assert.Equal(MarkdownBlockKind.Heading, block.Kind);
        Assert.Equal(3, block.Level);
        Assert.Equal("Load order", Assert.Single(block.Inlines).Text);
    }

    [Fact]
    public void Parse_FencedCode_CapturesLanguageAndBody()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("```csharp\nvar x = 1;\n```"));

        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("var x = 1;", block.Literal);
    }

    [Fact]
    public void Parse_FenceWithHostileLanguageTag_DropsTheTag()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("```c# rm -rf /\nbody\n```"));

        Assert.Null(block.Language);
    }

    [Fact]
    public void Parse_UnterminatedFence_StillEmitsCodeBlock()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("```\nhalf written"));

        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal("half written", block.Literal);
    }

    [Fact]
    public void Parse_IndentedBullet_SetsIndentLevel()
    {
        var blocks = AssistantMarkdownParser.Parse("- top\n  - nested");

        Assert.Equal(0, blocks[0].Indent);
        Assert.Equal(MarkdownBlockKind.BulletItem, blocks[1].Kind);
        Assert.Equal(1, blocks[1].Indent);
    }

    [Fact]
    public void Parse_OrderedItem_KeepsOriginalMarker()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("2. second"));

        Assert.Equal(MarkdownBlockKind.OrderedItem, block.Kind);
        Assert.Equal("2.", block.Marker);
        Assert.Equal("second", Assert.Single(block.Inlines).Text);
    }

    [Fact]
    public void Parse_Blockquote_StripsMarker()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("> careful"));

        Assert.Equal(MarkdownBlockKind.Quote, block.Kind);
        Assert.Equal("careful", Assert.Single(block.Inlines).Text);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    public void Parse_HorizontalRule_ProducesRuleBlock(string source)
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse(source));

        Assert.Equal(MarkdownBlockKind.Rule, block.Kind);
    }

    [Fact]
    public void Parse_BlankLine_DoesNotEmitEmptyParagraph()
    {
        var blocks = AssistantMarkdownParser.Parse("one\n\n\ntwo");

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind));
    }

    [Fact]
    public void Parse_AdjacentLines_BecomeOneParagraphWithLineBreak()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("first\nsecond"));

        Assert.Collection(
            block.Inlines,
            first => Assert.Equal("first", first.Text),
            second => Assert.Equal(MarkdownInlineStyle.LineBreak, second.Style),
            third => Assert.Equal("second", third.Text));
    }

    [Fact]
    public void Parse_PipeTable_ProducesTableBlock()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("| Mod | State |\n| --- | --- |\n| Alpha | On |"));

        Assert.Equal(MarkdownBlockKind.Table, block.Kind);
        Assert.Equal(2, block.Rows.Count);
        Assert.True(block.Rows[0].IsHeader);
        Assert.Equal("Alpha", block.Rows[1].Cells[0][0].Text);
    }

    [Fact]
    public void Parse_TableWithoutDelimiterRow_IsParagraph()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("| Mod | State |\n| Alpha | On |"));

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
    }

    [Fact]
    public void Parse_TableWithTooManyColumns_IsParagraph()
    {
        var header = "|" + string.Concat(Enumerable.Repeat(" c |", 12));
        var delimiter = "|" + string.Concat(Enumerable.Repeat(" --- |", 12));

        var blocks = AssistantMarkdownParser.Parse(header + "\n" + delimiter);

        Assert.All(blocks, block => Assert.NotEqual(MarkdownBlockKind.Table, block.Kind));
    }

    [Fact]
    public void Parse_HtmlAndImages_AreNeutralisedBeforeBlocks()
    {
        var block = Assert.Single(AssistantMarkdownParser.Parse("<b>![x](http://evil/a.png)</b>"));

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal("[image: x]", Assert.Single(block.Inlines).Text);
    }

    [Fact]
    public void Parse_DeeplyNestedMarkers_TerminatesAndIsBounded()
    {
        var blocks = AssistantMarkdownParser.Parse(new string('*', 2_000) + "text" + new string('*', 2_000));

        Assert.Single(blocks);
    }

    [Fact]
    public void Parse_OversizedInput_IsCapped()
    {
        var blocks = AssistantMarkdownParser.Parse(string.Join('\n', Enumerable.Repeat("line", 5_000)));

        Assert.True(blocks.Count <= 300);
    }
}
