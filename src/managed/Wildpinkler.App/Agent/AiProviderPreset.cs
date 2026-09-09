using System;
using System.Collections.Generic;
using System.Linq;

namespace Wildpinkler.App.Agent;

/// <summary>
/// A known OpenAI-compatible endpoint offered as a one-click starting point. Presets carry no key
/// material: a shipped application cannot embed a shared credential, so every paid or metered
/// provider asks the user for their own.
/// </summary>
public sealed record AiProviderPreset(
    string Id,
    string DisplayName,
    string Endpoint,
    bool RequiresApiKey,
    string DefaultModel,
    string? SignupUrl,
    string Notes)
{
    /// <summary>True when the user must supply the endpoint themselves before anything can work.</summary>
    public bool RequiresEndpoint => Endpoint.Length == 0;
}

public static class AiProviderPresets
{
    public const string DefaultProviderId = "ollama";

    private static readonly IReadOnlyList<AiProviderPreset> Presets =
    [
        new("ollama", "Ollama (local)", "http://localhost:11434/v1", false, "qwen3:8b",
            "https://ollama.com/download",
            "Runs entirely on this machine at no cost. Install Ollama, then run 'ollama pull qwen3:8b'."),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", true,
            "meta-llama/llama-3.3-70b-instruct:free",
            "https://openrouter.ai/settings/keys",
            "Offers free models with your own key. Free tiers are rate limited."),
        new("groq", "Groq", "https://api.groq.com/openai/v1", true, "llama-3.3-70b-versatile",
            "https://console.groq.com/keys",
            "Very fast inference with a free tier. Requires your own key."),
        new("openai", "OpenAI", "https://api.openai.com/v1", true, "gpt-4o-mini",
            "https://platform.openai.com/api-keys",
            "Paid usage billed to your OpenAI account."),
        new("azure", "Azure OpenAI", string.Empty, true, "gpt-4o-mini",
            "https://portal.azure.com",
            "Enter your resource endpoint ending in /openai/v1/ and use your deployment name as the model."),
        new("custom", "Custom (OpenAI-compatible)", string.Empty, false, string.Empty,
            null,
            "Any server that speaks the OpenAI chat completions API, such as LM Studio or llama.cpp."),
    ];

    public static IReadOnlyList<AiProviderPreset> All => Presets;

    public static AiProviderPreset Default => Presets[0];

    public static bool TryGet(string? id, out AiProviderPreset preset)
    {
        preset = Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return preset is not null;
    }

    public static AiProviderPreset GetOrDefault(string? id) => TryGet(id, out var preset) ? preset : Default;
}
