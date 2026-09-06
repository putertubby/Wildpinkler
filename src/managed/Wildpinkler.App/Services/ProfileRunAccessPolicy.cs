using System;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>Determines whether an operation may change a profile's effective mounted state.</summary>
public sealed class ProfileRunAccessPolicy
{
    private readonly ActiveRunRegistry _runs;

    public ProfileRunAccessPolicy(ActiveRunRegistry runs) => _runs = runs;

    public bool CanModify(Profile? profile) => profile is not null && !_runs.HasRun(profile.Id);

    public void EnsureCanModify(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (CanModify(profile))
            return;

        var target = _runs.TryGetRun(profile.Id, out var run) ? $" ({run!.TargetName})" : string.Empty;
        throw new InvalidOperationException($"'{profile.Name}' cannot be changed while a target is running{target}.");
    }
}