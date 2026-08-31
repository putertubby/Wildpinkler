using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

/// <summary>One tree node's backing data: the view/relative-path it represents, resolved once when its parent was expanded.</summary>
internal sealed class PreviewNodeInfo
{
    public required MergedView View { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required string ActiveBranchPath { get; init; }
    public required int ActiveBranchIndex { get; init; }
    public required IReadOnlyList<MergedEntryOrigin> ShadowedOrigins { get; init; }

    public string Glyph => IsDirectory ? "\uE8B7" : "\uE7C3";
}

// Merged content preview: rebuilt lazily, only while the Content section is the visible one.
public sealed partial class ProfilesPage
{
    private readonly MergedViewPreviewService _previewService = AppServices.MergedViewPreviewService;

    private void ResetMergedContent()
    {
        ContentTree.RootNodes.Clear();
        ShowEmptyMergedSelection();
    }

    /// <summary>Called after any commit, so an open preview never shows a stale merge.</summary>
    private void RefreshMergedContentIfVisible()
    {
        if (ContentSection.Visibility == Visibility.Visible)
            RebuildMergedContentRoots();
    }

    /// <summary>Identifies a node by what it represents, independent of the (recreated-on-refresh) TreeViewNode instance.</summary>
    private static string NodeKey(PreviewNodeInfo info) => $"{LaunchTargetResolver.NormalizeMountPath(info.View.MountPath)}|{info.RelativePath}";

    private static HashSet<string> CollectExpandedKeys(IEnumerable<TreeViewNode> nodes)
    {
        var keys = new HashSet<string>();
        void Walk(TreeViewNode node)
        {
            if (node.IsExpanded && node.Content is PreviewNodeInfo info)
                keys.Add(NodeKey(info));
            foreach (var child in node.Children)
                Walk(child);
        }

        foreach (var node in nodes)
            Walk(node);
        return keys;
    }

    private void RebuildMergedContentRoots()
    {
        var expandedKeys = CollectExpandedKeys(ContentTree.RootNodes);
        var selectedKey = ContentTree.SelectedNode?.Content is PreviewNodeInfo selectedInfo ? NodeKey(selectedInfo) : null;

        ShowEmptyMergedSelection();

        if (SelectedTarget is not { } target)
        {
            ContentTree.RootNodes.Clear();
            return;
        }

        TreeViewNode? selectedNode = null;
        var desiredViews = target.MergedViews.ToList();
        for (var index = ContentTree.RootNodes.Count - 1; index >= 0; index--)
        {
            if (ContentTree.RootNodes[index].Content is not PreviewNodeInfo info ||
                !desiredViews.Any(view => string.Equals(NodeKey(info), NodeKey(CreatePreviewNodeInfo(view)), StringComparison.OrdinalIgnoreCase)))
                ContentTree.RootNodes.RemoveAt(index);
        }

        for (var index = 0; index < desiredViews.Count; index++)
        {
            var view = desiredViews[index];
            var desiredInfo = CreatePreviewNodeInfo(view);
            var existingIndex = -1;
            for (var search = index; search < ContentTree.RootNodes.Count; search++)
            {
                if (ContentTree.RootNodes[search].Content is PreviewNodeInfo info &&
                    string.Equals(NodeKey(info), NodeKey(desiredInfo), StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = search;
                    break;
                }
            }

            TreeViewNode node;
            if (existingIndex < 0)
            {
                node = new TreeViewNode { Content = desiredInfo, HasUnrealizedChildren = true };
                ContentTree.RootNodes.Insert(index, node);
            }
            else
            {
                node = ContentTree.RootNodes[existingIndex];
                node.Content = desiredInfo;
                if (existingIndex != index)
                {
                    ContentTree.RootNodes.RemoveAt(existingIndex);
                    ContentTree.RootNodes.Insert(index, node);
                }
            }

            RestoreExpansion(node, expandedKeys, selectedKey, ref selectedNode);
        }

        if (selectedNode is not null)
            ContentTree.SelectedNode = selectedNode;
    }

    private static PreviewNodeInfo CreatePreviewNodeInfo(MergedView view) => new()
    {
        View = view,
        RelativePath = string.Empty,
        Name = view.Name.Length == 0 ? view.MountPath : view.Name,
        IsDirectory = true,
        ActiveBranchPath = view.Branches.FirstOrDefault() ?? string.Empty,
        ActiveBranchIndex = 0,
        ShadowedOrigins = Array.Empty<MergedEntryOrigin>()
    };

    /// <summary>Re-expands (and, if applicable, lazily populates) a freshly-rebuilt node that was previously expanded/selected.</summary>
    private void RestoreExpansion(TreeViewNode node, HashSet<string> expandedKeys, string? selectedKey, ref TreeViewNode? selectedNode)
    {
        if (node.Content is not PreviewNodeInfo info)
            return;

        var key = NodeKey(info);
        if (key == selectedKey)
            selectedNode = node;

        if (!expandedKeys.Contains(key))
            return;

        node.IsExpanded = true;
        if (node.HasUnrealizedChildren)
            PopulateChildren(node, info);

        foreach (var child in node.Children)
            RestoreExpansion(child, expandedKeys, selectedKey, ref selectedNode);
    }

    private void PopulateChildren(TreeViewNode node, PreviewNodeInfo info)
    {
        node.HasUnrealizedChildren = false;
        foreach (var entry in _previewService.ListLevel(info.View, info.RelativePath))
        {
            var childRelativePath = info.RelativePath.Length == 0 ? entry.Name : $"{info.RelativePath}\\{entry.Name}";
            node.Children.Add(new TreeViewNode
            {
                Content = new PreviewNodeInfo
                {
                    View = info.View,
                    RelativePath = childRelativePath,
                    Name = entry.Name,
                    IsDirectory = entry.IsDirectory,
                    ActiveBranchPath = entry.ActiveBranchPath,
                    ActiveBranchIndex = entry.ActiveBranchIndex,
                    ShadowedOrigins = entry.ShadowedOrigins
                },
                HasUnrealizedChildren = entry.IsDirectory
            });
        }
    }

    private void ContentTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not PreviewNodeInfo info || !args.Node.HasUnrealizedChildren)
            return;

        PopulateChildren(args.Node, info);
    }

    private void ContentTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
    }

    private void ContentTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (ContentTree.SelectedNode?.Content is not PreviewNodeInfo info)
        {
            ShowEmptyMergedSelection();
            return;
        }

        EmptySelectionText.Visibility = Visibility.Collapsed;
        DetailName.Text = info.Name;
        DetailOrigin.Text = info.ActiveBranchPath.Length == 0
            ? "No branch declares this entry."
            : $"Active branch (priority {info.ActiveBranchIndex + 1}): {info.ActiveBranchPath}";

        if (info.ShadowedOrigins.Count == 0)
        {
            DetailShadowedHeader.Visibility = Visibility.Collapsed;
            DetailShadowedList.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailShadowedHeader.Visibility = Visibility.Visible;
            DetailShadowedList.Visibility = Visibility.Visible;
            DetailShadowedList.ItemsSource = info.ShadowedOrigins
                .Select(origin => $"Priority {origin.BranchIndex + 1}: {origin.BranchPath}")
                .ToList();
        }
    }

    private void ShowEmptyMergedSelection()
    {
        EmptySelectionText.Visibility = Visibility.Visible;
        DetailName.Text = string.Empty;
        DetailOrigin.Text = string.Empty;
        DetailShadowedHeader.Visibility = Visibility.Collapsed;
        DetailShadowedList.Visibility = Visibility.Collapsed;
    }
}
