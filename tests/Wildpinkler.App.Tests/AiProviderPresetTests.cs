using System;
using System.Linq;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AiProviderPresetTests
{
    [Fact]
    public void TryGet_KnownId_ReturnsPreset()
    {
        Assert.True(AiProviderPresets.TryGet("openrouter", out var preset));
        Assert.Equal("https://openrouter.ai/api/v1", preset.Endpoint);
    }

    [Fact]
    public void TryGet_UnknownId_ReturnsFalse() =>
        Assert.False(AiProviderPresets.TryGet("nowhere", out _));

    [Fact]
    public void GetOrDefault_UnknownId_ReturnsLocalKeylessProvider()
    {
        var preset = AiProviderPresets.GetOrDefault(null);

        Assert.Equal("ollama", preset.Id);
        Assert.False(preset.RequiresApiKey);
    }

    [Fact]
    public void All_EveryPresetWithAnEndpoint_UsesHttpsOrLoopback()
    {
        foreach (var preset in AiProviderPresets.All.Where(candidate => candidate.Endpoint.Length > 0))
        {
            var uri = new Uri(preset.Endpoint);
            Assert.True(uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback, preset.Id);
        }
    }

    [Fact]
    public void All_EveryPreset_HasUniqueId() =>
        Assert.Equal(AiProviderPresets.All.Count, AiProviderPresets.All.Select(preset => preset.Id).Distinct().Count());
}
