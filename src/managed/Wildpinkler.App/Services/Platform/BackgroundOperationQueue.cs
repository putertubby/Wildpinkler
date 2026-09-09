using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wildpinkler.App.Services;

public sealed class BackgroundOperationQueue : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _operations = Channel.CreateUnbounded<Func<Task>>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger<BackgroundOperationQueue> _logger;
    private readonly Task _worker;
    private int _pending;

    public BackgroundOperationQueue(ILogger<BackgroundOperationQueue>? logger = null)
    {
        _logger = logger ?? NullLogger<BackgroundOperationQueue>.Instance;
        _worker = ProcessAsync();
    }

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
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // One failed operation must not take the worker down with it: the queue is shared
                    // by every background job for the lifetime of the app.
                    _logger.LogError(exception, "A queued background operation failed.");
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
