using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Commands;

/// <summary>
/// One timed extraction pass: wall and CPU time, the process working set at peak, and the
/// files/bytes actually written.
/// </summary>
public sealed record ExtractionBenchmarkRun(int Run, long WallTimeMs, long CpuTimeMs, long PeakWorkingSetBytes, int FilesWritten, long BytesWritten);

/// <summary>
/// The median of several <see cref="ExtractionBenchmarkRun"/>s plus the output manifest hash,
/// so two runs can be compared on performance AND output identity.
/// </summary>
public sealed record ExtractionBenchmarkReport(
    string ArchivePath,
    string Strategy,
    int Runs,
    long MedianWallTimeMs,
    long MedianCpuTimeMs,
    long MedianPeakWorkingSetBytes,
    long BytesPerSecond,
    string OutputSha256,
    int FilesWritten,
    long BytesWritten,
    IReadOnlyList<ExtractionBenchmarkRun> AllRuns);

/// <summary>
/// Opt-in extraction benchmark: re-extracts the same FOMOD selection N times into fresh staging
/// directories and reports medians. Used to A/B the SharpCompress path against native backends
/// without bundling the real (large) archive - the caller supplies the archive path and the
/// strategy to exercise ("sharpcompress" or "native7z").
/// </summary>
public sealed record BenchmarkExtractionCommand(
    string ArchivePath,
    int RunCount = 3,
    string Strategy = "sharpcompress") : IAppCommand<ExtractionBenchmarkReport>
{
    public BenchmarkExtractionCommand(string archivePath)
        : this(archivePath, 3)
    {
    }
}

/// <summary>
/// Runs <see cref="BenchmarkExtractionCommand"/> against the existing SharpCompress-backed
/// <see cref="ModInstallService"/> extraction path. Selection is "every group, every plugin"
/// so the benchmark always covers the full archive without user input.
/// </summary>
public sealed class BenchmarkExtractionHandler : IAppCommandHandler<BenchmarkExtractionCommand, ExtractionBenchmarkReport>
{
    private readonly IArchiveInspector _inspector;
    private readonly FomodInstallerParser _parser;

    public BenchmarkExtractionHandler(IArchiveInspector inspector, FomodInstallerParser parser)
    {
        _inspector = inspector;
        _parser = parser;
    }

    public async Task<ExtractionBenchmarkReport> HandleAsync(BenchmarkExtractionCommand command, CancellationToken cancellationToken)
    {
        if (!File.Exists(command.ArchivePath))
            throw new FileNotFoundException("Benchmark archive not found.", command.ArchivePath);
        if (command.RunCount < 1)
            command = command with { RunCount = 1 };

        // Parse the FOMOD once and resolve the fixed "select everything" selection up front so
        // every run measures exactly the same work.
        var fomodFiles = await Task.Run(() => _inspector.ReadFomodFiles(command.ArchivePath, null, cancellationToken), cancellationToken);
        var moduleConfig = fomodFiles["ModuleConfig.xml"];
        var module = _parser.TryParse(moduleConfig)
            ?? throw new InvalidOperationException("Archive is not a parseable FOMOD (no ModuleConfig.xml).");

        var selection = BuildSelectAllSelection(module);
        var fileState = new NullFomodFileStateProvider();
        var installs = new FomodSelectionResolver().ResolveFileInstalls(module, selection, fileState);
        if (installs.Count == 0)
            throw new InvalidOperationException("Selection resolved to zero files; cannot benchmark.");

        var benchmarkRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wildpinkler", "extraction-benchmarks");
        var process = Process.GetCurrentProcess();
        var manifestHash = string.Empty;
        var runs = new List<ExtractionBenchmarkRun>(command.RunCount);

        for (var i = 0; i < command.RunCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stagingRoot = Path.Combine(benchmarkRoot, Guid.NewGuid().ToString("n"));
            try
            {
                Directory.CreateDirectory(stagingRoot);
                var (wall, cpu) = await Task.Run(
                    () => TimeExtraction(command.ArchivePath, installs, stagingRoot, command.Strategy, cancellationToken), cancellationToken);
                // Count from the output tree so the numbers are authoritative regardless of how
                // progress coalescing interleaved with the final reports.
                var (files, bytes) = CountOutput(stagingRoot);
                if (i == command.RunCount - 1)
                    manifestHash = HashDirectory(stagingRoot);
                runs.Add(new ExtractionBenchmarkRun(i + 1, wall, cpu, process.PeakWorkingSet64, files, bytes));
            }
            finally
            {
                // Staging dirs are throwaway; clean them so repeated benchmarks don't accumulate.
                TryDeleteDirectory(stagingRoot);
            }
        }

        var medianWall = Median(runs.Select(r => r.WallTimeMs).ToArray());
        var medianCpu = Median(runs.Select(r => r.CpuTimeMs).ToArray());
        var medianWm = Median(runs.Select(r => r.PeakWorkingSetBytes).ToArray());
        var totalBytes = runs[^1].BytesWritten;
        return new ExtractionBenchmarkReport(
            Path.GetFullPath(command.ArchivePath),
            NormalizeStrategy(command.Strategy),
            command.RunCount,
            medianWall, medianCpu, medianWm,
            medianWall > 0 ? totalBytes * 1000 / medianWall : 0,
            manifestHash,
            runs[^1].FilesWritten,
            totalBytes,
            runs);
    }

    /// <summary>Times one extraction pass and returns (wallMs, cpuMs).</summary>
    private static (long, long) TimeExtraction(
        string archivePath,
        IReadOnlyList<FomodFileInstall> installs,
        string stagingRoot,
        string strategy,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        // Exercise the exact production extraction path without duplicating it.
        ModInstallService.ExtractFomodFilesForBenchmark(archivePath, installs, stagingRoot, null, strategy, cancellationToken);
        stopwatch.Stop();
        process.Refresh();
        return (stopwatch.ElapsedMilliseconds, (long)(process.TotalProcessorTime - cpuBefore).TotalMilliseconds);
    }

    /// <summary>
    /// Normalizes the requested strategy to a canonical value, rejecting unknown ones so a typo
    /// fails fast rather than silently running the wrong backend.
    /// </summary>
    private static string NormalizeStrategy(string strategy)
    {
        var s = (strategy ?? string.Empty).Trim();
        if (s.Equals("sharpcompress", StringComparison.OrdinalIgnoreCase))
            return "sharpcompress";
        if (s.Equals("native7z", StringComparison.OrdinalIgnoreCase))
            return "native7z";
        throw new ArgumentException($"Unknown benchmark strategy '{strategy}'. Use 'sharpcompress' or 'native7z'.", nameof(strategy));
    }

    private static (int, long) CountOutput(string root)
    {
        var files = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += new FileInfo(path).Length;
        }

        return (files, bytes);
    }

    /// <summary>Selects every plugin in every step, giving the maximal deterministic workload.</summary>
    private static List<FomodStepSelection> BuildSelectAllSelection(FomodModule module)
    {
        var selection = new List<FomodStepSelection>(module.InstallSteps.Count);
        foreach (var step in module.InstallSteps)
        {
            var groups = new List<FomodGroupSelection>(step.Groups.Count);
            foreach (var group in step.Groups)
                groups.Add(new FomodGroupSelection { Group = group, SelectedPlugins = new List<FomodPlugin>(group.Plugins) });
            selection.Add(new FomodStepSelection { Step = step, Groups = groups });
        }

        return selection;
    }

    private static long Median(long[] values)
    {
        Array.Sort(values);
        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    /// <summary>SHA-256 over "relativePath:hash" lines so two outputs compare exactly.</summary>
    private static string HashDirectory(string root)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            var digest = SHA256.HashData(File.ReadAllBytes(full));
            builder.Append(file).Append(':').Append(Convert.ToHexString(digest)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a still-open handle should not fail the benchmark.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
