using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote;

public enum RemoteCredentialKind
{
    None,
    ApiKey,
    Sso,
    OAuth
}

/// <summary>A resolved credential ready to be attached to a request.</summary>
public sealed record RemoteCredential(RemoteCredentialKind Kind, string Value, DateTimeOffset? ExpiresAt = null)
{
    public static RemoteCredential None { get; } = new(RemoteCredentialKind.None, string.Empty);

    public bool IsUsable => Kind != RemoteCredentialKind.None &&
                            !string.IsNullOrWhiteSpace(Value) &&
                            (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>
/// Seam between how a credential is obtained and how it is used. An API key implementation is
/// interactive-free; an SSO or OAuth implementation drives a browser handshake behind the same call.
/// </summary>
public interface IRemoteCredentialProvider
{
    string SiteId { get; }

    RemoteCredentialKind Kind { get; }

    /// <summary>False when the flow cannot run yet, for example an unissued application registration.</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false, for display next to a disabled control.</summary>
    string? UnavailableReason { get; }

    /// <summary>Obtains a credential, prompting the user where the flow requires it.</summary>
    Task<RemoteCredential> AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>Renews an expiring credential, or returns it unchanged when renewal does not apply.</summary>
    Task<RemoteCredential> RefreshAsync(RemoteCredential current, CancellationToken cancellationToken = default);
}
