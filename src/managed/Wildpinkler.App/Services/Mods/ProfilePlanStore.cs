using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>One row's persisted install inputs: the destination, path-remember flag, FOMOD
/// selections, and the user's include/exclude decision. Keyed by the mod's stable row key.</summary>
public sealed class ProfilePlanRowState
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Included { get; set; } = true;
    public string? Destination { get; set; }
    public string? SourceRoot { get; set; }
    public bool RememberPath { get; set; }
    public List<FomodStepSelection> Selections { get; set; } = new();
}

/// <summary>
/// A saved plan root: the remote identity of one mod the user planned from. Order matters —
/// the first root is the plan's primary (a fresh resolve replaces the entries), the rest merge
/// into it on restore.
/// </summary>
public sealed class PlanRootRef
{
    public string SiteId { get; set; } = string.Empty;
    public string GameKey { get; set; } = string.Empty;
    public string ModKey { get; set; } = string.Empty;
    public string? PageUrl { get; set; }

    /// <summary>The live reference for resolution.</summary>
    public Wildpinkler.Remote.RemoteRef ToRef()
        => new(SiteId, GameKey, ModKey, PageUrl: PageUrl);
}

/// <summary>The on-disk shape of one profile's saved plan.</summary>
public sealed class ProfilePlanSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = string.Empty;
    public List<PlanRootRef> Roots { get; set; } = new();
    public List<ProfilePlanRowState> Rows { get; set; } = new();

    /// <summary>
    /// Plan-local decisions ("ignore this in this plan"): keyed exactly like the global decision
    /// table. These live with the profile plan and are discarded with it; they shadow any global
    /// decision under the same key. Only <see cref="Wildpinkler.App.Models.PlanDecisionKind.Exclude"/>
    /// decisions are stored here — resolve/replace facts stay global.
    /// </summary>
    public Dictionary<string, Wildpinkler.App.Models.PlanDecision> LocalDecisions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists a per-profile "working plan": which mods are included/excluded, each row's destination,
/// and its FOMOD selections. This lets a plan be saved for later and restored on the next visit
/// without mutating the profile. Distinct from the exported mod list and from the global decision
/// table (which stores cross-profile resolve/exclude/replace facts).
/// </summary>
public sealed class ProfilePlanStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfilePlanStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "plans");
    }

    private string PathFor(string profileId) =>
        Path.Combine(_root, "plan-" + SafeFileToken(profileId) + ".json");

    private static string SafeFileToken(string profileId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(profileId.Where(c => Array.IndexOf(invalid, c) < 0).ToArray());
        return cleaned.Length > 0 ? cleaned : "profile";
    }

    public async Task<ProfilePlanSnapshot?> LoadAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(profileId);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return null;
            await using var stream = file.OpenRead();
            var snapshot = await JsonSerializer.DeserializeAsync<ProfilePlanSnapshot>(stream, JsonOptions, cancellationToken);
            if (snapshot is null || snapshot.SchemaVersion > CurrentSchemaVersion)
                return null;
            snapshot.ProfileId = profileId;
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ProfilePlanSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrEmpty(snapshot.ProfileId))
            throw new ArgumentException("A plan snapshot requires a profile id.", nameof(snapshot));
        snapshot.SchemaVersion = CurrentSchemaVersion;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var path = PathFor(snapshot.ProfileId);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records (or replaces) a plan-local decision for the profile. The snapshot is created
    /// on demand so a profile can carry decisions even before its first full save.
    /// </summary>
    public async Task SaveLocalDecisionAsync(string profileId, Wildpinkler.App.Models.PlanDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (string.IsNullOrEmpty(decision.Key))
            throw new ArgumentException("A plan decision requires a key.", nameof(decision));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var path = PathFor(profileId);
            ProfilePlanSnapshot? snapshot = null;
            var file = new FileInfo(path);
            if (file.Exists && file.Length <= MaxFileBytes)
            {
                try
                {
                    await using var stream = file.OpenRead();
                    snapshot = await JsonSerializer.DeserializeAsync<ProfilePlanSnapshot>(stream, JsonOptions, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    snapshot = null;
                }
            }
            snapshot ??= new ProfilePlanSnapshot();
            snapshot.ProfileId = profileId;
            snapshot.SchemaVersion = CurrentSchemaVersion;
            snapshot.LocalDecisions[decision.Key] = decision;
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes one plan-local decision for the profile. Returns true when a decision was actually
    /// removed, false when the profile had no such decision (no-op).
    /// </summary>
    public async Task<bool> TryRemoveLocalDecisionAsync(string profileId, string decisionKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(profileId);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return false;
            ProfilePlanSnapshot? snapshot;
            try
            {
                await using var stream = file.OpenRead();
                snapshot = await JsonSerializer.DeserializeAsync<ProfilePlanSnapshot>(stream, JsonOptions, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
            if (snapshot is null || !snapshot.LocalDecisions.Remove(decisionKey))
                return false;
            snapshot.SchemaVersion = CurrentSchemaVersion;
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes all plan-local decisions for the profile ("clear all" on this plan only).</summary>
    public async Task ClearLocalDecisionsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(profileId);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return;
            ProfilePlanSnapshot? snapshot;
            try
            {
                await using var stream = file.OpenRead();
                snapshot = await JsonSerializer.DeserializeAsync<ProfilePlanSnapshot>(stream, JsonOptions, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return;
            }
            if (snapshot is null || snapshot.LocalDecisions.Count == 0)
                return;
            snapshot.LocalDecisions.Clear();
            snapshot.SchemaVersion = CurrentSchemaVersion;
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the profile's saved plan (its "abandon without changes" counterpart).</summary>
    public async Task DiscardAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(profileId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed delete is non-fatal; the plan simply stays.
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
