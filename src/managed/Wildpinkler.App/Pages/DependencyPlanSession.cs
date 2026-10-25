using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Pages;

/// <summary>
/// A single in-progress "install with dependencies" session. The page owns the plan; the app shell
/// uses the session registry to route an incoming site-issued download link (the free-tier browser
/// relay) back to the matching plan entry.
/// </summary>
public sealed class DependencyPlanSession
{
    private readonly object _gate = new();

    public DependencyPlanSession(string siteId, string gameKey, string rootModKey)
    {
        SiteId = siteId;
        GameKey = gameKey;
        RootModKey = rootModKey;
    }

    /// <summary>
    /// Creates a session that observes an existing entries collection (the plan page's list) so the
    /// shell's relay lookups and the page's rows are the same objects.
    /// </summary>
    public DependencyPlanSession(string siteId, string gameKey, string rootModKey,
        ObservableCollection<DependencyPlanEntry> entries)
        : this(siteId, gameKey, rootModKey)
    {
        Entries = entries;
    }

    public string SiteId { get; }
    public string GameKey { get; }
    public string RootModKey { get; }

    public ObservableCollection<DependencyPlanEntry> Entries { get; set; } = new();

    /// <summary>Raised on the UI thread when an entry's state changed enough that the user should see it.</summary>
    public event Action? EntryUpdated;

    public void MarkDownloadStarted(string modKey, string? fileKey)
    {
        lock (_gate)
        {
            var entry = Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.RemoteIdentity?.ModKey, modKey, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return;
            entry.DownloadState = DependencyPlanEntryDownloadState.Downloading;
            entry.StatusDetail = "Download started from the site-issued link.";
        }
        EntryUpdated?.Invoke();
    }

    public void MarkInstalled(string modKey)
    {
        lock (_gate)
        {
            var entry = Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.RemoteIdentity?.ModKey, modKey, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return;
            entry.DownloadState = DependencyPlanEntryDownloadState.Installed;
            entry.IsSelected = false;
        }
        EntryUpdated?.Invoke();
    }

    public bool MatchesModKey(string modKey) =>
        Entries.Any(entry => string.Equals(entry.RemoteIdentity?.ModKey, modKey, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The set of live plan sessions. One per open plan page; the app shell consults this when an
/// incoming download link could belong to an open plan rather than the ordinary download queue.
/// </summary>
public static class DependencyPlanSessionRegistry
{
    private static readonly List<DependencyPlanSession> _sessions = new();
    private static readonly object _gate = new();

    public static void Add(DependencyPlanSession session)
    {
        lock (_gate) _sessions.Add(session);
    }

    public static void Remove(DependencyPlanSession session)
    {
        lock (_gate) _sessions.Remove(session);
    }

    /// <summary>The session whose plan contains this mod, if any open plan does.</summary>
    public static DependencyPlanSession? FindByModKey(string modKey)
    {
        lock (_gate) return _sessions.FirstOrDefault(session => session.MatchesModKey(modKey));
    }
}
