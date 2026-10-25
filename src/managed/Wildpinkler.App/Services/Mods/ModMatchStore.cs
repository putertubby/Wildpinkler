using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Services;

/// <summary>
/// A remembered manual override a user makes on the dependency plan page, keyed by the
/// requirement's site id and mod key. It records which existing thing the requirement is satisfied
/// by — a local mod in the library (<see cref="LocalModId"/>/<see cref="LocalName"/>) or another
/// site mod already in the plan (<see cref="TargetModKey"/>) — and how: <see cref="KindFold"/>
/// (the requirement is folded into the target, so it is satisfied by that mod's download) or
/// <see cref="KindObsolete"/> (the target is a newer/current mod that obsoletes the referenced one,
/// so the referenced mod is kept for its own tree and no redirect occurs). The
/// <see cref="SourceName"/> is the requirement's display name for the Resolved section. A future
/// resolve follows fold targets so the user makes each association only once.
/// </summary>
public sealed record ModMatchLink(
    string SiteId,
    string ModKey,
    string LocalModId,
    string? LocalName = null,
    string? TargetModKey = null,
    string? SourceName = null,
    string? TargetName = null,
    string Kind = "fold");

/// <summary>A remembered decision for a requirement that has no provider-supplied mod key.</summary>
public sealed record ContextualModMatchLink(
    string SiteId,
    string GameKey,
    string ParentModKey,
    string RequirementName,
    string LocalModId,
    string? LocalName = null,
    string? TargetModKey = null,
    string? TargetName = null,
    string Kind = "match");

/// <summary>The on-disk shape of the match table.</summary>
public sealed class ModMatchSnapshot
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Site+mod-key → remembered local association.</summary>
    public Dictionary<string, ModMatchLink> Links { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Parent identity + normalized requirement name → remembered keyless decision.</summary>
    public Dictionary<string, ContextualModMatchLink> ContextualLinks { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists the manual "match this requirement to that local mod" decisions a user makes on the
/// dependency plan page. The resolver consults it before falling back to name matching, so a match
/// the user makes once is remembered for every future resolve.
/// </summary>
public sealed class ModMatchStore : IDisposable
{
    private const int CurrentSchemaVersion = 2;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>The requirement is matched to a mod already in the library (its ref is pointed there).</summary>
    public const string KindMatch = "match";

    /// <summary>The requirement is folded into another mod in the plan (satisfied by that mod's download).</summary>
    public const string KindFold = "fold";

    /// <summary>The referenced mod is obsoleted by a newer/current mod (no redirect; the referenced mod stays for its own tree).</summary>
    public const string KindObsolete = "obsolete";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModMatchStore(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "mod-matches.json");
        _backupPath = _path + ".bak";
    }

    /// <summary>Loads the remembered links; a missing or corrupt file yields an empty table.</summary>
    public async Task<ModMatchSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return new ModMatchSnapshot();

            await using var stream = file.OpenRead();
            var snapshot = await JsonSerializer.DeserializeAsync<ModMatchSnapshot>(stream, JsonOptions, cancellationToken);
            if (snapshot is null || snapshot.SchemaVersion > CurrentSchemaVersion)
                return new ModMatchSnapshot();
            snapshot.Links = Usable(snapshot.Links);
            snapshot.ContextualLinks = UsableContextual(snapshot.ContextualLinks);
            snapshot.SchemaVersion = CurrentSchemaVersion;
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppDiagnostics.Write("The mod match table could not be read.", exception);
            return new ModMatchSnapshot();
        }
    }

    /// <summary>
    /// Persists a remembered "this requirement is that local mod" association for the given
    /// site+mod key, replacing any prior one. The local id and name let the resolver match and
    /// label the mod.
    /// </summary>
    public Task SaveAsync(
        string siteId, string modKey, string localModId, string? localName,
        string? sourceName, CancellationToken cancellationToken = default)
        => SaveLinkAsync(siteId, modKey,
            new ModMatchLink(siteId, modKey, localModId, localName, null, sourceName, localName, KindMatch), cancellationToken);

    /// <summary>
    /// Persists a remembered "this requirement is that other site mod already in a plan" association
    /// for the given site+mod key, replacing any prior one. The target key is a same-site mod key
    /// the resolver redirects to (fold) or, for an obsolete override, ignores — see <paramref name="kind"/>.
    /// </summary>
    public Task SaveAliasAsync(
        string siteId, string modKey, string targetModKey,
        string? sourceName, string? targetName, string kind,
        CancellationToken cancellationToken = default)
        => SaveLinkAsync(siteId, modKey,
            new ModMatchLink(siteId, modKey, string.Empty, null, targetModKey, sourceName, targetName, kind), cancellationToken);

    /// <summary>
    /// Persists a decision for a uniquely identified name-only requirement. The parent must have
    /// an actual provider identity; the normalized name is only used in this separate context key.
    /// </summary>
    public async Task SaveContextualAsync(
        string siteId,
        string gameKey,
        string parentModKey,
        string requirementName,
        string localModId,
        string? localName,
        string? targetModKey,
        string? targetName,
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(gameKey)
            || string.IsNullOrWhiteSpace(parentModKey) || string.IsNullOrWhiteSpace(requirementName)
            || (string.IsNullOrWhiteSpace(localModId) && string.IsNullOrWhiteSpace(targetModKey)))
            throw new ArgumentException("A contextual match requires a stable parent identity and a valid target.");

        if (string.Equals(siteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
            && !NexusModIdentity.TryNormalizeModKey(parentModKey, out parentModKey))
            throw new InvalidOperationException("A Nexus contextual match parent must use a positive numeric mod ID.");
        if (targetModKey is not null && string.Equals(siteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
            && !NexusModIdentity.TryNormalizeModKey(targetModKey, out targetModKey))
            throw new InvalidOperationException("A Nexus contextual match target must use a positive numeric mod ID.");

        var link = new ContextualModMatchLink(
            siteId, gameKey, parentModKey, NormalizeRequirementName(requirementName),
            localModId, localName, targetModKey, targetName, kind);
        var key = ContextualLinkKey(siteId, gameKey, parentModKey, requirementName);
        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            snapshot.ContextualLinks[key] = link;
            snapshot.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            AtomicFile.Publish(temporaryPath, _path, _backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The contextual mod match could not be saved.", exception);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    public static string ContextualLinkKey(string siteId, string gameKey, string parentModKey, string requirementName)
        => $"{siteId}::{gameKey}::{parentModKey}::{NormalizeRequirementName(requirementName)}";

    private static string NormalizeRequirementName(string name)
        => string.Join(' ', name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public async Task RemoveContextualAsync(string contextualKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contextualKey))
            return;

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            if (!snapshot.ContextualLinks.Remove(contextualKey))
                return;
            snapshot.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            AtomicFile.Publish(temporaryPath, _path, _backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The contextual mod match could not be removed.", exception);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the remembered association for the given site+mod key (undoing a fold or a local
    /// match). A no-op when no such association is stored.
    /// </summary>
    public async Task RemoveAsync(string siteId, string modKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(modKey))
            return;

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            if (!snapshot.Links.Remove(LinkKey(siteId, modKey)))
                return; // Nothing stored for this key; nothing to remove or publish.

            snapshot.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            AtomicFile.Publish(temporaryPath, _path, _backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The mod match could not be removed.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    private async Task SaveLinkAsync(string siteId, string modKey, ModMatchLink link, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(modKey))
            return;

        if (string.Equals(siteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (!NexusModIdentity.TryNormalizeModKey(modKey, out var normalizedModKey))
                throw new InvalidOperationException("A Nexus match must use a positive numeric mod ID.");

            var targetModKey = link.TargetModKey;
            if (targetModKey is not null)
            {
                if (!NexusModIdentity.TryNormalizeModKey(targetModKey, out var normalizedTargetKey))
                    throw new InvalidOperationException("A Nexus match target must use a positive numeric mod ID.");
                targetModKey = normalizedTargetKey;
            }

            modKey = normalizedModKey;
            link = link with { ModKey = normalizedModKey, TargetModKey = targetModKey };
        }

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            snapshot.Links[LinkKey(siteId, modKey)] = link;
            snapshot.SchemaVersion = CurrentSchemaVersion;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            AtomicFile.Publish(temporaryPath, _path, _backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("The mod match could not be saved.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    /// <summary>The link key combines site and mod key so the same mod on two sites stays distinct.</summary>
    public static string LinkKey(string siteId, string modKey)
        => $"{siteId}::{modKey}";

    /// <summary>
    /// Drops corrupt rows (empty key, or no usable target) so a bad entry cannot poison a match.
    /// A link is usable when it names a local mod or a target site mod.
    /// </summary>
    private static Dictionary<string, ModMatchLink> Usable(Dictionary<string, ModMatchLink>? links) =>
        (links ?? new Dictionary<string, ModMatchLink>())
            .Where(entry => entry.Value is not null
                && !string.IsNullOrWhiteSpace(entry.Value.SiteId)
                && !string.IsNullOrWhiteSpace(entry.Value.ModKey)
                && (!string.Equals(entry.Value.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                    || NexusModIdentity.TryNormalizeModKey(entry.Value.ModKey, out _))
                && (!string.IsNullOrWhiteSpace(entry.Value.LocalModId)
                    || !string.IsNullOrWhiteSpace(entry.Value.TargetModKey)))
            .Where(entry => !string.Equals(entry.Value.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Value.TargetModKey)
                || NexusModIdentity.TryNormalizeModKey(entry.Value.TargetModKey, out _))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static Dictionary<string, ContextualModMatchLink> UsableContextual(Dictionary<string, ContextualModMatchLink>? links) =>
        (links ?? new Dictionary<string, ContextualModMatchLink>())
            .Where(entry => entry.Value is not null
                && !string.IsNullOrWhiteSpace(entry.Value.SiteId)
                && !string.IsNullOrWhiteSpace(entry.Value.GameKey)
                && !string.IsNullOrWhiteSpace(entry.Value.ParentModKey)
                && !string.IsNullOrWhiteSpace(entry.Value.RequirementName)
                && (!string.Equals(entry.Value.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                    || NexusModIdentity.TryNormalizeModKey(entry.Value.ParentModKey, out _))
                && (!string.Equals(entry.Value.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(entry.Value.TargetModKey)
                    || NexusModIdentity.TryNormalizeModKey(entry.Value.TargetModKey, out _))
                && (!string.IsNullOrWhiteSpace(entry.Value.LocalModId)
                    || !string.IsNullOrWhiteSpace(entry.Value.TargetModKey)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    public void Dispose() => _gate.Dispose();
}
