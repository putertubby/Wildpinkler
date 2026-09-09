using System.Linq;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ChatReferenceMappingTests
{
    [Fact]
    public void BuildUserContent_NoReferences_ReturnsThePromptUnchanged()
    {
        var message = new ChatMessage(ChatRole.User, "Check my profile.");

        Assert.Equal("Check my profile.", OpenAiCompatibleChatCompletionClient.BuildUserContent(message));
    }

    [Fact]
    public void BuildUserContent_References_AddsStableIdsOnlyAtProviderBoundary()
    {
        var message = new ChatMessage(ChatRole.User, "Check this.")
        {
            References = [new ChatReference("profile", "profile-123", "Main profile")]
        };

        var providerContent = OpenAiCompatibleChatCompletionClient.BuildUserContent(message);

        Assert.Equal("Check this.", message.Content);
        Assert.Contains("profile-123", providerContent);
        Assert.Contains("Main profile", providerContent);
        Assert.DoesNotContain("ArchivePath", providerContent);
    }

    [Fact]
    public void BuildUserContent_MultipleReferences_PreservesTheirOrder()
    {
        var message = new ChatMessage(ChatRole.User, "Compare these.")
        {
            References =
            [
                new ChatReference("mod", "mod-a", "First"),
                new ChatReference("mod", "mod-b", "Second"),
            ]
        };

        var providerContent = OpenAiCompatibleChatCompletionClient.BuildUserContent(message);

        Assert.True(providerContent.IndexOf("mod-a", System.StringComparison.Ordinal)
            < providerContent.IndexOf("mod-b", System.StringComparison.Ordinal));
    }
}
