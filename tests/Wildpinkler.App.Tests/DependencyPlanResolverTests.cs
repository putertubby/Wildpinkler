using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the recursive resolver turns a tree of site requirements into a reviewable plan,
/// with every data-quality imperfection (404s, name-only, conflicts, cycles, already-installed)
/// landing in an entry status rather than an exception.
/// </summary>
public class DependencyPlanResolverTests
{
    private static readonly RemoteCredential Key = new(RemoteCredentialKind.ApiKey, "fake-key");

    private static readonly Func<IRemoteSiteProvider, CancellationToken, Task<(RemoteCredential Credential, RemoteAccount Account)>> FakeAuthentication =
        (provider, cancellationToken) =>
        {
            _ = provider;
            _ = cancellationToken;
            return Task.FromResult((Key, new RemoteAccount("user-1", "tester", IsPremium: false)));
        };

    private sealed class ConfigurableFakeProvider : IRemoteSiteProvider
    {
        public ConfigurableFakeProvider(
            IReadOnlyDictionary<string, IReadOnlyList<RemoteRequirement>> requirements,
            IReadOnlyDictionary<string, IReadOnlyList<RemoteFileMetadata>> files,
            IReadOnlyDictionary<string, RemoteModMetadata> mods,
            IReadOnlySet<string>? failingRequirements = null,
            IReadOnlySet<string>? failingDetails = null,
            string siteId = "fake")
        {
            _siteId = siteId;
            FailingRequirements = failingRequirements ?? new HashSet<string>();
            FailingDetails = failingDetails ?? new HashSet<string>();
            Requirements = new Dictionary<string, IReadOnlyList<RemoteRequirement>>(requirements);
            Files = new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>(files);
            Mods = new Dictionary<string, RemoteModMetadata>(mods);
        }

        public Dictionary<string, IReadOnlyList<RemoteRequirement>> Requirements { get; set; }
        public Dictionary<string, IReadOnlyList<RemoteFileMetadata>> Files { get; set; }
        public Dictionary<string, RemoteModMetadata> Mods { get; set; }
        public IReadOnlySet<string> FailingRequirements { get; }
        public IReadOnlySet<string> FailingDetails { get; }
        private readonly string _siteId;

        public string SiteId => _siteId;
        public string DisplayName => "Fake site";
        public string BaseUrl => "https://fake.invalid";
        public RemoteSiteCapabilities Capabilities { get; } = new(
            SupportsBrowserFallback: true, SupportsRequirements: true, SupportsModDetails: true);
        public RemoteRateLimit LastRateLimit => RemoteRateLimit.Unknown;
        public IRemoteProtocolHandler? ProtocolHandler => null;
        public IRemoteCredentialProvider CredentialProvider => new TestCredentialProvider();

        public Task<RemoteAccount> ValidateAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteAccount("user-1", "tester", IsPremium: false));

        public Task<RemoteModMetadata> GetModAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
            => Mods.TryGetValue(reference.ModKey, out var mod)
                ? Task.FromResult(mod)
                : Task.FromException<RemoteModMetadata>(
                    new RemoteSiteException(RemoteErrorKind.NotFound, "No such mod.", SiteId));

        public Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromException<RemoteFileMetadata>(
                new RemoteSiteException(RemoteErrorKind.NotFound, "No such file.", SiteId));

        public Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
            => Files.TryGetValue(reference.ModKey, out var files)
                ? Task.FromResult(new RemoteFileListing(files.ToList()))
                : Task.FromException<RemoteFileListing>(
                    new RemoteSiteException(RemoteErrorKind.NotFound, "No such mod.", SiteId));

        // The resolver fetches details (not the raw file listing) per mod; mirror the fake's
        // data so a known mod yields its files (empty when the test provides none) and an
        // unknown mod 404s exactly like GetModFilesAsync used to. Tests can opt specific mods
        // into a simulated failure (network-ish) or a 404 via FailingDetails.
        public Task<RemoteModDetails> GetModDetailsAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
        {
            if (FailingDetails.Contains(reference.ModKey))
                return Task.FromException<RemoteModDetails>(
                    new RemoteSiteException(RemoteErrorKind.Unknown, "Simulated transient failure.", SiteId));
            return Mods.TryGetValue(reference.ModKey, out var mod)
                ? Task.FromResult(new RemoteModDetails(
                    reference, mod.Name, mod.Summary, mod.Author, mod.Uploader,
                    mod.Version, LatestFiles: Files.TryGetValue(reference.ModKey, out var files) ? files.ToList() : new List<RemoteFileMetadata>()))
                : Task.FromException<RemoteModDetails>(
                    new RemoteSiteException(RemoteErrorKind.NotFound, "No such mod.", SiteId));
        }

        public Task<IReadOnlyList<RemoteRequirement>> GetRequirementsAsync(
            RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
        {
            if (FailingRequirements.Contains(reference.ModKey))
                return Task.FromException<IReadOnlyList<RemoteRequirement>>(
                    new RemoteSiteException(RemoteErrorKind.Unknown, "Simulated transient failure.", SiteId));
            return Requirements.TryGetValue(reference.ModKey, out var requirements)
                ? Task.FromResult<IReadOnlyList<RemoteRequirement>>(requirements.ToList())
                : Task.FromException<IReadOnlyList<RemoteRequirement>>(
                    new RemoteSiteException(RemoteErrorKind.NotFound, "No such mod.", SiteId));
        }

        public Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(
            RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteDownloadSource>>(Array.Empty<RemoteDownloadSource>());

        public Task<IReadOnlyList<RemoteHashMatch>> FindByHashAsync(
            string gameKey, string md5, RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteHashMatch>>(Array.Empty<RemoteHashMatch>());

        public Task<IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(
            string gameKey, string period, RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteModUpdate>>(Array.Empty<RemoteModUpdate>());

        public Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(
            RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteTrackedMod>>(Array.Empty<RemoteTrackedMod>());

        public Task<IReadOnlyList<RemoteGame>> GetGamesAsync(
            RemoteCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteGame>>(Array.Empty<RemoteGame>());

        public string BuildModPageUrl(RemoteRef reference) =>
            string.Equals(SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                ? NexusModIdentity.BuildModPageUrl(reference.GameKey, reference.ModKey)
                : $"{BaseUrl}/{reference.GameKey}/mods/{reference.ModKey}";

        private sealed class TestCredentialProvider : IRemoteCredentialProvider
        {
            public string SiteId => "fake";
            public RemoteCredentialKind Kind => RemoteCredentialKind.ApiKey;
            public bool IsAvailable => true;
            public string? UnavailableReason => null;
            public Task<RemoteCredential> AcquireAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new RemoteCredential(RemoteCredentialKind.ApiKey, "fake-key"));
            public Task<RemoteCredential> RefreshAsync(RemoteCredential current, CancellationToken cancellationToken = default)
                => Task.FromResult(current);
        }
    }

    private static (DependencyPlanResolver Resolver, ConfigurableFakeProvider Provider) Create(
        IReadOnlyDictionary<string, IReadOnlyList<RemoteRequirement>> requirements,
        IReadOnlyDictionary<string, IReadOnlyList<RemoteFileMetadata>> files,
        IReadOnlyDictionary<string, RemoteModMetadata> mods,
        IReadOnlySet<string>? failingRequirements = null,
        IReadOnlySet<string>? failingDetails = null,
        string siteId = "fake")
    {
        var provider = new ConfigurableFakeProvider(requirements, files, mods, failingRequirements, failingDetails, siteId);
        var registry = new RemoteSiteRegistry();
        registry.Register(provider);
        return (new DependencyPlanResolver(registry, FakeAuthentication), provider);
    }

    private static RemoteFileMetadata File(string key, bool primary = true, long size = 1024) => new(
        key, $"{key}.zip", Version: "1.0", SizeInBytes: size, Md5: key, IsPrimary: primary, Category: RemoteFileCategory.Main);

    private static RemoteModMetadata Mod(string key, string name) => new(
        new RemoteRef("fake", "testgame", key), name, Author: "Author", Version: "1.0");

    private static RemoteRequirement Req(string? modKey, string name, RemoteRequirementKind kind, string? fileKey = null, string? notes = null) =>
        new(kind, name, modKey, fileKey, notes);

    [Fact]
    public async Task ResolvesATreeWithKindsFilesAndChain()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required, fileKey: "20", notes: "needed") },
                ["2"] = new[] { Req("3", "Extra", RemoteRequirementKind.Recommended) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20"), File("21", primary: false) },
                ["3"] = new[] { File("30") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base"),
                ["3"] = Mod("3", "Extra")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, plan.Entries.Count);

        var root = plan.Entries.Single(entry => entry.Ref.ModKey == "1");
        Assert.True(root.IsSelected);
        Assert.Equal("10", root.FileKey);
        Assert.Equal("The mod you chose.", root.ChainText);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Ready, baseMod.Status);
        Assert.True(baseMod.IsSelected);
        // The requirement pinned file 20, so the primary 20 happens to match; assert the pin logic.
        Assert.Equal("20", baseMod.FileKey);
        Assert.Contains("needed", baseMod.RequirementNotes);

        var extra = plan.Entries.Single(entry => entry.Ref.ModKey == "3");
        Assert.Equal(DependencyPlanEntryStatus.Ready, extra.Status);
        Assert.False(extra.IsSelected); // recommended is off by default
        Assert.Equal(RemoteRequirementKind.Recommended, extra.Kind);
    }

    [Fact]
    public async Task InstalledModIsMatchedAndNotDownloaded()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-2",
                Name = "Base",
                Remote = new RemoteRef("fake", "testgame", "2"),
                Author = "Author",
                Version = "9.9"
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Installed, baseMod.Status);
        Assert.Equal("local-2", baseMod.LocalModId);
        Assert.Equal("9.9", baseMod.Version);
        Assert.Null(baseMod.FileKey);
    }

    [Fact]
    public async Task InstalledDependencyWithSameSiteIdentityAndVersionUsesSiteDetailsWithoutDuplicate()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-skse",
                Name = "Skyrim Script Extender (SKSE64)",
                Remote = new RemoteRef("fake", "testgame", "2"),
                Version = "2.3.1",
                ProfileIds = new List<string> { "profile-1" }
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Skyrim Script Extender (SKSE64)", RemoteRequirementKind.Required) },
                ["2"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = new RemoteModMetadata(
                    new RemoteRef("fake", "testgame", "2"),
                    "Skyrim Script Extender (SKSE64)",
                    Summary: "Official site details.",
                    Author: "SKSE Team",
                    Version: "2.3.1")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed,
            resolvingProfileId: "profile-1",
            cancellationToken: TestContext.Current.CancellationToken);

        var skseRows = plan.Entries.Where(entry => entry.Ref.ModKey == "2").ToList();
        var skse = Assert.Single(skseRows);
        Assert.Equal(DependencyPlanEntryStatus.Installed, skse.Status);
        Assert.Equal("local-skse", skse.LocalModId);
        Assert.Equal("2.3.1", skse.Version);
        Assert.Equal("2.3.1", skse.LatestSiteVersion);
        Assert.False(skse.UpdateAvailable);
        Assert.Equal("Official site details.", skse.Summary);
        Assert.Equal("SKSE Team", skse.Author);
        Assert.True(skse.InTargetProfile);
    }

    // Regression: when the root mod is already installed, its requirements must still be fetched
    // and walked, otherwise the plan silently collapses to the root alone (the "No dependencies
    // found" bug). The installed root keeps status Installed (no download) but its children appear.
    [Fact]
    public async Task InstalledRootStillResolvesItsDependencies()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-root",
                Name = "Root",
                Remote = new RemoteRef("fake", "testgame", "1")
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                // Root (1) is installed but declares a requirement on mod 2.
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        // Both the installed root and its dependency must be present in the plan.
        Assert.Equal(2, plan.Entries.Count);
        var root = plan.Entries.Single(entry => entry.Ref.ModKey == "1");
        Assert.Equal(DependencyPlanEntryStatus.Installed, root.Status);
        Assert.Equal("local-root", root.LocalModId);

        var child = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal("Base", child.Name);
        // The child is not installed, so it needs a downloadable artifact.
        Assert.Equal("20", child.FileKey);
    }

    [Fact]
    public async Task MissingModBecomesUnresolvable()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("9", "Gone Mod", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var gone = plan.Entries.Single(entry => entry.Ref.ModKey == "9");
        Assert.Equal(DependencyPlanEntryStatus.Unresolvable, gone.Status);
        Assert.Equal("Gone Mod", gone.Name);
        Assert.False(gone.IsSelected);
    }

    [Fact]
    public async Task NameOnlyRequirementNeedsMatching()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req(null!, "Some Body", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var body = plan.Entries.Single(entry => entry.Name == "Some Body");
        Assert.Equal(DependencyPlanEntryStatus.NeedsMatching, body.Status);
        Assert.Null(body.RemoteIdentity);
        Assert.StartsWith("name-only:", body.EntryKey);
        Assert.False(body.IsSelected);
    }

    [Fact]
    public async Task IncompatibleEdgeFlipsEntryToConflict()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[]
                {
                    Req("2", "Base", RemoteRequirementKind.Required),
                    Req("3", "Rival", RemoteRequirementKind.Incompatible)
                }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") },
                ["3"] = new[] { File("30") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base"),
                ["3"] = Mod("3", "Rival")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var rival = plan.Entries.Single(entry => entry.Ref.ModKey == "3");
        Assert.Equal(DependencyPlanEntryStatus.Conflict, rival.Status);
        Assert.False(rival.IsSelected);
    }

    [Fact]
    public async Task CyclesAreCollapsedNotRevisited()
    {
        var progressCalls = 0;
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "B", RemoteRequirementKind.Required) },
                ["2"] = new[] { Req("1", "Root", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "B")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"),
            Array.Empty<ModEntry>(),
            (done, total) => progressCalls++,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(1, plan.CollapsedCycles);
        Assert.Equal(2, progressCalls); // root + one child; the back edge is not re-fetched
    }

    [Fact]
    public async Task ProgressIsReportedPerVisitedMod()
    {
        var seen = new List<int>();
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "B", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "B")
            });

        await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"),
            Array.Empty<ModEntry>(),
            (done, _) => seen.Add(done),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, seen.Count);
        Assert.Equal(1, seen[0]);
        Assert.Equal(2, seen[1]);
    }

    // Round trip: resolve a small tree through the resolver, then feed the resolved plan into the
    // shared edge-derivation helper and confirm the edges it produces match the site data. This
    // is the same input the "save as mod list" builder and the install handoff consume.
    //
    // Tree: Root --Required--> Base, Root --Incompatible--> Rival, Base --Recommended--> Leaf.
    // Each target is reached through exactly one path, so the derived edge matches the site's
    // requirement that named it (no last-wins ambiguity to reason about here).
    [Fact]
    public async Task ResolvedPlanFeedsEdgeMapWithCorrectSourcesAndKinds()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[]
                {
                    Req("2", "Base", RemoteRequirementKind.Required, notes: "needed"),
                    Req("3", "Rival", RemoteRequirementKind.Incompatible)
                },
                ["2"] = new[] { Req("4", "Leaf", RemoteRequirementKind.Recommended) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") },
                ["3"] = new[] { File("30") },
                ["4"] = new[] { File("40") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base"),
                ["3"] = Mod("3", "Rival"),
                ["4"] = Mod("4", "Leaf")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var edges = DependencyPlanEdgeMap.Build(plan.Entries, rootModKey: "1");

        // Root → Base: kind Required, notes "needed" carried through the requirement notes.
        Assert.Contains(edges["1"], edge => edge.ModKey == "2" && edge.Kind == "Required" && edge.Notes == "needed");
        // Root → Rival: kind Incompatible carried verbatim as its string form.
        Assert.Contains(edges["1"], edge => edge.ModKey == "3" && edge.Kind == "Incompatible");
        // Base → Leaf: kind Recommended, sourced from Base (the direct parent in the chain), not the root.
        Assert.Contains(edges["2"], edge => edge.ModKey == "4" && edge.Kind == "Recommended");
        // The root has exactly its two direct requirements; nothing targets the root itself.
        Assert.Equal(2, edges["1"].Count);
        Assert.All(edges.Values, edgesList =>
            Assert.DoesNotContain(edgesList, edge => string.Equals(edge.ModKey ?? "", "1", System.StringComparison.OrdinalIgnoreCase)));
    }

    // The resolver fetches rich details (not just files) per mod; entries must carry the
    // site's display data and latest version label.
    [Fact]
    public async Task SiteDetailsEnrichEntries()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = new RemoteModMetadata(
                    new RemoteRef("fake", "testgame", "1"), "Root",
                    Summary: "A very good root.", Author: "Ada", Version: "2.5")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var root = plan.Entries.Single(entry => entry.Ref.ModKey == "1");
        Assert.Equal("A very good root.", root.Summary);
        Assert.Equal("Ada", root.Author);
        Assert.Equal("2.5", root.LatestSiteVersion);
    }

    // An installed mod whose local version differs from the site's latest must expose an
    // update affordance without leaving the Installed status.
    [Fact]
    public async Task InstalledModShowsUpdateAvailableWhenVersionsDiffer()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-1",
                Name = "Root",
                Remote = new RemoteRef("fake", "testgame", "1"),
                Version = "2.4"
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = new RemoteModMetadata(
                    new RemoteRef("fake", "testgame", "1"), "Root", Author: "Ada", Version: "2.5")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var root = plan.Entries.Single(entry => entry.Ref.ModKey == "1");
        Assert.Equal(DependencyPlanEntryStatus.Installed, root.Status);
        Assert.True(root.UpdateAvailable);
        Assert.Null(root.FileKey); // installed rows never pick a download file
    }

    // Same version on both sides: no update claim (the comparison is display-oriented).
    [Fact]
    public async Task InstalledModWithSameSiteVersionHasNoUpdate()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-1",
                Name = "Root",
                Remote = new RemoteRef("fake", "testgame", "1"),
                Version = "1.0"
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root") // version 1.0, matching the installed one
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var root = plan.Entries.Single(entry => entry.Ref.ModKey == "1");
        Assert.Equal(DependencyPlanEntryStatus.Installed, root.Status);
        Assert.False(root.UpdateAvailable);
    }

    // A transient details failure lands in FetchFailed (never auto-selected) and RetryEntryAsync
    // re-walks the entry once the site cooperates again.
    [Fact]
    public async Task DetailsFailureBecomesFetchFailedAndRetryRecovers()
    {
        var failingDetails = new HashSet<string> { "2" };
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required) },
                ["2"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base")
            },
            failingDetails: failingDetails);

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.FetchFailed, baseMod.Status);
        Assert.False(baseMod.IsSelected);

        failingDetails.Clear();
        await resolver.RetryEntryAsync(plan, baseMod, TestContext.Current.CancellationToken);

        Assert.Equal(DependencyPlanEntryStatus.Ready, baseMod.Status);
        Assert.True(baseMod.IsSelected);
        Assert.Equal("20", baseMod.FileKey);
    }

    // A transient requirements failure leaves the entry FetchFailed and its children are NOT
    // walked — retrying later must surface them.
    [Fact]
    public async Task RequirementsFailureBecomesFetchFailedWithoutChildren()
    {
        var failingRequirements = new HashSet<string> { "2" };
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required) },
                ["2"] = new[] { Req("3", "Leaf", RemoteRequirementKind.Required) },
                ["3"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") },
                ["3"] = new[] { File("30") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base"),
                ["3"] = Mod("3", "Leaf")
            },
            failingRequirements: failingRequirements);

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.FetchFailed, baseMod.Status);
        Assert.DoesNotContain(plan.Entries, entry => entry.Ref.ModKey == "3");

        failingRequirements.Clear();
        await resolver.RetryEntryAsync(plan, baseMod, TestContext.Current.CancellationToken);

        Assert.Equal(DependencyPlanEntryStatus.Ready, baseMod.Status);
        // The child that was never walked now appears in the plan (its selection flag is
        // not finalized by the retry pass; only the retried target is).
        var leaf = plan.Entries.Single(entry => entry.Ref.ModKey == "3");
        Assert.Equal(DependencyPlanEntryStatus.Ready, leaf.Status);
        Assert.Equal("30", leaf.FileKey);
    }

    // A requirement pins a file the site no longer offers: on a first walk the entry falls back
    // to the primary file and is Ready (the FileMismatch branch only fires on re-validation).
    [Fact]
    public async Task PinnedFileNotOfferedFallsBackToPrimary()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required, fileKey: "999") }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20"), File("21", primary: false) }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Ready, baseMod.Status);
        Assert.Equal("20", baseMod.FileKey); // primary, since pinned 999 is not offered
    }

    // File picking prefers a pinned requirement file over the primary when the site offers it.
    [Fact]
    public async Task PinnedFileIsPreferredWhenOffered()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required, fileKey: "21") }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20"), File("21", primary: false) }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Ready, baseMod.Status);
        Assert.Equal("21", baseMod.FileKey); // the pinned file, not the primary
    }

    // Regression: an installed mod whose site details 404 stays Installed — a 404 on an
    // already-present mod must not demote it to Unresolvable.
    [Fact]
    public async Task InstalledModSurvivesDetails404()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-2",
                Name = "Base",
                Remote = new RemoteRef("fake", "testgame", "2"),
                Version = "9.9"
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required) },
                ["2"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
                // mod 2 deliberately absent: details 404
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Installed, baseMod.Status);
        Assert.Equal("local-2", baseMod.LocalModId);
    }

    // Name-based matching is removed: a local mod whose name matches the requirement's mod name is
    // NOT recognized automatically (it has no Nexus reference). The requirement is identified on the
    // site and stays downloadable; the user must match it to the local mod through an action.
    [Fact]
    public async Task NameMatchIsNotAppliedAutomatically()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-bs",
                Name = "BodySlide and Outfit Studio"
                // deliberately no Remote: added from a local file
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "BodySlide and Outfit Studio", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "BodySlide and Outfit Studio")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        // Not matched to the local mod: it stays site-identified (downloadable), not Installed.
        Assert.NotEqual(DependencyPlanEntryStatus.Installed, baseMod.Status);
        Assert.Null(baseMod.LocalModId);
    }

    // Containment-based matching is also removed: a local name that merely contains the (shorter)
    // requirement name is not applied automatically.
    [Fact]
    public async Task ContainmentMatchIsNotAppliedAutomatically()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-bs",
                Name = "BodySlide and Outfit Studio"
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "BodySlide", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "BodySlide and Outfit Studio")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.NotEqual(DependencyPlanEntryStatus.Installed, baseMod.Status);
        Assert.Null(baseMod.LocalModId);
    }

    // Exact site+mod-key identity is still applied: an installed mod with the same site+mod key as
    // the requirement is recognized as already present (a factual identity, not a guess).
    [Fact]
    public async Task ExactSiteModKeyIdentityIsStillApplied()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-2",
                Name = "Base",
                Remote = new RemoteRef("fake", "testgame", "2")
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Base", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Base")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Installed, baseMod.Status);
        Assert.Equal("local-2", baseMod.LocalModId);
    }

    // A name-only requirement (no Nexus id) with no local candidate stays
    // NeedsMatching — the user must match it manually.
    [Fact]
    public async Task NameOnlyRequirementWithoutLocalCandidateStaysNeedsMatching()
    {
        var installed = new List<ModEntry>();

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req(null, "Some Mystery Mod", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, cancellationToken: TestContext.Current.CancellationToken);

        var mystery = plan.Entries.Single(entry => entry.Name == "Some Mystery Mod");
        Assert.Equal(DependencyPlanEntryStatus.NeedsMatching, mystery.Status);
        Assert.Null(mystery.RemoteIdentity);
        Assert.NotEqual("Some Mystery Mod", mystery.EntryKey);
    }

    [Fact]
    public async Task NexusNameOnlyRequirementStaysNeedsMatchingWithoutBuildingInvalidPageUrl()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req(null, "SKSE", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            },
            siteId: NexusSiteProvider.Id);

        var plan = await resolver.ResolveAsync(
            new RemoteRef(NexusSiteProvider.Id, "skyrimspecialedition", "1"),
            Array.Empty<ModEntry>(),
            cancellationToken: TestContext.Current.CancellationToken);

        var requirement = plan.Entries.Single(entry => entry.Name == "SKSE");
        Assert.Equal(DependencyPlanEntryStatus.NeedsMatching, requirement.Status);
        Assert.Null(requirement.RemoteIdentity);
        Assert.Equal(NexusSiteProvider.Id, requirement.SiteId);
        Assert.NotNull(requirement.ContextualRequirementKey);
    }

    [Fact]
    public async Task DuplicateNameOnlyRequirementsRemainDistinctAndCannotBeRememberedByAmbiguousContext()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[]
                {
                    Req(null, "Shared Requirement", RemoteRequirementKind.Required),
                    Req(null, "Shared Requirement", RemoteRequirementKind.Required)
                }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(),
            cancellationToken: TestContext.Current.CancellationToken);

        var requirements = plan.Entries.Where(entry => entry.Name == "Shared Requirement").ToList();
        Assert.Equal(2, requirements.Count);
        Assert.NotEqual(requirements[0].EntryKey, requirements[1].EntryKey);
        Assert.All(requirements, entry => Assert.Null(entry.ContextualRequirementKey));
        Assert.All(requirements, entry => Assert.Null(entry.RemoteIdentity));
    }

    // A remembered manual association is NOT applied automatically by the resolver: the local mod
    // is still surfaced as an unmatched/unidentified requirement; the user re-applies the match
    // through an action (the dialog pre-selects the remembered mod).
    [Fact]
    public async Task RememberedAssociationIsNotAppliedAutomatically()
    {
        var installed = new List<ModEntry>
        {
            new()
            {
                Id = "local-bs",
                Name = "My Local BodySlide Build"
                // No Remote ref and no name overlap with the requirement — only the remembered link knows it.
            }
        };

        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "BodySlide", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "BodySlide and Outfit Studio")
            });

        var matchLinks = new Dictionary<string, ModMatchLink>(StringComparer.Ordinal)
        {
            [ModMatchStore.LinkKey("fake", "2")] = new ModMatchLink("fake", "2", "local-bs")
        };

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), installed, matchLinks: matchLinks,
            cancellationToken: TestContext.Current.CancellationToken);

        var baseMod = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        // Not auto-matched to the local mod despite the remembered association.
        Assert.NotEqual(DependencyPlanEntryStatus.Installed, baseMod.Status);
        Assert.Null(baseMod.LocalModId);
    }

    // A remembered key-to-key (fold) association is NOT applied automatically: the requirement is
    // fetched on its own (key "2") and is not redirected into the target (key "3"); nothing is
    // folded into the target. The user re-applies the fold through an action.
    [Fact]
    public async Task RememberedFoldIsNotAppliedAutomatically()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "BodySlide", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") }
            },
            // Key "2" is deliberately absent: fetched on its own it 404s (Unresolvable).
            // Key "3" exists but is no longer required by anything (the fold no longer redirects).
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root")
            });

        var matchLinks = new Dictionary<string, ModMatchLink>(StringComparer.Ordinal)
        {
            [ModMatchStore.LinkKey("fake", "2")] = new ModMatchLink("fake", "2", string.Empty, null, "3")
        };

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(),
            matchLinks: matchLinks,
            cancellationToken: TestContext.Current.CancellationToken);

        // The requirement is walked under its own key, not redirected to the fold target.
        var entry = plan.Entries.Single(candidate => candidate.Ref.ModKey == "2");
        Assert.Equal(DependencyPlanEntryStatus.Unresolvable, entry.Status);
        Assert.Empty(entry.FoldedIn);
        // The target key was never fetched and nothing was folded into it.
        Assert.DoesNotContain(plan.Entries, candidate => candidate.Ref.ModKey == "3");
    }

    // When no alias exists a named requirement is fetched on its own and produces no fold.
    [Fact]
    public async Task WithoutAlias_NoFoldIsRecorded()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "BodySlide", RemoteRequirementKind.Required) },
                ["2"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "BodySlide and Outfit Studio")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(),
            cancellationToken: TestContext.Current.CancellationToken);

        var entry = plan.Entries.Single(candidate => candidate.Ref.ModKey == "2");
        Assert.Empty(entry.FoldedIn);
    }

    // An "obsolete" link must NOT redirect the requirement: the referenced mod keeps its own row.
    [Fact]
    public async Task ObsoleteLinkDoesNotRedirectTheRequirement()
    {
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "Old Mod", RemoteRequirementKind.Required) },
                ["2"] = Array.Empty<RemoteRequirement>()
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "Old Mod")
            });

        var matchLinks = new Dictionary<string, ModMatchLink>(StringComparer.Ordinal)
        {
            [ModMatchStore.LinkKey("fake", "2")] = new ModMatchLink(
                "fake", "2", string.Empty, null, "99", "Old Mod", "Newer Mod", ModMatchStore.KindObsolete)
        };

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(),
            matchLinks: matchLinks,
            cancellationToken: TestContext.Current.CancellationToken);

        // The obsolete mod still resolves on its own (it is installed and kept).
        var entry = plan.Entries.Single(candidate => candidate.Ref.ModKey == "2");
        Assert.Empty(entry.FoldedIn);
        // Nothing was folded into the target key.
        Assert.DoesNotContain(plan.Entries, candidate => candidate.Ref.ModKey == "99");
    }

    [Fact]
    public async Task IncompatibleConflictIsRecordedOnBothSides()
    {
        // Root requires A and B; A declares B incompatible. Both entries must end up Conflicting,
        // each pointing at the other.
        var (resolver, _) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[]
                {
                    Req("2", "A", RemoteRequirementKind.Required),
                    Req("3", "B", RemoteRequirementKind.Required)
                },
                ["2"] = new[] { Req("3", "B", RemoteRequirementKind.Incompatible, notes: "replaces B") }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") },
                ["3"] = new[] { File("30") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "A"),
                ["3"] = Mod("3", "B")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);

        var a = plan.Entries.Single(entry => entry.Ref.ModKey == "2");
        var b = plan.Entries.Single(entry => entry.Ref.ModKey == "3");
        Assert.Equal("3", a.ConflictWithKey);
        Assert.Equal("B", a.ConflictWithName);
        Assert.Equal("2", b.ConflictWithKey);
        Assert.Equal("A", b.ConflictWithName);
        // Both rows project to the Conflicting lifecycle status.
        a.ProjectRowStatus();
        b.ProjectRowStatus();
        Assert.Equal(RowStatus.Conflicting, a.RowStatus);
        Assert.Equal(RowStatus.Conflicting, b.RowStatus);
    }

    [Fact]
    public async Task IncrementalResolveAppendsOnlyBrandNewEntries()
    {
        var (resolver, provider) = Create(
            requirements: new Dictionary<string, IReadOnlyList<RemoteRequirement>>
            {
                ["1"] = new[] { Req("2", "B", RemoteRequirementKind.Required) }
            },
            files: new Dictionary<string, IReadOnlyList<RemoteFileMetadata>>
            {
                ["1"] = new[] { File("10") },
                ["2"] = new[] { File("20") }
            },
            mods: new Dictionary<string, RemoteModMetadata>
            {
                ["1"] = Mod("1", "Root"),
                ["2"] = Mod("2", "B")
            });

        var plan = await resolver.ResolveAsync(
            new RemoteRef("fake", "testgame", "1"), Array.Empty<ModEntry>(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, plan.Entries.Count);
        var existingB = plan.Entries.Single(entry => entry.Ref.ModKey == "2");

        // The site now also requires C from the root (only the root's requirement list changes).
        provider.Requirements["1"] = new[]
        {
            Req("2", "B", RemoteRequirementKind.Required),
            Req("3", "C", RemoteRequirementKind.Required)
        };
        provider.Files["3"] = new[] { File("30") };
        provider.Mods["3"] = Mod("3", "C");

        var updated = await resolver.ResolveIncrementalAsync(
            plan, new[] { "1" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, updated.Entries.Count);
        var newC = updated.Entries.Single(entry => entry.Ref.ModKey == "3");
        Assert.Equal(RemoteRequirementKind.Required, newC.Kind);
        // The existing entry instance is preserved (identity and data untouched).
        Assert.Same(existingB, updated.Entries.Single(entry => entry.Ref.ModKey == "2"));
    }
}
