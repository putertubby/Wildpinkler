using System;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RemoteTextSanitizerTests
{
    [Fact]
    public void ToPlainText_HtmlAndScript_IsPlainText()
    {
        var result = RemoteTextSanitizer.ToPlainText("<p>Useful <strong>summary</strong></p><script>alert(1)</script>", 1024);

        Assert.Equal("Useful summary alert(1)", result);
        Assert.DoesNotContain("<", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPlainText_UrlsAndDangerousSchemes_AreRemoved()
    {
        var result = RemoteTextSanitizer.ToPlainText(
            "Visit https://example.com and nxm://evil/file, file:///secret and data:text/plain,x.",
            1024);

        Assert.DoesNotContain("example.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nxm:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToPlainText_Entities_AreDecodedOnce()
    {
        var result = RemoteTextSanitizer.ToPlainText("&lt;script&gt; &amp;amp;", 1024);

        Assert.Equal("script &amp;", result);
    }

    [Fact]
    public void ToPlainText_OverLimit_TruncatesOnWordBoundary()
    {
        var result = RemoteTextSanitizer.ToPlainText("one two three four five", 18);

        Assert.EndsWith("[truncated]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("four", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPlainText_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RemoteTextSanitizer.ToPlainText(null, 100));
        Assert.Equal(string.Empty, RemoteTextSanitizer.ToPlainText("   ", 100));
        Assert.Equal(string.Empty, RemoteTextSanitizer.ToPlainText("value", 0));
    }

    [Fact]
    public void ToPlainText_ControlCharactersAndWhitespace_AreCollapsed()
    {
        var result = RemoteTextSanitizer.ToPlainText("one\r\n\t two", 1024);

        Assert.Equal("one two", result);
    }
}
