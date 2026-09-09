using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Commands;

public sealed record RemoteModDto(string ModId, string SiteId, string Name, string? Author, string? Version, string? Category, DateTimeOffset? UpdatedAt, bool IsAdult, string? Summary);
public sealed record RemoteFileDto(string ModId, string SiteId, string FileKey, string FileName, string? Version, long? SizeInBytes, string? Md5, DateTimeOffset? UploadedAt, RemoteFileCategory Category, bool IsPrimary, string? Description, string? Changelog);
public sealed record RemoteFileListDto(string ModId, string SiteId, IReadOnlyList<RemoteFileDto> Files);
public sealed record RemoteHashMatchDto(string ModId, string SiteId, string Name, string? Author, string? Version, string FileKey, string FileName, string? FileVersion, long? SizeInBytes, DateTimeOffset? UpdatedAt);
public sealed record RemoteTrackedModDto(string SiteId, string GameKey, string ModKey, string Name, string? Author, string? Version, DateTimeOffset? UpdatedAt);
public sealed record RemoteRateLimitDto(string SiteId, int? HourlyRemaining, int? DailyRemaining, DateTimeOffset? HourlyReset, DateTimeOffset? DailyReset, bool IsExhausted);
public sealed record RemoteGameDto(string SiteId, string GameKey, string Name, string? Genre);

public sealed record GetRemoteModCommand(string ModId) : IAppCommand<RemoteModDto>;
public sealed record GetRemoteModFilesCommand(string ModId) : IAppCommand<RemoteFileListDto>;
public sealed record GetRemoteFileCommand(string ModId, string FileKey) : IAppCommand<RemoteFileDto>;
public sealed record IdentifyRemoteArchiveCommand(string ModId) : IAppCommand<RemoteHashMatchDto?>;
public sealed record GetTrackedRemoteModsCommand : IAppCommand<IReadOnlyList<RemoteTrackedModDto>>;
public sealed record GetRemoteRateLimitsCommand : IAppCommand<IReadOnlyList<RemoteRateLimitDto>>;
public sealed record GetRemoteGamesCommand : IAppCommand<IReadOnlyList<RemoteGameDto>>;
public sealed record CheckModForUpdateCommand(string ModId, string Period = "1w") : IAppCommand<ModUpdateCheckResult>;

public sealed class GetRemoteModHandler : IAppCommandHandler<GetRemoteModCommand, RemoteModDto>
{
    private readonly ModStore _mods;
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;

    public GetRemoteModHandler(ModStore mods, RemoteSiteRegistry registry, RemoteSiteContext context) =>
        (_mods, _registry, _context) = (mods, registry, context);

    public async Task<RemoteModDto> HandleAsync(GetRemoteModCommand command, CancellationToken cancellationToken)
    {
        var (mod, provider, credential) = await RemoteCommandSupport.ResolveAsync(_mods, _registry, _context, command.ModId, cancellationToken);
        var metadata = await provider.GetModAsync(mod.Remote!, credential, cancellationToken);
        return new RemoteModDto(mod.Id, metadata.Ref.SiteId, metadata.Name, metadata.Author, metadata.Version,
            metadata.CategoryName, metadata.UpdatedAt, metadata.IsAdult,
            metadata.IsAdult ? null : RemoteTextSanitizer.ToPlainText(metadata.Summary ?? metadata.DescriptionHtml, 1024));
    }
}

public sealed class GetRemoteModFilesHandler : IAppCommandHandler<GetRemoteModFilesCommand, RemoteFileListDto>
{
    private readonly ModStore _mods;
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;

    public GetRemoteModFilesHandler(ModStore mods, RemoteSiteRegistry registry, RemoteSiteContext context) =>
        (_mods, _registry, _context) = (mods, registry, context);

    public async Task<RemoteFileListDto> HandleAsync(GetRemoteModFilesCommand command, CancellationToken cancellationToken)
    {
        var (mod, provider, credential) = await RemoteCommandSupport.ResolveAsync(_mods, _registry, _context, command.ModId, cancellationToken);
        var listing = await provider.GetModFilesAsync(mod.Remote!, credential, cancellationToken);
        return new RemoteFileListDto(mod.Id, provider.SiteId, listing.Files
            .Select(file => RemoteCommandSupport.ToFileDto(mod.Id, provider.SiteId, file)).ToList());
    }
}

public sealed class GetRemoteFileHandler : IAppCommandHandler<GetRemoteFileCommand, RemoteFileDto>
{
    private readonly ModStore _mods;
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;

    public GetRemoteFileHandler(ModStore mods, RemoteSiteRegistry registry, RemoteSiteContext context) =>
        (_mods, _registry, _context) = (mods, registry, context);

    public async Task<RemoteFileDto> HandleAsync(GetRemoteFileCommand command, CancellationToken cancellationToken)
    {
        var (mod, provider, credential) = await RemoteCommandSupport.ResolveAsync(_mods, _registry, _context, command.ModId, cancellationToken);
        var file = await provider.GetFileAsync(mod.Remote! with { FileKey = command.FileKey }, credential, cancellationToken);
        return RemoteCommandSupport.ToFileDto(mod.Id, provider.SiteId, file);
    }
}

public sealed class IdentifyRemoteArchiveHandler : IAppCommandHandler<IdentifyRemoteArchiveCommand, RemoteHashMatchDto?>
{
    private readonly ModStore _mods;
    private readonly RemoteMetadataService _metadata;

    public IdentifyRemoteArchiveHandler(ModStore mods, RemoteMetadataService metadata) => (_mods, _metadata) = (mods, metadata);

    public async Task<RemoteHashMatchDto?> HandleAsync(IdentifyRemoteArchiveCommand command, CancellationToken cancellationToken)
    {
        var mod = (await _mods.LoadAsync()).FirstOrDefault(candidate => string.Equals(candidate.Id, command.ModId, StringComparison.Ordinal));
        if (mod is null)
            throw new InvalidOperationException($"Mod '{command.ModId}' was not found.");

        var identification = await _metadata.IdentifyAsync(mod, cancellationToken);
        return identification is null ? null : new RemoteHashMatchDto(
            mod.Id, identification.SiteId, identification.Mod.Name, identification.Mod.Author,
            identification.Mod.Version, identification.File.FileKey, identification.File.FileName,
            identification.File.Version, identification.File.SizeInBytes, identification.Mod.UpdatedAt);
    }
}

public sealed class GetTrackedRemoteModsHandler : IAppCommandHandler<GetTrackedRemoteModsCommand, IReadOnlyList<RemoteTrackedModDto>>
{
    private readonly TrackedModsService _tracked;

    public GetTrackedRemoteModsHandler(TrackedModsService tracked) => _tracked = tracked;

    public async Task<IReadOnlyList<RemoteTrackedModDto>> HandleAsync(
        GetTrackedRemoteModsCommand command,
        CancellationToken cancellationToken)
    {
        var tracked = await _tracked.GetTrackedModsAsync(cancellationToken);
        return tracked.Select(item => new RemoteTrackedModDto(
            item.Ref.SiteId,
            item.Ref.GameKey,
            item.Ref.ModKey,
            item.Name,
            item.Author,
            item.Version,
            item.UpdatedAt)).ToList();
    }
}

public sealed class GetRemoteRateLimitsHandler : IAppCommandHandler<GetRemoteRateLimitsCommand, IReadOnlyList<RemoteRateLimitDto>>
{
    private readonly RemoteSiteRegistry _registry;

    public GetRemoteRateLimitsHandler(RemoteSiteRegistry registry) => _registry = registry;

    public Task<IReadOnlyList<RemoteRateLimitDto>> HandleAsync(
        GetRemoteRateLimitsCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RemoteRateLimitDto> result = _registry.Providers
            .Select(provider => new RemoteRateLimitDto(
                provider.SiteId,
                provider.LastRateLimit.HourlyRemaining,
                provider.LastRateLimit.DailyRemaining,
                provider.LastRateLimit.HourlyReset,
                provider.LastRateLimit.DailyReset,
                provider.LastRateLimit.IsExhausted))
            .ToList();
        return Task.FromResult(result);
    }
}

public sealed class GetRemoteGamesHandler : IAppCommandHandler<GetRemoteGamesCommand, IReadOnlyList<RemoteGameDto>>
{
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteGameCatalog _catalog;

    public GetRemoteGamesHandler(RemoteSiteRegistry registry, RemoteGameCatalog catalog) =>
        (_registry, _catalog) = (registry, catalog);

    public async Task<IReadOnlyList<RemoteGameDto>> HandleAsync(GetRemoteGamesCommand command, CancellationToken cancellationToken)
    {
        var results = new List<RemoteGameDto>();
        foreach (var provider in _registry.Providers.Where(item => item.Capabilities.SupportsGameCatalog))
        {
            var games = await _catalog.GetGamesAsync(provider, cancellationToken);
            results.AddRange(games.Select(game => new RemoteGameDto(provider.SiteId, game.GameKey, game.Name, game.Genre)));
        }

        return results;
    }
}

public sealed class CheckModForUpdateHandler : IAppCommandHandler<CheckModForUpdateCommand, ModUpdateCheckResult>
{
    private readonly ModStore _mods;
    private readonly UpdateCheckService _updates;

    public CheckModForUpdateHandler(ModStore mods, UpdateCheckService updates) => (_mods, _updates) = (mods, updates);

    public async Task<ModUpdateCheckResult> HandleAsync(CheckModForUpdateCommand command, CancellationToken cancellationToken)
    {
        var mod = (await _mods.LoadAsync()).FirstOrDefault(candidate => string.Equals(candidate.Id, command.ModId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Mod '{command.ModId}' was not found.");
        if (mod.Remote is null)
            throw new InvalidOperationException($"'{mod.Name}' has no upstream site record.");

        // The site API only reports updates per site/game, not per mod, so one sweep is still made -
        // this just narrows the result to the mod the caller asked about.
        var result = await _updates.CheckForUpdatesAsync(command.Period, cancellationToken);
        var candidates = result.Candidates.Where(candidate => candidate.Entry.Id == mod.Id).ToList();
        var failures = result.Failures.Where(failure =>
            failure.SiteId == mod.Remote.SiteId && failure.GameKey == mod.Remote.GameKey).ToList();
        return new ModUpdateCheckResult(candidates, failures);
    }
}

internal static class RemoteCommandSupport
{
    public static async Task<(ModEntry Mod, IRemoteSiteProvider Provider, RemoteCredential Credential)> ResolveAsync(
        ModStore mods, RemoteSiteRegistry registry, RemoteSiteContext context, string modId, CancellationToken cancellationToken)
    {
        var mod = (await mods.LoadAsync()).FirstOrDefault(candidate => string.Equals(candidate.Id, modId, StringComparison.Ordinal));
        if (mod is null)
            throw new InvalidOperationException($"Mod '{modId}' was not found.");
        if (mod.Remote is null)
            throw new InvalidOperationException($"Mod '{mod.Name}' has no upstream site record.");
        if (string.IsNullOrWhiteSpace(mod.Remote.ModKey))
            throw new InvalidOperationException($"Mod '{mod.Name}' has no usable upstream identifier.");
        if (!registry.TryGet(mod.Remote.SiteId, out var provider))
            throw new InvalidOperationException($"The site '{mod.Remote.SiteId}' is not available in this build.");

        var (credential, _) = await context.AuthenticateAsync(provider, cancellationToken);
        return (mod, provider, credential);
    }

    public static RemoteFileDto ToFileDto(string modId, string siteId, RemoteFileMetadata file) => new(
        modId, siteId, file.FileKey, file.FileName, file.Version, file.SizeInBytes, file.Md5, file.UploadedAt,
        file.Category, file.IsPrimary, RemoteTextSanitizer.ToPlainText(file.Description, 512),
        RemoteTextSanitizer.ToPlainText(file.ChangelogText, 2048));
}
