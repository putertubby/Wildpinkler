using System;

namespace Wildpinkler.Remote;

/// <summary>Last known request budget reported by a site, used to warn before requests start failing.</summary>
public sealed record RemoteRateLimit(
    int? HourlyRemaining = null,
    int? DailyRemaining = null,
    DateTimeOffset? HourlyReset = null,
    DateTimeOffset? DailyReset = null)
{
    public static RemoteRateLimit Unknown { get; } = new();

    public bool IsExhausted => HourlyRemaining is <= 0 || DailyRemaining is <= 0;

    /// <summary>When the exhausted budget recovers, or null when nothing is exhausted.</summary>
    public DateTimeOffset? NextReset => (HourlyRemaining is <= 0, DailyRemaining is <= 0) switch
    {
        (true, true) => Earliest(HourlyReset, DailyReset),
        (true, false) => HourlyReset,
        (false, true) => DailyReset,
        _ => null
    };

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left < right ? left : right;
}
