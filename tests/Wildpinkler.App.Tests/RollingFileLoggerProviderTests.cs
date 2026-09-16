using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wildpinkler.App.Services.Diagnostics;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-logger-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RollingFileLoggerProvider _provider;

    public RollingFileLoggerProviderTests()
    {
        Directory.CreateDirectory(_root);
        _provider = new RollingFileLoggerProvider(_root, new LogLevelAccessor());
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private List<string> ReadLogLines()
    {
        // Close the writer first so the log files are no longer locked.
        _provider.Dispose();
        return new DirectoryInfo(_root).GetFiles("wildpinkler-*.log").SelectMany(file => File.ReadAllLines(file.FullName)).ToList();
    }

    [Fact]
    public void LogMessage_RenderedWithFixedWidthColumns()
    {
        var logger = _provider.CreateLogger("Tests.Foo");
        logger.Log(LogLevel.Information, default, "hello world", null, (state, _) => state!);

        var line = Assert.Single(ReadLogLines());

        // Column 1: ISO-8601 UTC timestamp in round-trip ("O") format.
        var levelIndex = line.IndexOf(' ');
        Assert.True(levelIndex > 0, "Line must start with a timestamp.");
        var timestamp = line[..levelIndex];
        var parsed = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(timestamp, parsed.ToString("O", CultureInfo.InvariantCulture));

        // Column 2: three-character level abbreviation.
        Assert.Equal("inf", line[(levelIndex + 1)..(levelIndex + 4)]);

        // Column 3: short category left-padded to 16 characters.
        var categoryStart = levelIndex + 5;
        Assert.Equal("Foo".PadRight(16), line[categoryStart..(categoryStart + 16)]);

        // The message follows the category column, separated by a single space.
        Assert.Equal(" hello world", line[(categoryStart + 16)..]);
    }

    [Fact]
    public void LogMessage_TrailingPropertyBlockOmitsTemplateProperties()
    {
        var logger = _provider.CreateLogger("Tests.Dedup");
        var state = new KeyValuePair<string, object?>[]
        {
            new("{OriginalFormat}", "Value A is {A}"),
            new("A", "alpha"),
            new("B", "beta"),
        };
        logger.Log(LogLevel.Information, default, state, null, (_, _) => "Value A is alpha");

        var line = Assert.Single(ReadLogLines());

        Assert.Contains("Value A is alpha", line);
        Assert.DoesNotContain("A=", line);
        Assert.Contains(" {B=beta}", line);
    }

    [Fact]
    public void LogMessage_TrailingPropertyBlockEmittedWhenTemplateOmitsProperty()
    {
        var logger = _provider.CreateLogger("Tests.Dedup");
        var state = new KeyValuePair<string, object?>[]
        {
            new("{OriginalFormat}", "plain message"),
            new("B", "beta"),
        };
        logger.Log(LogLevel.Information, default, state, null, (_, _) => "plain message");

        var line = Assert.Single(ReadLogLines());

        Assert.Contains("plain message {B=beta}", line);
    }

    [Fact]
    public void BeginScope_WithRunId_PrefixesLinesWhileActive()
    {
        var logger = _provider.CreateLogger("Tests.Scoped");
        using (logger.BeginScope(new Dictionary<string, object> { ["RunId"] = "7f3a21" }))
        {
            logger.Log(LogLevel.Information, default, "inside scope", null, (state, _) => state!);
        }
        logger.Log(LogLevel.Information, default, "outside scope", null, (state, _) => state!);

        var lines = ReadLogLines();
        var inside = Assert.Single(lines, line => line.Contains("inside scope"));
        var outside = Assert.Single(lines, line => line.Contains("outside scope"));

        Assert.Contains(" [run 7f3a21] inside scope", inside);
        Assert.DoesNotContain("[run", outside);
    }

    [Fact]
    public void BeginScope_WithCorrelationIdDictionary_UsesCmdPrefix()
    {
        var logger = _provider.CreateLogger("Tests.Scoped");
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CommandName"] = "OpenLogFolder",
            ["CorrelationId"] = "abc12345",
        }))
        {
            logger.Log(LogLevel.Information, default, "dispatching", null, (state, _) => state!);
        }

        var line = Assert.Single(ReadLogLines());

        Assert.Contains(" [cmd abc12345] dispatching", line);
    }

    [Fact]
    public async Task BeginScope_AcrossAsyncContinuation_StaysActiveThenClears()
    {
        var logger = _provider.CreateLogger("Tests.Async");
        var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = "99887766" });

        var task = Task.Run(async () =>
        {
            logger.Log(LogLevel.Information, default, "inside continuation", null, (state, _) => state!);
            await Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await task.WaitAsync(TestContext.Current.CancellationToken);

        scope?.Dispose();
        logger.Log(LogLevel.Information, default, "after disposal", null, (state, _) => state!);

        var lines = ReadLogLines();
        var inside = Assert.Single(lines, line => line.Contains("inside continuation"));
        var after = Assert.Single(lines, line => line.Contains("after disposal"));

        Assert.Contains(" [run 99887766] inside continuation", inside);
        Assert.DoesNotContain("[run", after);
    }

    [Fact]
    public void LogMessage_ExceptionTextStillAppendedAndSanitized()
    {
        var logger = _provider.CreateLogger("Tests.Faulty");
        logger.Log(LogLevel.Information, default, "with exception",
            new InvalidOperationException("line one\nline two"), (state, _) => state!);

        var lines = ReadLogLines();
        var entry = Assert.Single(lines, line => line.Contains("with exception"));

        Assert.Contains("System.InvalidOperationException: line one line two", string.Join("\n", lines));
        Assert.DoesNotContain('\n', entry);
    }
}
