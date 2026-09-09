using System;
using System.Threading;

namespace Wildpinkler.App.Agent;

/// <summary>Bounds provider requests made by remote tools during one user turn.</summary>
public sealed class RemoteCallBudget
{
    public const int MaxCallsPerTurn = 3;

    private int _used;

    public void BeginTurn() => Interlocked.Exchange(ref _used, 0);

    public bool TryConsume() => Interlocked.Increment(ref _used) <= MaxCallsPerTurn;
}
