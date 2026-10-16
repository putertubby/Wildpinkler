using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        Assert.Equal(0, completion.ExitCode);
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
        Assert.Equal(0, completion.ExitCode);
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
        Assert.Equal(1, completion.ExitCode);
        Assert.Equal(1, fixture.Binding.OutputVersion);
        Assert.True(Directory.Exists(ProfileFolderService.GetToolOutputFolder(fixture.Profile, fixture.Target.Id, 2)));
        Assert.False(fixture.Runs.HasRun(fixture.Profile.Id));
    }

    [Fact]
    public async Task LaunchAsync_WhenLauncherStartThrows_AbandonsPendingOutputAndReleasesProfile()
    {
        var fixture = CreateFixture(throwOnStart: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.LaunchAsync(fixture.Profile, fixture.Target));

        Assert.Equal(1, fixture.Binding.OutputVersion);
        Assert.False(Directory.Exists(ProfileFolderService.GetToolOutputFolder(fixture.Profile, fixture.Target.Id, 2)));
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

    [Fact]
    public async Task LaunchAndWaitAsync_SettingsOnlySorter_SetsSortedFlag()
    {
        // Regression: a plugin-sorting tool that produces no output (e.g. LOOT) has no binding or
        // pending run, yet it must still clear the "unsorted" warning by setting the sorted flag.
        var (service, profile, target, process, game) = await RunToolWithStores(sorter: true);
        await RunToCompletion(service, profile, target, process, game);

        var stored = (await new ProfileStore(_root).LoadAsync()).Single(item => item.Id == profile.Id);
        Assert.True(stored.PluginListSorted);
    }

    [Fact]
    public async Task LaunchAndWaitAsync_NonSorterTool_DoesNotSetSortedFlag()
    {
        var (service, profile, target, process, game) = await RunToolWithStores(sorter: false);
        await RunToCompletion(service, profile, target, process, game);

        var stored = (await new ProfileStore(_root).LoadAsync()).Single(item => item.Id == profile.Id);
        Assert.False(stored.PluginListSorted);
    }

    private async Task<(LaunchService Service, Profile Profile, LaunchTarget Target, FakeProcess Process, GameEntry Game)> RunToolWithStores(bool sorter)
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "tool.exe");
        var loader = Path.Combine(_root, LaunchService.LoaderFileName);
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(loader, string.Empty);

        // The profile and game definition both declare a plugin list, which is the precondition for
        // the sorted flag to be considered at all.
        var profile = new Profile { Id = "profile", Name = "Profile", FolderPath = Path.Combine(_root, "profile") };
        Directory.CreateDirectory(profile.FolderPath);
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool", IsEnabled = true });
        var game = new GameEntry
        {
            Id = "game",
            InstallPath = _root,
            Definition = new GameDefinition
            {
                DefinitionId = "test-game",
                Name = "Test Game",
                PluginList = new GamePluginList { ListPath = Path.Combine(_root, "localappdata", "plugins.txt") }
            }
        };
        await new ProfileStore(_root).SaveAsync(new[] { profile });

        // The tool entry's definition is display-only (not persisted); the sorter flag lives in the
        // definition catalog, which is what LaunchService consults after a run completes.
        var toolDefinitionStore = new ToolDefinitionStore(Path.Combine(_root, "tool-definitions"));
        var definitionDirectory = toolDefinitionStore.UserDirectory;
        Directory.CreateDirectory(definitionDirectory);
        var definitionJson =
            """
            {
              "schemaVersion": 2,
              "definitionId": "test-sorter",
              "name": "Test Sorter",
              "definitionVersion": 1,
              "author": "Wildpinkler",
              "description": "Test tool for sorted-flag behavior.",
              "sortsPluginList": __sorter__,
              "executableRelativePath": "tool.exe",
              "detectionMarkers": [
                "tool.exe"
              ]
            }
            """
            .Replace("__sorter__", sorter ? "true" : "false");
        File.WriteAllText(Path.Combine(definitionDirectory, "test-sorter.wptool.json"), definitionJson);

        var toolStore = new ToolStore(_root);
        await toolStore.SaveAsync(new[]
        {
            new ToolEntry
            {
                Id = "tool",
                InstallPath = _root,
                DefinitionId = "test-sorter",
                DefinitionVersion = 1
            }
        });

        // A settings-only tool (definition-backed tools never capture) means the page never
        // creates a binding, so target.Id ("tool") is the only handle to the tool.
        var target = new LaunchTarget(
            "tool", "Tool", LaunchTargetKind.Tool, executable, string.Empty, _root, executable, _root,
            Array.Empty<MergedView>(), new Dictionary<string, string>(), Array.Empty<string>(), "profile.tool.json", false, string.Empty,
            null, string.Empty, string.Empty);
        var process = new FakeProcess();
        var service = new LaunchService(
            new ProfileConfigExporter(), new ProfileFolderService(_root), new ActiveRunRegistry(),
            new FakeLauncher(process, throwOnStart: false), new AppSettings(), loader,
            pluginsTxt: null, toolStore: toolStore, toolDefinitionStore: toolDefinitionStore, profileStore: new ProfileStore(_root));

        return (service, profile, target, process, game);
    }

    private static async Task RunToCompletion(LaunchService service, Profile profile, LaunchTarget target, FakeProcess process, GameEntry game)
    {
        var completionTask = service.LaunchAndWaitAsync(profile, target, game);
        process.Exit(0);
        var completion = await completionTask.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(completion.Succeeded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture(bool throwOnStart = false, ILogger<LaunchService>? logger = null)
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
            Array.Empty<MergedView>(), new Dictionary<string, string>(), Array.Empty<string>(), "profile.tool.json", true, string.Empty,
            null, string.Empty, string.Empty);
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process, throwOnStart);
        var runs = new ActiveRunRegistry();
        var service = new LaunchService(new ProfileConfigExporter(), new ProfileFolderService(_root), runs, launcher, new AppSettings(), loader, logger);
        return new Fixture(service, profile, binding, target, process, launcher, runs, _root, loader, executable);
    }

    [Fact]
    public async Task CreateStartInfo_PassesVirtualExecutablePathAsTargetArgument()
    {
        Directory.CreateDirectory(_root);
        var realExecutable = Path.Combine(_root, "loaders", "loader.exe");
        var virtualExecutable = @"C:\games\test\loaders\loader.exe";
        Directory.CreateDirectory(Path.GetDirectoryName(realExecutable)!);
        File.WriteAllText(realExecutable, string.Empty);
        var loader = Path.Combine(_root, LaunchService.LoaderFileName);
        File.WriteAllText(loader, string.Empty);

        var profile = new Profile { Id = "profile", Name = "Profile", FolderPath = Path.Combine(_root, "profile") };
        Directory.CreateDirectory(profile.FolderPath);
        // Models the mod-launcher case: the real executable sits in the mod's folder, but through the
        // VFS the game sees it at InstallPath + relative path. The --target argument must carry the
        // virtual path, not the real one.
        var target = new LaunchTarget(
            "game", "Game", LaunchTargetKind.Game, realExecutable, string.Empty, Path.GetDirectoryName(realExecutable)!,
            virtualExecutable, @"C:\games\test\loaders",
            Array.Empty<MergedView>(), new Dictionary<string, string>(), Array.Empty<string>(), "profile.json", false, string.Empty,
            null, string.Empty, string.Empty);
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process, throwOnStart: false);
        var service = new LaunchService(new ProfileConfigExporter(), new ProfileFolderService(_root), new ActiveRunRegistry(), launcher, new AppSettings(), loader);

        var completionTask = service.LaunchAndWaitAsync(profile, target);
        process.Exit(0);
        var completion = await completionTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(completion.Succeeded);
        Assert.NotNull(launcher.LastArguments);
        var indexOfTarget = launcher.LastArguments!.IndexOf("--target");
        Assert.True(indexOfTarget >= 0, "--target flag was not passed.");
        Assert.Equal(virtualExecutable, launcher.LastArguments[indexOfTarget + 1]);
        Assert.DoesNotContain(realExecutable, launcher.LastArguments);

        // --curdir immediately follows the --target value and carries the virtual directory of the target.
        var indexOfCurdir = launcher.LastArguments.IndexOf("--curdir");
        Assert.True(indexOfCurdir == indexOfTarget + 2, "--curdir must follow the --target value.");
        Assert.Equal(@"C:\games\test\loaders", launcher.LastArguments[indexOfCurdir + 1]);

        // The loader process must not be given an explicit working directory.
        Assert.NotNull(launcher.LastStartInfo);
        Assert.Equal(string.Empty, launcher.LastStartInfo!.WorkingDirectory);
    }

    [Fact]
    public async Task CreateStartInfo_PassesCurdirAsDirectoryOfNestedVirtualExecutable()
    {
        Directory.CreateDirectory(_root);
        var realExecutable = Path.Combine(_root, "bin", "game.exe");
        var virtualExecutable = @"C:\games\test\bin\game.exe";
        Directory.CreateDirectory(Path.GetDirectoryName(realExecutable)!);
        File.WriteAllText(realExecutable, string.Empty);
        var loader = Path.Combine(_root, LaunchService.LoaderFileName);
        File.WriteAllText(loader, string.Empty);

        var profile = new Profile { Id = "profile", Name = "Profile", FolderPath = Path.Combine(_root, "profile") };
        Directory.CreateDirectory(profile.FolderPath);
        var target = new LaunchTarget(
            "game", "Game", LaunchTargetKind.Game, realExecutable, string.Empty, Path.GetDirectoryName(realExecutable)!,
            virtualExecutable, @"C:\games\test\bin",
            Array.Empty<MergedView>(), new Dictionary<string, string>(), Array.Empty<string>(), "profile.json", false, string.Empty,
            null, string.Empty, string.Empty);
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process, throwOnStart: false);
        var service = new LaunchService(new ProfileConfigExporter(), new ProfileFolderService(_root), new ActiveRunRegistry(), launcher, new AppSettings(), loader);

        var completionTask = service.LaunchAndWaitAsync(profile, target);
        process.Exit(0);
        await completionTask.WaitAsync(TestContext.Current.CancellationToken);

        var indexOfCurdir = launcher.LastArguments!.IndexOf("--curdir");
        Assert.True(indexOfCurdir >= 0, "--curdir flag was not passed.");
        Assert.Equal(@"C:\games\test\bin", launcher.LastArguments[indexOfCurdir + 1]);
    }

    [Fact]
    public async Task CreateStartInfo_PassesArgsAndSteamIdWhenPresent()
    {
        var (launcher, _) = await StartTargetWithFlags("--mod load -v2", "114440");

        var indexOfArgs = launcher.LastArguments!.IndexOf("--args");
        Assert.True(indexOfArgs >= 0, "--args flag was not passed.");
        Assert.Equal("--mod load -v2", launcher.LastArguments[indexOfArgs + 1]);

        var indexOfSteamId = launcher.LastArguments.IndexOf("--steamid");
        Assert.True(indexOfSteamId >= 0, "--steamid flag was not passed.");
        Assert.Equal("114440", launcher.LastArguments[indexOfSteamId + 1]);
    }

    [Fact]
    public async Task CreateStartInfo_OmitsArgsAndSteamIdWhenEmpty()
    {
        var (launcher, _) = await StartTargetWithFlags(string.Empty, string.Empty);

        Assert.DoesNotContain("--args", launcher.LastArguments!);
        Assert.DoesNotContain("--steamid", launcher.LastArguments!);
    }

    [Fact]
    public async Task CreateStartInfo_KeepsArgumentValuesContainingSpacesAsDiscreteEntries()
    {
        var (launcher, _) = await StartTargetWithFlags("-load \"mod one\" -extra", string.Empty);

        // Each ArgumentList entry must reach the process as exactly one discrete argument;
        // a naive space-joined command line would split the -args payload apart.
        Assert.Contains("-load \"mod one\" -extra", launcher.LastArguments!);

        var indexOfArgs = launcher.LastArguments!.IndexOf("--args");
        Assert.True(indexOfArgs >= 0, "--args flag was not passed.");
        Assert.Equal("-load \"mod one\" -extra", launcher.LastArguments[indexOfArgs + 1]);
    }

    [Fact]
    public async Task CreateStartInfo_PassesDefaultLogLevelAndLogPipe()
    {
        var (launcher, _) = await StartTargetWithFlags(string.Empty, string.Empty, new AppSettings());

        var indexLevel = launcher.LastArguments!.IndexOf("--loglevel");
        Assert.True(indexLevel >= 0, "--loglevel flag was not passed.");
        Assert.Equal("WARN", launcher.LastArguments[indexLevel + 1]);

        var indexPipe = launcher.LastArguments.IndexOf("--logpipe");
        Assert.True(indexPipe >= 0, "--logpipe flag was not passed.");
        Assert.Equal(@"\\.\pipe\uufs64log", launcher.LastArguments[indexPipe + 1]);
    }

    [Fact]
    public async Task CreateStartInfo_PassesConfiguredLogLevelAndLogPipe()
    {
        var settings = new AppSettings { VfsLogLevel = VfsLogLevel.Trace, VfsLogPipe = @"\\.\pipe\custompipe" };
        var (launcher, _) = await StartTargetWithFlags(string.Empty, string.Empty, settings);

        var indexLevel = launcher.LastArguments!.IndexOf("--loglevel");
        Assert.True(indexLevel >= 0, "--loglevel flag was not passed.");
        Assert.Equal("TRACE", launcher.LastArguments[indexLevel + 1]);

        var indexPipe = launcher.LastArguments.IndexOf("--logpipe");
        Assert.True(indexPipe >= 0, "--logpipe flag was not passed.");
        Assert.Equal(@"\\.\pipe\custompipe", launcher.LastArguments[indexPipe + 1]);
    }

    [Fact]
    public async Task CreateStartInfo_FallsBackToDefaultLogPipeWhenBlank()
    {
        var settings = new AppSettings { VfsLogLevel = VfsLogLevel.Fatal, VfsLogPipe = "   " };
        var (launcher, _) = await StartTargetWithFlags(string.Empty, string.Empty, settings);

        var indexLevel = launcher.LastArguments!.IndexOf("--loglevel");
        Assert.Equal("FATAL", launcher.LastArguments[indexLevel + 1]);

        var indexPipe = launcher.LastArguments.IndexOf("--logpipe");
        Assert.Equal(@"\\.\pipe\uufs64log", launcher.LastArguments[indexPipe + 1]);
    }

    [Fact]
    public async Task CreateScope_ScopesInvocationAndCompletionLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wp-launch-scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var provider = new Wildpinkler.App.Services.Diagnostics.RollingFileLoggerProvider(dir, new Wildpinkler.App.Services.Diagnostics.LogLevelAccessor());
        try
        {
            var fixture = CreateFixture(logger: new DelegateLogger(provider.CreateLogger("LaunchService")));

            var completionTask = fixture.Service.LaunchAndWaitAsync(fixture.Profile, fixture.Target);
            fixture.Process.Exit(0);
            await completionTask.WaitAsync(TestContext.Current.CancellationToken);

            // Close the writer first so the log files are no longer locked.
            provider.Dispose();
            var lines = new List<string>();
            foreach (var file in new DirectoryInfo(dir).GetFiles("wildpinkler-*.log"))
                lines.AddRange(File.ReadAllLines(file.FullName));

            var startLine = Assert.Single(lines, line => line.Contains("Starting 'Profile'"));
            var commandLineLine = Assert.Single(lines, line => line.Contains("Loader command line:"));
            var completionLine = Assert.Single(lines, line => line.Contains("exited with code 0"));

            // All three lines are correlated by the same 8-hex run id opened by the service.
            var runIdPattern = @"\[run ([0-9a-f]{8})\]";
            var startPrefix = Regex.Match(startLine, runIdPattern);
            Assert.True(startPrefix.Success, $"Start line is missing the [run <8-hex>] prefix: {startLine}");
            Assert.Equal(startPrefix.Value, Regex.Match(commandLineLine, runIdPattern).Value);
            Assert.Equal(startPrefix.Value, Regex.Match(completionLine, runIdPattern).Value);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    private async Task<(FakeLauncher Launcher, ILaunchedProcess Process)> StartTargetWithFlags(string arguments, string steamId, AppSettings? settings = null)
    {
        Directory.CreateDirectory(_root);
        var realExecutable = Path.Combine(_root, "bin", "game.exe");
        var virtualExecutable = @"C:\games\test\bin\game.exe";
        Directory.CreateDirectory(Path.GetDirectoryName(realExecutable)!);
        File.WriteAllText(realExecutable, string.Empty);
        var loader = Path.Combine(_root, LaunchService.LoaderFileName);
        File.WriteAllText(loader, string.Empty);

        var profile = new Profile { Id = "profile", Name = "Profile", FolderPath = Path.Combine(_root, "profile") };
        Directory.CreateDirectory(profile.FolderPath);
        var target = new LaunchTarget(
            "game", "Game", LaunchTargetKind.Game, realExecutable, arguments, Path.GetDirectoryName(realExecutable)!,
            virtualExecutable, @"C:\games\test\bin",
            Array.Empty<MergedView>(), new Dictionary<string, string>(), Array.Empty<string>(), "profile.json", false, steamId,
            null, string.Empty, string.Empty);
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process, throwOnStart: false);
        var service = new LaunchService(new ProfileConfigExporter(), new ProfileFolderService(_root), new ActiveRunRegistry(), launcher, settings ?? new AppSettings(), loader);

        var completionTask = service.LaunchAndWaitAsync(profile, target);
        process.Exit(0);
        await completionTask.WaitAsync(TestContext.Current.CancellationToken);

        return (launcher, process);
    }

    private sealed record Fixture(
        LaunchService Service,
        Profile Profile,
        ProfileTool Binding,
        LaunchTarget Target,
        FakeProcess Process,
        FakeLauncher Launcher,
        ActiveRunRegistry Runs,
        string Root,
        string Loader,
        string Executable);

    private sealed class FakeLauncher(FakeProcess process, bool throwOnStart) : IProcessLauncher
    {
        public int StartCount { get; private set; }
        public List<string>? LastArguments { get; private set; }
        public ProcessStartInfo? LastStartInfo { get; private set; }

        public ILaunchedProcess Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            LastStartInfo = startInfo;
            LastArguments = new List<string>(startInfo.ArgumentList);
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

    private sealed class DelegateLogger(ILogger inner) : ILogger<LaunchService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}