using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum ActiveRunState
{
    Reserving,
    Running
}

/// <summary>One loader-backed target currently reserved or running for a profile.</summary>
public sealed record ActiveRun(
    string ProfileId,
    string TargetId,
    string TargetName,
    LaunchTargetKind TargetKind,
    DateTimeOffset StartedAt,
    ActiveRunState State);

/// <summary>
/// Owns the in-memory reservation for loader-backed profile runs. A profile can have exactly one
/// reservation so launch setup and target execution cannot overlap with another profile mutation.
/// </summary>
public sealed class ActiveRunRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveRun> _runs = new(StringComparer.Ordinal);

    public event EventHandler<ActiveRun>? RunStarted;
    public event EventHandler<ActiveRun>? RunEnded;

    public bool TryReserve(Profile profile, LaunchTarget target, out ActiveRun run)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_runs.TryGetValue(profile.Id, out run!))
                return false;

            run = new ActiveRun(
                profile.Id,
                target.Id,
                target.DisplayName,
                target.Kind,
                DateTimeOffset.UtcNow,
                ActiveRunState.Reserving);
            _runs.Add(profile.Id, run);
            return true;
        }
    }

    public bool MarkRunning(ActiveRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        ActiveRun running;
        lock (_gate)
        {
            if (!_runs.TryGetValue(run.ProfileId, out var current) || current != run)
                return false;

            running = current with { State = ActiveRunState.Running };
            _runs[run.ProfileId] = running;
        }

        RunStarted?.Invoke(this, running);
        return true;
    }

    public bool Release(ActiveRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        ActiveRun ended;
        lock (_gate)
        {
            if (!_runs.TryGetValue(run.ProfileId, out var current) ||
                current.ProfileId != run.ProfileId ||
                current.TargetId != run.TargetId ||
                current.StartedAt != run.StartedAt)
                return false;

            _runs.Remove(run.ProfileId);
            ended = current;
        }

        RunEnded?.Invoke(this, ended);
        return true;
    }

    public bool HasRun(string profileId)
    {
        lock (_gate)
            return _runs.ContainsKey(profileId);
    }

    public bool HasRun(string profileId, string targetId)
    {
        lock (_gate)
            return _runs.TryGetValue(profileId, out var run) && string.Equals(run.TargetId, targetId, StringComparison.Ordinal);
    }

    public bool TryGetRun(string profileId, out ActiveRun? run)
    {
        lock (_gate)
            return _runs.TryGetValue(profileId, out run);
    }
}