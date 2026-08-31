using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote.Nexus;

/// <summary>
/// Client-side request budget mirroring the behaviour Nexus expects: a burst allowance that only
/// recovers one request per second, so a loop cannot sustain high traffic even when the server
/// would still answer. Tampering with this is explicitly discouraged by the acceptable use policy.
/// </summary>
internal sealed class NexusThrottle
{
    private const int FreeCapacity = 300;
    private const int PremiumCapacity = 600;
    private const double RecoveryPerSecond = 1d;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private int _capacity = FreeCapacity;
    private double _tokens = FreeCapacity;
    private DateTimeOffset _lastRefill;

    public NexusThrottle(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastRefill = _time.GetUtcNow();
    }

    public void SetPremium(bool isPremium)
    {
        var capacity = isPremium ? PremiumCapacity : FreeCapacity;
        _gate.Wait();
        try
        {
            _capacity = capacity;
            _tokens = Math.Min(_tokens, capacity);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Refill();
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return;
            }

            delay = TimeSpan.FromSeconds((1 - _tokens) / RecoveryPerSecond);
            _tokens -= 1;
        }
        finally
        {
            _gate.Release();
        }

        await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
    }

    private void Refill()
    {
        var now = _time.GetUtcNow();
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
            return;
        _lastRefill = now;
        _tokens = Math.Min(_capacity, _tokens + (elapsed * RecoveryPerSecond));
    }
}
