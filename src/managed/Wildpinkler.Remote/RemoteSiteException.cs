using System;

namespace Wildpinkler.Remote;

/// <summary>
/// Site-agnostic failure category. Providers map their transport errors onto this so the UI can
/// offer a remedy without knowing which site produced the failure.
/// </summary>
public enum RemoteErrorKind
{
    Unknown,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    KeyExpired,
    PremiumRequired,
    AccountMismatch,
    Network,
    Server
}

public sealed class RemoteSiteException : Exception
{
    public RemoteSiteException(
        RemoteErrorKind kind,
        string message,
        string? siteId = null,
        RemoteRateLimit? rateLimit = null,
        DateTimeOffset? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        SiteId = siteId;
        RateLimit = rateLimit;
        RetryAfter = retryAfter;
    }

    public RemoteErrorKind Kind { get; }
    public string? SiteId { get; }
    public RemoteRateLimit? RateLimit { get; }
    public DateTimeOffset? RetryAfter { get; }

    /// <summary>Short, user-facing next step. The message says what happened; this says what to do.</summary>
    public string Remedy => Kind switch
    {
        RemoteErrorKind.Unauthorized => "Add or re-validate the API key for this site in Settings.",
        RemoteErrorKind.Forbidden => "Your account is not allowed to perform this request.",
        RemoteErrorKind.NotFound => "The mod or file no longer exists on the site.",
        RemoteErrorKind.RateLimited => RetryAfter is { } reset
            ? $"The site's request limit is used up. It resets {reset.ToLocalTime():g}."
            : "The site's request limit is used up. Try again later.",
        RemoteErrorKind.KeyExpired => "The download link has expired. Start the download again from the mod page.",
        RemoteErrorKind.PremiumRequired => "Start this download from the mod page using its download-with-manager button.",
        RemoteErrorKind.AccountMismatch => "The link was created for a different account. Sign in as that account or get a new link.",
        RemoteErrorKind.Network => "Check your connection and try again.",
        RemoteErrorKind.Server => "The site is having problems. Try again later.",
        _ => "Try again, or check the site's status."
    };
}
