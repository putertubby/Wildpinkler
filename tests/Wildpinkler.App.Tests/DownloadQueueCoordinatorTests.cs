using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DownloadQueueCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-download-queue-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _httpClient = new();
    private readonly ModStore _store;
    private readonly RemoteArchiveAcquisitionService _acquisition;

    public DownloadQueueCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ModStore(_root);

        var registry = new RemoteSiteRegistry();
        registry.Register(new FakeRemoteSiteProvider());
        _acquisition = new RemoteArchiveAcquisitionService(
            registry,
            WaitForCancellationAsync,
            new ArchiveDownloadService(_httpClient),
            _store);
    }

    [Fact]
    public async Task Enqueue_IdenticalExactFile_ReturnsExistingJob()
    {
        var coordinator = CreateCoordinator();
        try
        {
            var link = RemoteLink.ForModFile("fake", "testgame", "1", "2");

            var first = coordinator.Enqueue(link);
            var second = coordinator.Enqueue(link);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Single(coordinator.Jobs);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    [Fact]
    public async Task Enqueue_DifferentFileKeys_CreatesSeparateJobs()
    {
        var coordinator = CreateCoordinator();
        try
        {
            var first = coordinator.Enqueue(RemoteLink.ForModFile("fake", "testgame", "1", "2"));
            var second = coordinator.Enqueue(RemoteLink.ForModFile("fake", "testgame", "1", "3"));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.Equal(2, coordinator.Jobs.Count);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    [Fact]
    public async Task Enqueue_SiteAndGameCaseDiffer_ReturnsExistingJob()
    {
        var coordinator = CreateCoordinator();
        try
        {
            var first = coordinator.Enqueue(RemoteLink.ForModFile("FAKE", "TESTGAME", "1", "2"));
            var second = coordinator.Enqueue(RemoteLink.ForModFile("fake", "testgame", "1", "2"));

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Single(coordinator.Jobs);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    [Theory]
    [InlineData("Mod", "mod", "2", "2")]
    [InlineData("1", "1", "File", "file")]
    public async Task Enqueue_ModOrFileCaseDiffers_CreatesSeparateJobs(
        string firstModKey,
        string secondModKey,
        string firstFileKey,
        string secondFileKey)
    {
        var coordinator = CreateCoordinator();
        try
        {
            var first = coordinator.Enqueue(RemoteLink.ForModFile("fake", "testgame", firstModKey, firstFileKey));
            var second = coordinator.Enqueue(RemoteLink.ForModFile("fake", "testgame", secondModKey, secondFileKey));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.Equal(2, coordinator.Jobs.Count);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    [Fact]
    public async Task Enqueue_DifferentConfirmedGames_ReturnsExistingJob()
    {
        var coordinator = CreateCoordinator();
        try
        {
            var link = RemoteLink.ForModFile("fake", "testgame", "1", "2");

            var first = coordinator.Enqueue(link, ["game-a"]);
            var second = coordinator.Enqueue(link, ["game-b"]);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(["game-a"], first.ConfirmedGameIds);
            Assert.Single(coordinator.Jobs);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    [Fact]
    public async Task Enqueue_IdenticalUnresolvedMod_ReturnsExistingJob()
    {
        var coordinator = CreateCoordinator();
        try
        {
            var first = coordinator.Enqueue(RemoteLink.ForMod("fake", "testgame", "1"));
            var second = coordinator.Enqueue(RemoteLink.ForMod("fake", "testgame", "1"));

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Single(coordinator.Jobs);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    [Fact]
    public async Task Enqueue_AfterCancellation_CreatesNewJob()
    {
        var coordinator = CreateCoordinator();
        try
        {
            var link = RemoteLink.ForModFile("fake", "testgame", "1", "2");
            var first = coordinator.Enqueue(link);
            Assert.NotNull(first);

            var cancelled = WaitForTerminalAsync(first);
            first.Cancellation.Cancel();
            await cancelled;

            var second = coordinator.Enqueue(link);

            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.Equal(2, coordinator.Jobs.Count);
        }
        finally
        {
            await CancelActiveAsync(coordinator);
        }
    }

    private DownloadQueueCoordinator CreateCoordinator() => new(_acquisition);

    private static async Task<(RemoteCredential Credential, RemoteAccount Account)> WaitForCancellationAsync(
        IRemoteSiteProvider provider,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The authentication gate should only end through cancellation.");
    }

    private static async Task CancelActiveAsync(DownloadQueueCoordinator coordinator)
    {
        var active = coordinator.Jobs.Where(job => job.IsActive).ToList();
        var completions = active.Select(WaitForTerminalAsync).ToList();
        foreach (var job in active)
            job.Cancellation.Cancel();
        await Task.WhenAll(completions);
    }

    private static async Task WaitForTerminalAsync(DownloadJob job)
    {
        if (!job.IsActive)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadJob.State) && !job.IsActive)
                completion.TrySetResult();
        };
        job.PropertyChanged += handler;
        try
        {
            if (!job.IsActive)
                return;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            job.PropertyChanged -= handler;
        }
    }

    public void Dispose()
    {
        _acquisition.Dispose();
        _store.Dispose();
        _httpClient.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}