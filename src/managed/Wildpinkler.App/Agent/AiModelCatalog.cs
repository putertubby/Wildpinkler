using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Agent;

/// <summary>
/// Lists the models an endpoint advertises. Free-tier model names change often, so this is only an
/// aid: the settings page keeps the model field editable and a manual name always wins.
/// </summary>
public sealed class AiModelCatalog : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    // Redirects are refused so a relocation cannot carry the key to another host.
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<string> Models)> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        AiConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.Endpoint is null)
            return [];

        var key = configuration.Endpoint.AbsoluteUri;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheLifetime)
                return cached.Models;

            var models = await FetchAsync(configuration, cancellationToken);
            if (models.Count > 0)
                _cache[key] = (DateTimeOffset.UtcNow, models);
            return models;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate() => _cache.Clear();

    /// <summary>Answers whether the endpoint is listening, so a local server that is not running can be named.</summary>
    public async Task<bool> IsReachableAsync(AiConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (configuration.Endpoint is null)
            return false;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(configuration.Endpoint.AbsoluteUri.TrimEnd('/') + "/models"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.SendAsync(request, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> FetchAsync(
        AiConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var address = new Uri(configuration.Endpoint!.AbsoluteUri.TrimEnd('/') + "/models");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (!string.IsNullOrEmpty(configuration.ApiKey))
            request.Headers.Add("Authorization", $"Bearer {configuration.ApiKey}");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        return ParseModels(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Reads the OpenAI model-list shape. Anything else yields nothing rather than an error.</summary>
    public static IReadOnlyList<string> ParseModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            return data.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
