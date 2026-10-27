using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Services;

/// <summary>The on-disk shape of the plan decision table.</summary>
public sealed class PlanDecisionSnapshot
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Decision key → decision. One decision per key; saving replaces.</summary>
    public Dictionary<string, PlanDecision> Decisions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists the user's plan decisions (resolve / exclude / replace) in one table with one schema.
/// Every plan action is "add or remove one decision" and every displayed row state is derived by
/// <see cref="PlanStateProjector"/> from (resolved facts + decisions) — so a decision cannot drift
/// from the row it describes, and the same decision automatically applies to every occurrence of
/// the requirement it covers.
/// </summary>
public sealed partial class PlanDecisionStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<PlanDecisionStore> _logger;

    public PlanDecisionStore(string? root = null, ILogger<PlanDecisionStore>? logger = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "plan-decisions.json");
        _backupPath = _path + ".bak";
        _logger = logger ?? NullLogger<PlanDecisionStore>.Instance;
    }

    /// <summary>Loads the decisions; a missing, corrupt, or newer-schema file yields an empty table.</summary>
    public async Task<PlanDecisionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return new PlanDecisionSnapshot();

            await using var stream = file.OpenRead();
            var snapshot = await JsonSerializer.DeserializeAsync<PlanDecisionSnapshot>(stream, JsonOptions, cancellationToken);
            if (snapshot is null || snapshot.SchemaVersion > CurrentSchemaVersion)
                return new PlanDecisionSnapshot();
            snapshot.Decisions = Usable(snapshot.Decisions);
            snapshot.SchemaVersion = CurrentSchemaVersion;
            LogLoaded(snapshot.Decisions.Count, _path);
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppDiagnostics.Write("The plan decision table could not be read.", exception);
            LogLoadFailed(_path, exception);
            return new PlanDecisionSnapshot();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} plan decision(s) from {Path}.")]
    private partial void LogLoaded(int count, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The plan decision table could not be read from {Path}; using an empty table.")]
    private partial void LogLoadFailed(string path, Exception exception);

    /// <summary>Persists a decision, replacing any prior one under the same key.</summary>
    public async Task SaveAsync(PlanDecision decision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Key))
            throw new ArgumentException("A plan decision requires a key.", nameof(decision));

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            var replaced = snapshot.Decisions.ContainsKey(decision.Key);
            snapshot.Decisions[decision.Key] = decision;
            snapshot.SchemaVersion = CurrentSchemaVersion;
            await WriteAsync(temporaryPath, snapshot, cancellationToken);
            LogSaved(decision.Kind, decision.Key, decision.SourceName, decision.TargetName, replaced, snapshot.Decisions.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The plan decision could not be saved.", exception);
            LogSaveFailed(decision.Key, exception);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Plan decision saved: {Kind} '{Source}' -> {Target} (key {Key}, {Replaced}, {Remaining} total).")]
    private partial void LogSaved(PlanDecisionKind kind, string key, string? source, string? target, bool replaced, int remaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan decision {Key} could not be saved.")]
    private partial void LogSaveFailed(string key, Exception exception);

    /// <summary>Removes every stored decision, leaving an empty table; a no-op when already empty.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            if (snapshot.Decisions.Count == 0)
                return;
            var removed = snapshot.Decisions.Count;
            snapshot.Decisions.Clear();
            snapshot.SchemaVersion = CurrentSchemaVersion;
            await WriteAsync(temporaryPath, snapshot, cancellationToken);
            LogCleared(removed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The plan decisions could not be cleared.", exception);
            LogClearFailed(exception);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleared all {Count} plan decisions.")]
    private partial void LogCleared(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clear the plan decisions.")]
    private partial void LogClearFailed(Exception exception);

    /// <summary>Removes the decision stored under the given key; a no-op when none exists.</summary>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            if (!snapshot.Decisions.Remove(key))
            {
                LogRemoveMissing(key);
                return;
            }
            await WriteAsync(temporaryPath, snapshot, cancellationToken);
            LogRemoved(key, snapshot.Decisions.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The plan decision could not be removed.", exception);
            LogRemoveFailed(key, exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan decision removed: {Key} ({Remaining} remaining).")]
    private partial void LogRemoved(string key, int remaining);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No plan decision to remove under {Key}.")]
    private partial void LogRemoveMissing(string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan decision {Key} could not be removed.")]
    private partial void LogRemoveFailed(string key, Exception exception);

    private async Task WriteAsync(string temporaryPath, PlanDecisionSnapshot snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        AtomicFile.Publish(temporaryPath, _path, _backupPath);
    }

    /// <summary>
    /// The id key for a decision keyed by a site mod identity, e.g. "nexus::skyrim::11411". The game
    /// key is included so the same site mod under two games stays distinct.
    /// </summary>
    public static string IdKey(string siteId, string gameKey, string modKey)
        => $"{siteId}::{gameKey}::{NormalizeModKey(siteId, modKey)}";

    /// <summary>
    /// The contextual key for a name-only requirement under a parent mod, e.g.
    /// "nexus::skyrim::12345::SKYRIM SCRIPT EXTENDER". All occurrences of the same requirement
    /// under the same parent share this key, so one decision covers every occurrence.
    /// </summary>
    public static string ContextKey(string siteId, string gameKey, string parentModKey, string requirementName)
        => $"{siteId}::{gameKey}::{NormalizeModKey(siteId, parentModKey)}::{NormalizeRequirementName(requirementName)}";

    /// <summary>
    /// The key of the decision a row should read. A row with a site identity uses its id key; a
    /// row without one (name-only, never promoted) falls back to its parent's context key.
    /// </summary>
    public static string? KeyFor(
        string? siteId, string? gameKey, string? modKey,
        RemoteRef? parentRef, string? requirementName)
    {
        if (siteId is not null && gameKey is not null && modKey is { Length: > 0 } && modKey != "0"
            && (!string.Equals(siteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                || NexusModIdentity.TryNormalizeModKey(modKey, out _)))
            return IdKey(siteId, gameKey, modKey);
        if (siteId is not null && gameKey is not null
            && parentRef is { } parent && parent.ModKey is { Length: > 0 }
            && requirementName is { Length: > 0 })
            return ContextKey(siteId, gameKey, parent.ModKey, requirementName);
        return null;
    }

    public static string NormalizeRequirementName(string name)
        => string.Join(' ', name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>Normalizes Nexus mod keys so "011411" and "11411" produce the same key.</summary>
    internal static string NormalizeModKey(string siteId, string modKey)
        => string.Equals(siteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(modKey, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id.ToString(CultureInfo.InvariantCulture)
            : modKey;

    /// <summary>Drops corrupt rows (empty key, or a decision with no usable target where one is required).</summary>
    private static Dictionary<string, PlanDecision> Usable(Dictionary<string, PlanDecision>? decisions)
    {
        var result = new Dictionary<string, PlanDecision>(StringComparer.Ordinal);
        if (decisions is null)
            return result;
        foreach (var (key, decision) in decisions)
        {
            if (decision is null || string.IsNullOrWhiteSpace(key))
                continue;
            if (decision.Kind == PlanDecisionKind.Exclude)
            {
                result[key] = decision;
            }
            else if (decision.Target is { } target
                && (target.IsLocal
                    || (target.SiteId is { Length: > 0 } && target.ModKey is { Length: > 0 })))
            {
                result[key] = decision;
            }
        }
        return result;
    }

    public void Dispose() => _gate.Dispose();
}
