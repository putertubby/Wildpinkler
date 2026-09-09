using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AiConfigurationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-ai-" + Guid.NewGuid().ToString("n"));
    private readonly CredentialStore _credentials;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly AiConfigurationStore _store;

    public AiConfigurationStoreTests()
    {
        Directory.CreateDirectory(_root);
        _credentials = new CredentialStore(_root);
        _settingsStore = new AppSettingsStore(_root);
        _settings = _settingsStore.Load();
        _store = new AiConfigurationStore(_settings, _settingsStore, _credentials);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_Defaults_UsesKeylessLocalProvider()
    {
        var configuration = await _store.GetAsync(Token);

        Assert.Equal("ollama", configuration.ProviderId);
        Assert.Equal("http://localhost:11434/v1", configuration.Endpoint?.AbsoluteUri.TrimEnd('/'));
        Assert.False(configuration.RequiresApiKey);
        Assert.True(configuration.IsUsable);
    }

    [Fact]
    public async Task GetAsync_MissingModel_FallsBackToPresetDefault()
    {
        await _store.SaveAsync("openai", null, null, "sk-test", true, true, Token);

        var configuration = await _store.GetAsync(Token);

        Assert.Equal("gpt-4o-mini", configuration.ModelId);
    }

    [Fact]
    public async Task SaveAsync_ApiKey_StoresInCredentialStoreNotSettingsFile()
    {
        await _store.SaveAsync("openrouter", null, null, "sk-secret-value", true, true, Token);

        var settingsText = await File.ReadAllTextAsync(Path.Combine(_root, "app-settings.json"), Token);
        Assert.DoesNotContain("sk-secret-value", settingsText, StringComparison.Ordinal);
        Assert.Equal("sk-secret-value", await _credentials.GetAsync("assistant:openrouter"));
    }

    [Fact]
    public async Task SaveAsync_KeyForOneProvider_LeavesAnotherProvidersKeyIntact()
    {
        await _store.SaveAsync("openrouter", null, null, "router-key", true, true, Token);
        await _store.SaveAsync("groq", null, null, "groq-key", true, true, Token);

        Assert.Equal("router-key", await _credentials.GetAsync("assistant:openrouter"));
        Assert.Equal("groq-key", await _credentials.GetAsync("assistant:groq"));
    }

    [Fact]
    public async Task SaveAsync_NullApiKey_LeavesStoredKeyUnchanged()
    {
        await _store.SaveAsync("openai", null, null, "sk-original", true, true, Token);
        await _store.SaveAsync("openai", null, "gpt-4o", null, true, true, Token);

        Assert.Equal("sk-original", await _credentials.GetAsync("assistant:openai"));
    }

    [Fact]
    public async Task SaveAsync_EmptyApiKey_ClearsStoredKey()
    {
        await _store.SaveAsync("openai", null, null, "sk-original", true, true, Token);
        await _store.SaveAsync("openai", null, null, string.Empty, true, true, Token);

        Assert.Null(await _credentials.GetAsync("assistant:openai"));
    }

    [Fact]
    public async Task SaveAsync_UnknownProvider_ReturnsFailure()
    {
        var result = await _store.SaveAsync("nowhere", null, null, null, true, true, Token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SaveAsync_HttpEndpointOnRemoteHost_ReturnsFailure()
    {
        var result = await _store.SaveAsync("custom", "http://evil.example/v1", "any", null, true, true, Token);

        Assert.False(result.Succeeded);
        Assert.Contains("https", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_HttpEndpointOnLoopback_Succeeds()
    {
        var result = await _store.SaveAsync("custom", "http://127.0.0.1:1234/v1", "any", null, true, true, Token);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task SaveAsync_Changed_RaisesChangedEvent()
    {
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        await _store.SaveAsync("openai", null, null, "sk-test", true, true, Token);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SaveAsync_Toggles_ArePersistedToTheSettingsFile()
    {
        await _store.SaveAsync("ollama", null, null, null, false, false, Token);

        var reloaded = new AppSettingsStore(_root).Load();

        Assert.False(reloaded.AssistantAutoRunReadOnlyTools);
        Assert.False(reloaded.AssistantPersistTranscript);
    }

    [Fact]
    public async Task GetAsync_SettingsFileEditedToACleartextRemoteEndpoint_ReturnsUnusableConfiguration()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "app-settings.json"),
            """
            {"SchemaVersion":2,"AssistantProviderId":"custom","AssistantEndpoint":"http://evil.example/v1","AssistantModelId":"any"}
            """,
            Token);

        var reloaded = new AppSettingsStore(_root).Load();
        var store = new AiConfigurationStore(reloaded, new AppSettingsStore(_root), _credentials);
        var configuration = await store.GetAsync(Token);

        Assert.Null(configuration.Endpoint);
        Assert.False(configuration.IsUsable);
    }

    [Fact]
    public async Task GetAsync_LoopbackEndpoint_IsNotReportedAsRemote() =>
        Assert.False((await _store.GetAsync(Token)).IsRemote);

    [Fact]
    public async Task GetAsync_HostedProvider_IsReportedAsRemote()
    {
        await _store.SaveAsync("openrouter", null, null, "sk-test", true, true, Token);

        Assert.True((await _store.GetAsync(Token)).IsRemote);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("")]
    public void TryParseEndpoint_UnusableInput_ReturnsFalse(string input) =>
        Assert.False(AiConfigurationStore.TryParseEndpoint(input, out _, out _));

    [Fact]
    public void TryParseEndpoint_HttpsAddress_ReturnsTrue()
    {
        Assert.True(AiConfigurationStore.TryParseEndpoint("https://example.com/v1", out var endpoint, out _));
        Assert.Equal("example.com", endpoint!.Host);
    }

    public void Dispose()
    {
        _credentials.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
