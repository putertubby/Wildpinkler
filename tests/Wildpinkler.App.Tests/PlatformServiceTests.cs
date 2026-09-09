using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ExecutableScanServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-exescan-" + Guid.NewGuid().ToString("N"));

    public ExecutableScanServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Scan_ReturnsExecutablesRelativeToTheRoot()
    {
        File.WriteAllText(Path.Combine(_root, "game.exe"), string.Empty);
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "tool.exe"), string.Empty);

        var results = ExecutableScanService.Scan(_root);

        Assert.Contains("game.exe", results, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(results, item => item.EndsWith("tool.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, item => Path.IsPathRooted(item));
    }

    [Fact]
    public void Scan_IgnoresNonExecutableFiles()
    {
        File.WriteAllText(Path.Combine(_root, "readme.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "data.bsa"), string.Empty);

        Assert.Empty(ExecutableScanService.Scan(_root));
    }

    [Fact]
    public void Scan_MissingFolder_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(ExecutableScanService.Scan(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Scan_IsBoundedByMaxResults()
    {
        for (var index = 0; index < ExecutableScanService.MaxResults + 25; index++)
            File.WriteAllText(Path.Combine(_root, $"game{index}.exe"), string.Empty);

        Assert.True(ExecutableScanService.Scan(_root).Count <= ExecutableScanService.MaxResults);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

public sealed class BackgroundOperationQueueTests
{
    [Fact]
    public async Task Enqueue_RunsOperationsInOrder()
    {
        await using var queue = new BackgroundOperationQueue();
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(() => { order.Enqueue(1); return Task.CompletedTask; });
        queue.Enqueue(() => { order.Enqueue(2); return Task.CompletedTask; });
        queue.Enqueue(() => { order.Enqueue(3); done.SetResult(); return Task.CompletedTask; });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], order.ToArray());
    }

    [Fact]
    public async Task Enqueue_OneFailingOperation_DoesNotStopTheQueue()
    {
        await using var queue = new BackgroundOperationQueue();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(() => throw new InvalidOperationException("boom"));
        queue.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZeroOnceTheQueueDrains()
    {
        await using var queue = new BackgroundOperationQueue();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.PendingCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Changed_IsRaisedAsWorkIsQueued()
    {
        await using var queue = new BackgroundOperationQueue();
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => raised.TrySetResult();

        queue.Enqueue(() => Task.CompletedTask);

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;

    public AppSettingsStoreTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _root);
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var settings = new AppSettingsStore().Load();

        Assert.NotNull(settings);
        Assert.True(settings.ConfirmRemoteDownloads);
    }

    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        var directory = Path.Combine(_root, "Wildpinkler");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app-settings.json"), "{ not json");

        Assert.NotNull(new AppSettingsStore().Load());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _previousLocalAppData);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
