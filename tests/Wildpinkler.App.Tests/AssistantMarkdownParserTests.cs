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
}
