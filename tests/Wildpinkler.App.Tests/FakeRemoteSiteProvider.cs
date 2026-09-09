using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Tests;

/// <summary>
/// A complete, in-memory <see cref="IRemoteSiteProvider"/>. It exists to prove the abstraction is
/// implementable without any Nexus-specific assumption, and to give the conformance suite a second
/// implementation to run against.
/// </summary>
public sealed class FakeRemoteSiteProvider : IRemoteSiteProvider
{
    public const string SiteIdentifier = "fake";

    private readonly Dictionary<string, RemoteModMetadata> _mods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteFileMetadata> _files = new(StringComparer.Ordinal);

    public FakeRemoteSiteProvider(RemoteSiteCapabilities? capabilities = null)
    {
        Capabilities = capabilities ?? new RemoteSiteCapabilities(
            SupportsProtocolLinks: true,
            SupportsUpdateCheck: true,
            SupportsTracking: true,
            SupportsHashLookup: true,
            SupportsBrowserFallback: true,
            SupportsGameCatalog: true);
        CredentialProvider = new FakeCredentialProvider();
        ProtocolHandler = new FakeProtocolHandler();

        _mods["1"] = new RemoteModMetadata(
            new RemoteRef(SiteIdentifier, "testgame", "1"),
            "Test mod",
            Summary: "A mod that only exists in tests.",
            Author: "An author",
            Version: "1.0",
            UpdatedAt: DateTimeOffset.UnixEpoch);
        _files["2"] = new RemoteFileMetadata(
            "2",
            "mod.zip",
            DisplayName: "Test mod v1",
            Version: "1.0",
            SizeInBytes: 1024,
            Md5: "d41d8cd98f00b204e9800998ecf8427e",
            UploadedAt: DateTimeOffset.UnixEpoch,
            Category: RemoteFileCategory.Main,
            IsPrimary: true);
    }

    public string SiteId => SiteIdentifier;

    public string DisplayName => "Fake site";

    public string BaseUrl => "https://fake.invalid";

    public RemoteSiteCapabilities Capabilities { get; }

    public RemoteRateLimit LastRateLimit { get; private set; } = RemoteRateLimit.Unknown;

    public IRemoteProtocolHandler? ProtocolHandler { get; }

    public IRemoteCredentialProvider CredentialProvider { get; }

    public Task<RemoteAccount> ValidateAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        return Task.FromResult(new RemoteAccount("user-1", "tester", IsPremium: false));
    }

    public Task<RemoteModMetadata> GetModAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        return _mods.TryGetValue(reference.ModKey, out var mod)
            ? Task.FromResult(mod)
            : throw new RemoteSiteException(RemoteErrorKind.NotFound, "No such mod.", SiteIdentifier);
    }

    public Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        return reference.FileKey is { } fileKey && _files.TryGetValue(fileKey, out var file)
            ? Task.FromResult(file)
            : throw new RemoteSiteException(RemoteErrorKind.NotFound, "No such file.", SiteIdentifier);
    }

    public Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        return Task.FromResult(new RemoteFileListing(_files.Values.ToList()));
    }

    public Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        IReadOnlyList<RemoteDownloadSource> sources = [new RemoteDownloadSource(new Uri("https://fake.invalid/download/2"), "Primary", 0)];
        return Task.FromResult(sources);
    }

    public Task<IReadOnlyList<RemoteHashMatch>> FindByHashAsync(string gameKey, string md5, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        IReadOnlyList<RemoteHashMatch> matches = _files.Values
            .Where(file => string.Equals(file.Md5, md5, StringComparison.OrdinalIgnoreCase))
            .Select(file => new RemoteHashMatch(_mods["1"], file))
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(string gameKey, string period, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        IReadOnlyList<RemoteModUpdate> updates = [new RemoteModUpdate("1", "Test mod", DateTimeOffset.UnixEpoch.AddDays(1))];
        return Task.FromResult(updates);
    }

    public Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        IReadOnlyList<RemoteTrackedMod> tracked =
        [
            new RemoteTrackedMod(new RemoteRef(SiteIdentifier, "testgame", "1"), "Test mod", "An author", "1.0", DateTimeOffset.UnixEpoch)
        ];
        return Task.FromResult(tracked);
    }

    public Task<IReadOnlyList<RemoteGame>> GetGamesAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        RequireCredential(credential);
        IReadOnlyList<RemoteGame> games = [new RemoteGame("testgame", "Test game")];
        return Task.FromResult(games);
    }

    public string BuildModPageUrl(RemoteRef reference) =>
        $"{BaseUrl}/{reference.GameKey}/mods/{reference.ModKey}";

    private void RequireCredential(RemoteCredential credential)
    {
        if (!credential.IsUsable)
            throw new RemoteSiteException(RemoteErrorKind.Unauthorized, "No credential is configured.", SiteIdentifier);
        LastRateLimit = new RemoteRateLimit(100, 1000, null, null);
    }

    private sealed class FakeCredentialProvider : IRemoteCredentialProvider
    {
        public string SiteId => SiteIdentifier;

        public RemoteCredentialKind Kind => RemoteCredentialKind.ApiKey;

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<RemoteCredential> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredential(RemoteCredentialKind.ApiKey, "fake-key"));

        public Task<RemoteCredential> RefreshAsync(RemoteCredential current, CancellationToken cancellationToken = default) =>
            Task.FromResult(current);
    }

    private sealed class FakeProtocolHandler : IRemoteProtocolHandler
    {
        public string SiteId => SiteIdentifier;

        public string Scheme => "fakemod";

        public bool CanHandle(Uri? uri) =>
            uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

        public RemoteLink Parse(Uri uri)
        {
            if (!CanHandle(uri))
                return RemoteLink.ForUnsupported(SiteIdentifier, "This link is not a fake-site link.");

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 4
                ? RemoteLink.ForModFile(SiteIdentifier, uri.Host, segments[1], segments[3], null, null, null)
                : RemoteLink.ForUnsupported(SiteIdentifier, "This fake-site link is not a file link.");
        }
    }
}
