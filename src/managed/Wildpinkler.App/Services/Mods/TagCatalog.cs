using System;
using System.Collections.Generic;
using System.Linq;

namespace Wildpinkler.App.Services;

/// <summary>
/// The user's global mod-tag set: the "defaults" offered everywhere tags are managed. Stored on
/// <see cref="AppSettings.CustomTags"/> (null until first touched). Pure and testable: every
/// operation is a static function over an ordered list so the UI layers just read/write it.
/// </summary>
public static class TagCatalog
{
    /// <summary>The function defaults seeded on first use; all are removable and addable.</summary>
    public static readonly IReadOnlyList<string> DefaultTags =
    [
        "UI", "Gameplay", "Graphics", "Audio", "Immersion", "Utility", "Framework", "Performance"
    ];

    /// <summary>
    /// Returns the effective tag list, seeding it with the defaults when none has been set yet
    /// (i.e. <paramref name="current"/> is null/empty and <paramref name="seedsDefaults"/> is true).
    /// </summary>
    public static List<string> Ensure(List<string>? current, bool seedsDefaults = true)
    {
        if (current is { Count: > 0 })
            return current;
        return seedsDefaults
            ? new List<string>(DefaultTags)
            : new List<string>();
    }

    /// <summary>
    /// Adds a tag to the list (deduplicated case-insensitively), preserving existing order.
    /// Returns true when the tag was added.
    /// </summary>
    public static bool Add(List<string> tags, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || tags.Any(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;
        tags.Add(trimmed);
        return true;
    }

    /// <summary>
    /// Removes a tag from the list (case-insensitive). This only stops offering the tag as a
    /// default; mods that already carry it keep it. Returns true when a tag was removed.
    /// </summary>
    public static bool Remove(List<string> tags, string name)
    {
        var trimmed = name.Trim();
        for (var i = tags.Count - 1; i >= 0; i--)
        {
            if (string.Equals(tags[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                tags.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Resets the tag list back to the function defaults.</summary>
    public static List<string> Reset(List<string> tags)
    {
        tags.Clear();
        tags.AddRange(DefaultTags);
        return tags;
    }

    /// <summary>True when a mod's tags overlap the active filter set (OR semantics).</summary>
    public static bool MatchesAny(IReadOnlyList<string> modTags, IReadOnlyList<string> activeTags)
        => activeTags.Count == 0 || modTags.Any(t =>
            activeTags.Any(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase)));
}
