using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class LaunchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-launch-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LaunchAndWaitAsync_ReturnsFinalCompletionAfterOutputPromotion()
    {
        var fixture = CreateFixture();

        var completionTask = fixture.Service.LaunchAndWaitAsync(fixture.Profile, fixture.Target);
        Assert.False(completionTask.IsCompleted);
        fixture.Process.Exit(0);
        var completion = await completionTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(completion.Succeeded);
        Assert.Equal(2, fixture.Binding.OutputVersion);
        Assert.False(fixture.Runs.HasRun(fixture.Profile.Id));
    }

    [Fact]
    public async Task LaunchAsync_WhenToolSucceeds_PromotesPendingOutputAfterLoaderExit()
    {
        var fixture = CreateFixture();
        LaunchCompletion? completion = null;
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.LaunchCompleted += result =>
        {
            completion = result;
            completionSource.SetResult();
        };

        await fixture.Service.LaunchAsync(fixture.Profile, fixture.Target);

        Assert.True(fixture.Runs.HasRun(fixture.Profile.Id, fixture.Target.Id));
        Assert.Equal(1, fixture.Binding.OutputVersion);
        fixture.Process.Exit(0);
        await completionSource.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.True(completion!.Succeeded);
        Assert.Equal(2, fixture.Binding.OutputVersion);
        Assert.False(fixture.Runs.HasRun(fixture.Profile.Id));
    }

    [Fact]
    public async Task LaunchAsync_WhenLoaderFails_DoesNotPromoteToolOutput()
    {
        var fixture = CreateFixture();
        var completionSource = new TaskCompletionSource<LaunchCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.LaunchCompleted += result => completionSource.SetResult(result);

        await fixture.Service.LaunchAsync(fixture.Profile, fixture.Target);
        fixture.Process.Exit(1);
        var completion = await completionSource.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(completion.Succeeded);
        Assert.Equal(1, fixture.Binding.OutputVersion);
        Assert.True(Directory.Exists(ProfileFolderProvisioner.GetToolOutputFolder(fixture.Profile, fixture.Target.Id, 2)));
        Assert.False(fixture.Runs.HasRun(fixture.Profile.Id));
    }

    [Fact]
    public async Task LaunchAsync_WhenLauncherStartThrows_AbandonsPendingOutputAndReleasesProfile()
    {
        var fixture = CreateFixture(throwOnStart: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.LaunchAsync(fixture.Profile, fixture.Target));

        Assert.Equal(1, fixture.Binding.OutputVersion);
        Assert.False(Directory.Exists(ProfileFolderProvisioner.GetToolOutputFolder(fixture.Profile, fixture.Target.Id, 2)));
        Assert.False(fixture.Runs.HasRun(fixture.Profile.Id));
    }

    [Fact]
    public async Task LaunchAsync_WhenProfileAlreadyRunning_RejectsBeforeStartingAnotherLoader()
    {
        var fixture = CreateFixture();
        await fixture.Service.LaunchAsync(fixture.Profile, fixture.Target);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.LaunchAsync(fixture.Profile, fixture.Target));

        Assert.Equal(1, fixture.Launcher.StartCount);
        fixture.Process.Exit(0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture(bool throwOnStart = false)
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "tool.exe");
        var loader = Path.Combine(_root, LaunchService.LoaderFileName);
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(loader, string.Empty);

        var profile = new Profile { Id = "profile", Name = "Profile", FolderPath = Path.Combine(_root, "profile") };
        Directory.CreateDirectory(profile.FolderPath);
        var binding = new ProfileTool { ToolEntryId = "tool", IsEnabled = true, OutputVersion = 1 };
        profile.Tools.Add(binding);
        var target = new LaunchTarget(
            "tool", "Tool", LaunchTargetKind.Tool, executable, string.Empty, _root, executable, _root,
            Array.Empty<MergedView>(), new Dictionary<string, string>(), Array.Empty<string>(), "profile.tool.json", true, string.Empty);
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process, throwOnStart);
        var runs = new ActiveRunRegistry();
        var service = new LaunchService(new ProfileConfigExporter(), new ProfileFolderProvisioner(_root), runs, launcher, loader);
        return new Fixture(service, profile, binding, target, process, launcher, runs);
    }

    private sealed record Fixture(
        LaunchService Service,
        Profile Profile,
        ProfileTool Binding,
        LaunchTarget Target,
        FakeProcess Process,
        FakeLauncher Launcher,
        ActiveRunRegistry Runs);

    private sealed class FakeLauncher(FakeProcess process, bool throwOnStart) : IProcessLauncher
    {
        public int StartCount { get; private set; }

        public ILaunchedProcess Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            if (throwOnStart)
                throw new InvalidOperationException("Unable to start loader.");
            return process;
        }
    }

    private sealed class FakeProcess : ILaunchedProcess
    {
        private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> WaitForExitAsync() => _exitCode.Task;

        public void Exit(int exitCode) => _exitCode.SetResult(exitCode);

        public void Dispose()
        {
        }
    }
}