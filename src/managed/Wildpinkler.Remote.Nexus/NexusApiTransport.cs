using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote.Nexus;

/// <summary>
/// HTTP transport for the Nexus v1 REST API: identifying headers, throttling, rate-limit tracking
/// and translation of every failure into <see cref="RemoteSiteException"/>.
/// </summary>
internal sealed class NexusApiTransport : IDisposable
{
    private const string ProtocolVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly NexusThrottle _throttle;

    public NexusApiTransport(string applicationName, string applicationVersion, HttpClient? client = null, NexusThrottle? throttle = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { BaseAddress = new Uri("https://api.nexusmods.com/v1/") };
        _client.BaseAddress ??= new Uri("https://api.nexusmods.com/v1/");
        _throttle = throttle ?? new NexusThrottle();

        // Required by the Nexus API acceptable use policy so usage can be attributed to this app.
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", applicationName);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", applicationVersion);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Protocol-Version", ProtocolVersion);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"{applicationName}/{applicationVersion}");
    }

    public RemoteRateLimit LastRateLimit { get; private set; } = RemoteRateLimit.Unknown;

    public void SetPremium(bool isPremium) => _throttle.SetPremium(isPremium);

    public async Task<T> GetAsync<T>(string path, RemoteCredential credential, CancellationToken cancellationToken)
    {
        if (!credential.IsUsable)
            throw new RemoteSiteException(RemoteErrorKind.Unauthorized, "No Nexus Mods API key is configured.", NexusSiteProvider.Id);

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("apikey", credential.Value);
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new RemoteSiteException(RemoteErrorKind.Network, "Could not reach Nexus Mods.", NexusSiteProvider.Id, LastRateLimit, innerException: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteSiteException(RemoteErrorKind.Network, "The request to Nexus Mods timed out.", NexusSiteProvider.Id, LastRateLimit, innerException: exception);
        }

        using (response)
        {
            LastRateLimit = ReadRateLimit(response);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw Translate(response.StatusCode, body, LastRateLimit);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                       ?? throw new RemoteSiteException(RemoteErrorKind.Server, "Nexus Mods returned an empty response.", NexusSiteProvider.Id, LastRateLimit);
            }
            catch (JsonException exception)
            {
                throw new RemoteSiteException(RemoteErrorKind.Server, "Nexus Mods returned a response this version cannot read.", NexusSiteProvider.Id, LastRateLimit, innerException: exception);
            }
        }
    }

    public static RemoteSiteException Translate(HttpStatusCode statusCode, string body, RemoteRateLimit rateLimit)
    {
        var message = ExtractMessage(body) ?? $"Nexus Mods returned {(int)statusCode} {statusCode}.";
        var kind = statusCode switch
        {
            HttpStatusCode.Unauthorized => RemoteErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => RemoteErrorKind.Forbidden,
            HttpStatusCode.NotFound => RemoteErrorKind.NotFound,
            HttpStatusCode.TooManyRequests => RemoteErrorKind.RateLimited,
            >= HttpStatusCode.InternalServerError => RemoteErrorKind.Server,
            _ => RemoteErrorKind.Unknown
        };

        return new RemoteSiteException(kind, message, NexusSiteProvider.Id, rateLimit, rateLimit.NextReset);
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                return message.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static RemoteRateLimit ReadRateLimit(HttpResponseMessage response) => new(
        ReadInt(response, "X-RL-Hourly-Remaining"),
        ReadInt(response, "X-RL-Daily-Remaining"),
        ReadReset(response, "X-RL-Hourly-Reset"),
        ReadReset(response, "X-RL-Daily-Reset"));

    private static int? ReadInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var value)
            ? value
            : null;

    private static DateTimeOffset? ReadReset(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;
        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // The header has been a unix timestamp historically and a date string more recently.
        if (long.TryParse(raw, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
