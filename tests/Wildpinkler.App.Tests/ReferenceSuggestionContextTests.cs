using Wildpinkler.App.Controls;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ReferenceSuggestionContextTests
{
    [Theory]
    [InlineData("hello", 5)]
    [InlineData("hello @world ", 13)]
    [InlineData("@world text", 11)]
    public void TryGetQuery_WithoutActiveMention_ReturnsFalse(string text, int caretPosition)
    {
        Assert.False(ReferenceSuggestionContext.TryGetQuery(text, caretPosition, out _));
    }

    [Theory]
    [InlineData("@", 1, "")]
    [InlineData("Ask @pro", 8, "pro")]
    [InlineData("@first @second", 14, "second")]
    [InlineData("@profile", 1, "")]
    public void TryGetQuery_ActiveMention_ReturnsQuery(string text, int caretPosition, string expectedQuery)
    {
        Assert.True(ReferenceSuggestionContext.TryGetQuery(text, caretPosition, out var query));
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void TryGetQuery_CaretAfterWhitespace_ReturnsFalse()
    {
        Assert.False(ReferenceSuggestionContext.TryGetQuery("@profile details", 16, out _));
    }

    [Fact]
    public void TryGetQuery_CaretInsideMention_ReturnsTextBeforeCaret()
    {
        Assert.True(ReferenceSuggestionContext.TryGetQuery("@profile", 4, out var query));
        Assert.Equal("pro", query);
    }
}