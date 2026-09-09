using System;
using System.Text.Json;
using Wildpinkler.App.Commands;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RemoteCommandDtoTests
{
    [Fact]
    public void RemoteModDto_DoesNotExposeProviderHtmlOrLocalArchivePath()
    {
        var dto = new RemoteModDto("mod-1", "nexus", "Example", "Author", "1.0", "Main", null, false, "Summary");
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("ArchivePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DescriptionHtml", json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteFileDto_ContainsSanitizedFieldsOnly()
    {
        var dto = new RemoteFileDto("mod-1", "nexus", "file-1", "Example.zip", "1.0", 42, "md5", null,
            RemoteFileCategory.Main, true, "description", "changelog");
        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("changelog", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchivePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteRateLimitDto_ReportsExhaustionWithoutCredentials()
    {
        var dto = new RemoteRateLimitDto("nexus", 0, 12, DateTimeOffset.UtcNow, null, true);
        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("IsExhausted", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Credential", json, StringComparison.OrdinalIgnoreCase);
    }
}
