using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// One node in the archive preview tree. The control renders every node in one flat
/// <see cref="ListView"/> and uses <see cref="Depth"/> to indent, so expansion is a matter of
/// inserting/removing rows around the expanded folder rather than a nested ItemsControl.
/// </summary>
public sealed partial class ArchiveFileTreeNode : ObservableObject
{
    private const string FolderCollapsed = "\uE8B7";
    private const string FileGlyph = "\uE8A5";

    private string _sizeText = string.Empty;
    private string _glyph;
    private Windows.UI.Text.FontWeight _fontWeight = FontWeights.SemiBold;

    public ArchiveFileTreeNode(string name, string path, int depth, bool isDirectory)
    {
        Name = name;
        Path = path;
        Depth = depth;
        IsDirectory = isDirectory;
        _glyph = isDirectory ? FolderCollapsed : FileGlyph;
        _fontWeight = isDirectory ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>The node's display name (the immediate segment, not the full path).</summary>
    public string Name { get; }

    /// <summary>The node's archive-relative path, using '/' separators (top level has no prefix).</summary>
    public string Path { get; }

    /// <summary>0-based indent level.</summary>
    public int Depth { get; }

    /// <summary>True for a folder node (which may be expanded).</summary>
    public bool IsDirectory { get; }

    /// <summary>True once the node's children have been fetched (folder nodes only).</summary>
    public bool IsLoaded { get; set; }

    /// <summary>True while the node's children are being fetched (the glyph flips to a spinner).</summary>
    public bool IsLoading { get; set; }

    /// <summary>Left margin (px) computed from <see cref="Depth"/> for the flat-tree layout.</summary>
    public Microsoft.UI.Xaml.Thickness Indent =>
        new(Depth * 18, 0, 0, 0);

    /// <summary>Human file size ("" for folders / unknown).</summary>
    public string SizeText
    {
        get => _sizeText;
        set => SetProperty(ref _sizeText, value);
    }

    /// <summary>Segoe MDL2 glyph: folder (collapsed), chevron-down (expanded), hourglass (loading), file.</summary>
    public string Glyph
    {
        get => _glyph;
        set => SetProperty(ref _glyph, value);
    }

    /// <summary>Font weight for the name (folders semibold, files normal).</summary>
    public Windows.UI.Text.FontWeight FontWeight
    {
        get => _fontWeight;
        set => SetProperty(ref _fontWeight, value);
    }
}

/// <summary>
/// Read-only, lazy preview of a non-FOMOD archive's layout, shown in the Profile Content View's
/// details pane. Top-level directories are listed immediately (from <see cref="ArchiveLayout"/>);
/// expanding a directory asks the page (via the injected <see cref="ListChildrenAsync"/>) for its
/// entries and appends them to the flat tree. No selection, no writes - purely informational.
/// </summary>
public sealed partial class ArchiveFileTree : UserControl
{
    private const string FolderCollapsed = "\uE8B7";
    private const string FolderExpanded = "\uE826";
    private const string LoadingGlyph = "\uE700";

    private readonly List<ArchiveFileTreeNode> _flat = new();
    private Func<string, Task<IReadOnlyList<string>>>? _listChildren;

    public ArchiveFileTree()
    {
        InitializeComponent();
        TreeList.ItemsSource = _flat;
        TreeList.Tapped += Row_Tapped;
    }

    /// <summary>
    /// Sets the tree to the archive's top-level entries (directories plus loose root files),
    /// fetched eagerly so the initial view is a pure tree: each row either expands into its
    /// children or is a file — no directory ever appears twice (once as a row, once under its
    /// parent). The child lister callback is called with an archive-relative directory path when
    /// the user expands a folder whose children were not fetched upfront; it returns the names
    /// of the immediate children (folders and files) under that path.
    /// </summary>
    public async Task SetLayoutAsync(ArchiveLayout layout, Func<string, Task<IReadOnlyList<string>>>? listChildren)
    {
        _listChildren = listChildren;
        _allPaths = null;

        IReadOnlyList<string>? allPaths = null;
        if (listChildren is not null)
        {
            try
            {
                allPaths = await listChildren(string.Empty);
            }
            catch
            {
                // The eager fetch is an optimization; expansion falls back to on-demand listing.
            }
        }

        var entries = TopLevelEntries(layout.Directories, allPaths ?? Array.Empty<string>());
        _flat.Clear();
        foreach (var (name, path, isDirectory) in entries)
            _flat.Add(new ArchiveFileTreeNode(name, path, 0, isDirectory));
        _allPaths = allPaths;
        TreeList.ItemsSource = null;
        TreeList.ItemsSource = _flat;
        TreeList.Visibility = _flat.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _flat.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // The eagerly-fetched archive file list; null until the initial fetch completes (or failed).
    private IReadOnlyList<string>? _allPaths;

    /// <summary>
    /// Pure step (unit-testable): the top-level rows of the archive — the distinct first-segment
    /// directories (from the layout's containing directories) plus any file that sits directly
    /// at the archive root. Order is name-sorted, case-insensitive, folders first.
    /// </summary>
    internal static List<(string Name, string Path, bool IsDirectory)> TopLevelEntries(
        IReadOnlyList<string> directories, IEnumerable<string>? allFilePaths)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            var clean = directory.Replace('\\', '/');
            if (clean.Length == 0)
                continue;
            var slash = clean.IndexOf('/', StringComparison.Ordinal);
            var top = slash < 0 ? clean : clean[..slash];
            dirs.Add(top);
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in allFilePaths ?? Array.Empty<string>())
        {
            var clean = raw.Replace('\\', '/');
            if (clean.Length == 0 || clean.Contains('/', StringComparison.Ordinal))
                continue;
            files.Add(clean);
        }

        var entries = new List<(string Name, string Path, bool IsDirectory)>();
        foreach (var dir in dirs)
            entries.Add((dir, dir, true));
        foreach (var file in files.Where(f => !dirs.Contains(f)))
            entries.Add((file, file, false));
        return entries
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Clears every row and restores the empty state (e.g. when the tree is detached mid-fetch).</summary>
    public void Clear()
    {
        _flat.Clear();
        _allPaths = null;
        TreeList.ItemsSource = null;
        TreeList.ItemsSource = _flat;
        TreeList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Toggles a folder's expansion: collapsing removes its descendants from the flat list;
    /// expanding appends its immediate children right after it.
    /// </summary>
    private async void Row_Tapped(object sender, TappedRoutedEventArgs args)
    {
        // The node tag lives on the row's inner Grid, but the Tapped event's original source is
        // whichever child the pointer hit (the glyph or a text block). Walk up to the tagged row.
        var node = FindNodeFromSource(args.OriginalSource);
        if (node is null || !node.IsDirectory || node.IsLoading)
            return;

        if (!node.IsLoaded)
        {
            IReadOnlyList<string> allPaths;
            if (_allPaths is not null)
            {
                // The eager fetch already has the full file list; expand without a network hop.
                allPaths = _allPaths;
            }
            else
            {
                if (_listChildren is null)
                    return;
                node.IsLoading = true;
                node.Glyph = LoadingGlyph;
                try
                {
                    allPaths = await _listChildren(node.Path);
                }
                catch
                {
                    // Leave the glyph as the folder mark; the user can tap again to retry.
                    node.Glyph = FolderCollapsed;
                    node.IsLoading = false;
                    return;
                }
                node.IsLoading = false;
            }
            node.IsLoaded = true;
            ExpandWith(node, allPaths);
        }
        else
        {
            Collapse(node);
        }
    }

    /// <summary>
    /// Appends the node's immediate children (one level) right after <paramref name="node"/>.
    /// <paramref name="allPaths"/> are archive-relative paths; only entries that live directly
    /// under the node's path are surfaced, so expanding a folder never flattens its descendants.
    /// </summary>
    private void ExpandWith(ArchiveFileTreeNode node, IReadOnlyList<string> allPaths)
    {
        var nodeIndex = _flat.IndexOf(node);
        if (nodeIndex < 0)
            return;
        node.Glyph = FolderExpanded;

        var prefix = node.Path.Length == 0
            ? string.Empty
            : node.Path + "/";

        var children = ImmediateChildren(prefix, allPaths);

        var insertAt = nodeIndex + 1;
        foreach (var (name, childPath) in children)
        {
            var isDirectory = allPaths.Any(candidate =>
                candidate.Replace('\\', '/').StartsWith(childPath + "/", StringComparison.OrdinalIgnoreCase));
            _flat.Insert(insertAt++, new ArchiveFileTreeNode(name, childPath, node.Depth + 1, isDirectory));
        }
        Refresh();
    }

    /// <summary>
    /// Pure step (unit-testable): the immediate children of the folder identified by
    /// <paramref name="folderPath"/> (archive-relative, '/'-separated, always with a trailing
    /// '/' — the empty string means the archive root), computed from a full list of archive-relative
    /// file paths. Only entries directly under the folder are returned — the first path segment below
    /// it (a folder when it has descendants, a file when it doesn't), deduped and ordered — so
    /// expanding a folder never surfaces grandchildren.
    /// </summary>
    internal static List<(string Name, string Path)> ImmediateChildren(string folderPath, IEnumerable<string> allPaths)
    {
        var children = new List<(string Name, string Path)>();
        foreach (var raw in allPaths)
        {
            var clean = raw.Replace('\\', '/');
            if (folderPath.Length > 0 && !clean.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var remainder = clean[folderPath.Length..];
            var slash = remainder.IndexOf('/', StringComparison.Ordinal);
            // A folder's remainder still contains a deeper segment; a file's does not.
            var childName = slash >= 0 ? remainder[..slash] : remainder;
            if (childName.Length == 0)
                continue;
            var childPath = folderPath + childName;
            if (children.Any(c => string.Equals(c.Path, childPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            children.Add((childName, childPath));
        }
        return children
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Collapse(ArchiveFileTreeNode node)
    {
        var index = _flat.IndexOf(node);
        if (index < 0)
            return;
        node.Glyph = FolderCollapsed;

        var removeStart = index + 1;
        var removeEnd = removeStart;
        while (removeEnd < _flat.Count && _flat[removeEnd].Depth > node.Depth)
            removeEnd++;
        for (var i = removeEnd - 1; i >= removeStart; i--)
            _flat.RemoveAt(i);
        Refresh();
    }

    private void Refresh()
    {
        TreeList.ItemsSource = null;
        TreeList.ItemsSource = _flat;
    }

    /// <summary>
    /// Walks up from the tapped element to the row's Grid carrying the node tag, because the
    /// <c>Tapped</c> event's original source is the inner child (glyph or text) that was hit,
    /// not the tagged row container.
    /// </summary>
    private static ArchiveFileTreeNode? FindNodeFromSource(object originalSource)
    {
        for (var element = originalSource as UIElement; element is not null; element = GetParent(element))
        {
            if (element is FrameworkElement { Tag: ArchiveFileTreeNode node })
                return node;
        }
        return null;
    }

    private static UIElement? GetParent(UIElement element)
        => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) as UIElement;
}
