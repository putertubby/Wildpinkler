using System;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Agent;

/// <summary>The resolved provider settings a chat client needs, with the preset defaults folded in.</summary>
public sealed record AiConfiguration(
    string ProviderId,
    Uri? Endpoint,
    string ModelId,
    bool RequiresApiKey,
    string? ApiKey)
{
    public bool IsUsable => Endpoint is not null
        && ModelId.Length > 0
        && (!RequiresApiKey || !string.IsNullOrWhiteSpace(ApiKey));

    /// <summary>True when the conversation would leave this machine.</summary>
    public bool IsRemote => Endpoint is not null && !AiConfigurationStore.IsLoopback(Endpoint);

    /// <summary>A sentence naming what is still missing, or null when the configuration is complete.</summary>
    public string? Problem => Endpoint is null
        ? "No endpoint is set for the assistant."
        : ModelId.Length == 0
            ? "No model is selected for the assistant."
            : RequiresApiKey && string.IsNullOrWhiteSpace(ApiKey)
                ? "This provider needs an API key."
                : null;
}

public sealed record AiConfigurationSaveResult(bool Succeeded, string? Message)
{
    public static readonly AiConfigurationSaveResult Ok = new(true, null);

    public static AiConfigurationSaveResult Failed(string message) => new(false, message);
}

/// <summary>
/// Owns the assistant's provider choice. Non-secret settings go to <see cref="AppSettingsStore"/>;
/// the API key goes to <see cref="CredentialStore"/> in a per-provider slot so switching providers
/// does not discard the previous key.
/// </summary>
public sealed class AiConfigurationStore
{
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _store;
    private readonly CredentialStore _credentials;

    public AiConfigurationStore(AppSettings settings, AppSettingsStore store, CredentialStore credentials)
    {
        _settings = settings;
        _store = store;
        _credentials = credentials;
    }

    /// <summary>Raised after a successful save so a cached client can rebuild itself.</summary>
    public event EventHandler? Changed;

    public static string CredentialKeyFor(string providerId) => $"assistant:{providerId}";

    public async Task<AiConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        var preset = AiProviderPresets.GetOrDefault(_settings.AssistantProviderId);
        var endpointText = Coalesce(_settings.AssistantEndpoint, preset.Endpoint);
        var model = Coalesce(_settings.AssistantModelId, preset.DefaultModel) ?? string.Empty;

        string? key = null;
        if (preset.RequiresApiKey)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key = await _credentials.GetAsync(CredentialKeyFor(preset.Id));
        }

        _ = TryParseEndpoint(endpointText, out var endpoint, out _);
        return new AiConfiguration(preset.Id, endpoint, model, preset.RequiresApiKey, key);
    }

    /// <summary>
    /// A null <paramref name="apiKey"/> leaves the stored key untouched; an empty string clears it.
    /// </summary>
    public async Task<AiConfigurationSaveResult> SaveAsync(
        string providerId,
        string? endpoint,
        string? modelId,
        string? apiKey,
        bool autoRunReadOnlyTools,
        bool persistTranscript,
        CancellationToken cancellationToken = default)
    {
        if (!AiProviderPresets.TryGet(providerId, out var preset))
            return AiConfigurationSaveResult.Failed($"'{providerId}' is not a known provider.");

        var endpointText = Coalesce(endpoint, preset.Endpoint);
        if (endpointText is not null && !TryParseEndpoint(endpointText, out _, out var problem))
            return AiConfigurationSaveResult.Failed(problem!);

        cancellationToken.ThrowIfCancellationRequested();

        _settings.AssistantProviderId = preset.Id;
        _settings.AssistantEndpoint = Normalize(endpoint) == preset.Endpoint ? null : Normalize(endpoint);
        _settings.AssistantModelId = Normalize(modelId) == preset.DefaultModel ? null : Normalize(modelId);
        _settings.AssistantAutoRunReadOnlyTools = autoRunReadOnlyTools;
        _settings.AssistantPersistTranscript = persistTranscript;
        _store.Save(_settings);

        if (apiKey is not null)
            await _credentials.SetAsync(CredentialKeyFor(preset.Id), apiKey);

        Changed?.Invoke(this, EventArgs.Empty);
        return AiConfigurationSaveResult.Ok;
    }

    public async Task<bool> HasStoredKeyAsync(string providerId) =>
        !string.IsNullOrEmpty(await _credentials.GetAsync(CredentialKeyFor(providerId)));

    /// <summary>
    /// The endpoint is user supplied, so cleartext HTTP is refused off the loopback interface: an
    /// API key must never travel unencrypted to a remote host.
    /// </summary>
    public static bool TryParseEndpoint(string? text, out Uri? endpoint, out string? problem)
    {
        endpoint = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "Enter the endpoint address.";
            return false;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
        {
            problem = "The endpoint must be a full address, such as https://example.com/v1.";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttps)
        {
            endpoint = parsed;
            return true;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && IsLoopback(parsed))
        {
            endpoint = parsed;
            return true;
        }

        problem = parsed.Scheme == Uri.UriSchemeHttp
            ? "Use https for a remote endpoint; plain http is only allowed for a local server."
            : "The endpoint must use http or https.";
        return false;
    }

    internal static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Coalesce(string? preferred, string fallback) =>
        Normalize(preferred) ?? (fallback.Length == 0 ? null : fallback);
}
