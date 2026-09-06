using System;
using System.Collections.Generic;
using Wildpinkler.App.Pages;

namespace Wildpinkler.App;

// Single source of truth for the functionality subsections, shared by the nav pane and the landing page cards.
public static class NavigationCatalog
{
    public sealed record Entry(string Tag, string Label, Type PageType, string IconGlyph);

    public const string ModsTag = "Mods";
    public const string GamesTag = "Games";
    public const string ProfilesTag = "Profiles";
    public const string ModListsTag = "ModLists";
    public const string DownloadsTag = "Downloads";

    public static IReadOnlyList<Entry> Sections { get; } = new[]
    {
        new Entry(GamesTag, "Games", typeof(GamesPage), "\uE7FC"),
        new Entry(ProfilesTag, "Profiles", typeof(ProfilesPage), "\uE77B"),
        new Entry(DownloadsTag, "Downloads", typeof(DownloadsPage), "\uE896"),
        new Entry(ModsTag, "Mods", typeof(ModsPage), "\uE7B8"),
        new Entry(ModListsTag, "Mod lists", typeof(ModListsPage), "\uE8A5"),
        new Entry("Tools", "Tools", typeof(ToolsPage), "\uE90F"),
        new Entry("Updates", "Updates & Tracked", typeof(UpdatesPage), "\uE895"),
    };
}
