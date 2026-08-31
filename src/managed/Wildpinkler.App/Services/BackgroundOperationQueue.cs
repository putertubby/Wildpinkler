using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

public sealed class BackgroundOperationQueue : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _operations = Channel.CreateUnbounded<Func<Task>>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _pending;

    public BackgroundOperationQueue() => _worker = ProcessAsync();

    public int PendingCount => Volatile.Read(ref _pending);
    public event EventHandler? Changed;

    public void Enqueue(Func<Task> operation)
    {
        Interlocked.Increment(ref _pending);
        _operations.Writer.TryWrite(operation);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var operation in _operations.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    await operation();
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _operations.Writer.TryComplete();
        _shutdown.Cancel();
        try { await _worker; } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
