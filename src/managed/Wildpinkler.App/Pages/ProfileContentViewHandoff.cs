using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Pages;

/// <summary>
/// One mod an entry point is handing to the <see cref="ProfileContentView"/> for planning and
/// install: a library mod (archived) and/or a site link to download it. The remote reference is
/// the row's identity; the entry is null for site mods that have no local library record yet.
/// </summary>
public sealed record ProfileContentViewIncomingMod(ModEntry? Entry, RemoteRef? Remote, RemoteLink? DownloadLink, bool ResolveDependencies = false);

/// <summary>
/// The one-shot handoff from an add-mod entry point (the profile page's add buttons, the remote
/// download review dialog) to the profile content view: navigate to the page and seed a session
/// row per incoming mod - downloading when a site link is supplied, resolved in place otherwise.
/// </summary>
public static class ProfileContentViewHandoff
{
    private static (string ProfileId, List<ProfileContentViewIncomingMod> Mods)? _pending;

    public static void Set(string profileId, IReadOnlyList<ProfileContentViewIncomingMod> mods)
    {
        _pending = (profileId, mods.ToList());
    }

    public static (string ProfileId, List<ProfileContentViewIncomingMod> Mods)? Take()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }
}
