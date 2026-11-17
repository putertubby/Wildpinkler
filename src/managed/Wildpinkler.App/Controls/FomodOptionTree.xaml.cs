using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class FomodOptionNode : ObservableObject
{
    private const string ChevronCollapsed = "\uE70D";
    private const string ChevronExpanded = "\uE706";

    public static string ChevronCollapsedGlyph() => ChevronCollapsed;
    public static string ChevronExpandedGlyph() => ChevronExpanded;

    private string _name;
    private string _description = string.Empty;
    private string _typeText = string.Empty;
    private string _groupRuleText = string.Empty;
    private string _expandGlyph = ChevronCollapsed;
    private Windows.UI.Text.FontWeight _fontWeight = FontWeights.SemiBold;
    private bool _isContainer = true;
    private bool _isChecked;
    private bool _isEnabled = true;
    private bool _isExpanded;

    public FomodOptionNode(string name, int depth, bool isContainer)
    {
        _name = name;
        Depth = depth;
        _isContainer = isContainer;
        _fontWeight = isContainer ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public string Key { get; init; } = string.Empty;
    public FomodInstallStep? Step { get; set; }
    public FomodGroup? Group { get; set; }
    public FomodPlugin? Plugin { get; set; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public int Depth { get; }

    public bool IsContainer
    {
        get => _isContainer;
        set => SetProperty(ref _isContainer, value);
    }

    public Microsoft.UI.Xaml.Thickness Indent => new(Depth * 18, 0, 0, 0);

    public string Description
    {
        get => _description;
        set
        {
            SetProperty(ref _description, value);
            OnPropertyChanged(nameof(HasDescription));
        }
    }

    public bool HasDescription => _description.Length > 0;

    public string TypeText
    {
        get => _typeText;
        set
        {
            SetProperty(ref _typeText, value);
            OnPropertyChanged(nameof(ShowTypeBadge));
        }
    }

    public bool ShowTypeBadge => _typeText.Length > 0;

    public string GroupRuleText
    {
        get => _groupRuleText;
        set
        {
            SetProperty(ref _groupRuleText, value);
            OnPropertyChanged(nameof(HasGroupRule));
        }
    }

    public bool HasGroupRule => _groupRuleText.Length > 0;

    public Windows.UI.Text.FontWeight FontWeight
    {
        get => _fontWeight;
        set => SetProperty(ref _fontWeight, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string ExpandGlyph
    {
        get => _expandGlyph;
        set => SetProperty(ref _expandGlyph, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    // The plugin's preview image (leaf rows only); null when the archive has no usable image.
    // Loaded off-thread and set on the UI thread by the tree's LoadImagesAsync.
    private ImageSource? _thumbnail;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            SetProperty(ref _thumbnail, value);
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;
}

public sealed partial class FomodOptionTree : UserControl
{
    private readonly List<FomodOptionNode> _flat = new();
    private readonly FomodSelectionResolver _resolver;
    private FomodModule? _module;
    private IFomodFileStateProvider? _files;
    private bool _recomputing;

    public FomodOptionTree(IFomodVersionProvider? versions = null)
    {
        InitializeComponent();
        TreeList.ItemsSource = _flat;
        TreeList.Tapped += Row_Tapped;
        _resolver = new FomodSelectionResolver(versions);
    }

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<FomodStepSelection> Selections { get; private set; } = Array.Empty<FomodStepSelection>();

    public IReadOnlyList<FomodFileInstall> ResolvedFiles { get; private set; } = Array.Empty<FomodFileInstall>();

    public string SelectionSignature(string destinationBase) =>
        _resolver.ComputeSelectionSignature(Selections, destinationBase);

    /// <summary>
    /// Applies a previously saved selection (as recorded in
    /// <see cref="FomodStepSelection"/>) to the currently realized leaves, then recomputes the
    /// derived state and resolved files. Called after <see cref="SetModule"/> so a rebuilt tree
    /// opens with the user's prior choices instead of the module defaults. Plugins not present
    /// in the saved selection fall back to their default-checked state. Safe to call with an
    /// empty list (leaves defaults in place).
    /// </summary>
    public void ApplySelections(IReadOnlyList<FomodStepSelection> selections)
    {
        if (_module is null || _flat.Count == 0)
            return;

        // Map saved selections to a set of "stepKey>groupKey>pluginName" keys that are selected.
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in selections)
        {
            var stepName = step.Step.Name;
            foreach (var group in step.Groups)
            {
                var groupKey = "step:" + stepName + ">" + group.Group.Name;
                foreach (var plugin in group.SelectedPlugins)
                    selectedKeys.Add(groupKey + ">" + plugin.Name);
            }
        }

        // Fully restore the saved selection: every leaf matches its saved checked state.
        // Required/NotUsable plugins are forced back to their constrained state by the initial
        // pass in RecomputeSelection, so this is safe for constrained leaves too. RecomputeSelection
        // then carries the user's checked state over by key (see BuildVisibleNodes) and
        // re-derives the files/validation.
        foreach (var node in _flat.Where(n => !n.IsContainer && n.Plugin is not null))
        {
            node.IsChecked = selectedKeys.Contains(node.Key);
        }

        RecomputeSelection();
    }

    public void SetModule(FomodModule module, IFomodFileStateProvider files)
    {
        _module = module;
        _files = files;
        _flat.Clear();

        foreach (var step in module.InstallSteps)
            _flat.Add(new FomodOptionNode(step.Name, 0, isContainer: true)
            {
                Key = "step:" + step.Name,
                Step = step,
                IsExpanded = true,
                ExpandGlyph = FomodOptionNode.ChevronExpandedGlyph()
            });

        WarningText.Text = module.Warnings.Count > 0 ? string.Join(" ", module.Warnings) : string.Empty;
        WarningText.Visibility = module.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RecomputeSelection();
    }

    /// <summary>
    /// Loads the preview images for every visible plugin in one archive pass (off the UI thread)
    /// and sets each leaf row's <see cref="FomodOptionNode.Thumbnail"/> on the UI thread. Missing
    /// or undecodable images simply leave the row without a thumbnail and never block selection.
    /// Call after <see cref="SetModule"/>; safe to call again (e.g. after a selection recompute).
    /// </summary>
    public async Task LoadPluginThumbnailsAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || _module is null)
            return;

        var leaves = _flat.Where(node => !node.IsContainer && node.Plugin is not null).ToList();
        if (leaves.Count == 0)
            return;

        var entryPaths = leaves.Select(node => node.Plugin!.ImagePath);
        var images = await AppServices.FomodImageService.PreloadAsync(archivePath, entryPaths, 128, cancellationToken);
        if (images.Count == 0)
            return;

        // PreloadAsync has already registered every result in its cache, so the per-plugin
        // CreateBitmapSource below is cheap. Must run on the UI thread (WriteableBitmap).
        foreach (var node in leaves)
        {
            var path = node.Plugin!.ImagePath;
            if (path is null)
                continue;
            var key = path.Replace('\\', '/');
            node.Thumbnail = images.TryGetValue(key, out var data)
                ? FomodImageService.CreateBitmapSource(data)
                : null;
        }
    }

    private void RecomputeSelection()
    {
        var module = _module;
        var files = _files;
        if (module is null || files is null || _recomputing)
            return;
        _recomputing = true;
        try
        {
            var initialFlags = AccumulateFlags();
            foreach (var node in _flat.Where(n => !n.IsContainer))
            {
                var type = _resolver.ResolvePluginType(node.Plugin!, initialFlags, files);
                switch (type)
                {
                    case FomodPluginType.Required:
                        node.IsChecked = true;
                        node.IsEnabled = false;
                        break;
                    case FomodPluginType.NotUsable:
                        node.IsChecked = false;
                        node.IsEnabled = false;
                        break;
                    default:
                        node.IsEnabled = true;
                        break;
                }
            }

            var flags = AccumulateFlags();

            var oldChecked = _flat.Where(n => !n.IsContainer)
                .ToDictionary(n => n.Key, n => n.IsChecked, StringComparer.Ordinal);
            var oldExpanded = _flat.Where(n => n.IsContainer)
                .ToDictionary(n => n.Key, n => n.IsExpanded, StringComparer.Ordinal);

            var visibleSteps = module.InstallSteps.Where(step => _resolver.IsStepVisible(step, flags, files)).ToList();
            var desired = BuildVisibleNodes(visibleSteps, flags, module, files, oldChecked, oldExpanded);

            // Preserve the user's existing selection/expand state across a recompute: a simple
            // checkbox toggle should update the tree in place, not tear down the ListView (the
            // source of the visible flicker on choice). Only the derived state and files change.
            var structureChanged = desired.Count != _flat.Count
                || !desired.Select(n => n.Key).SequenceEqual(_flat.Select(n => n.Key));
            if (structureChanged)
            {
                // Genuine structural change (a step/group became visible, or a node was added/
                // removed): rebuild the list. The old node state was captured above, so expansion
                // and checked state carry over by key.
                _flat.Clear();
                foreach (var node in desired)
                    _flat.Add(node);
                Refresh();
            }
            else
            {
                // In place: update the surviving nodes' checked/enabled/type so the two-way
                // bindings refresh the existing checkboxes without a rebuild. The _recomputing
                // guard (set above) suppresses the CheckBox event handlers while we do this, so
                // this cannot re-enter RecomputeSelection.
                for (int i = 0; i < _flat.Count; i++)
                    UpdateNodeInPlace(_flat[i], desired[i]);
            }
            Selections = BuildSelections(visibleSteps, flags);
            ResolvedFiles = _resolver.ResolveFileInstalls(module, Selections, files);
            ValidationText.Text = BuildValidationErrors(visibleSteps, flags);
            ValidationText.Visibility = ValidationText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _recomputing = false;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Builds the flat node list for the currently visible steps, carrying the user's existing
    /// checked/expand state (keyed) over from the previous realization so a recompute never
    /// silently discards what the user chose or expanded. Mirrors the node layout the
    /// <see cref="RecomputeSelection"/> rebuild path uses.
    /// </summary>
    private List<FomodOptionNode> BuildVisibleNodes(
        IReadOnlyList<FomodInstallStep> visibleSteps,
        IReadOnlyDictionary<string, string> flags,
        FomodModule module,
        IFomodFileStateProvider files,
        Dictionary<string, bool> oldChecked,
        Dictionary<string, bool> oldExpanded)
    {
        var nodes = new List<FomodOptionNode>();
        foreach (var step in visibleSteps)
        {
            var stepKey = "step:" + step.Name;
            nodes.Add(new FomodOptionNode(step.Name, 0, isContainer: true)
            {
                Key = stepKey,
                Step = step,
                IsExpanded = oldExpanded.GetValueOrDefault(stepKey, true),
                ExpandGlyph = oldExpanded.GetValueOrDefault(stepKey, true)
                    ? FomodOptionNode.ChevronExpandedGlyph()
                    : FomodOptionNode.ChevronCollapsedGlyph()
            });
            if (!oldExpanded.GetValueOrDefault(stepKey, true))
                continue;

            foreach (var group in step.Groups)
            {
                var groupKey = stepKey + ">" + group.Name;
                nodes.Add(new FomodOptionNode(group.Name, 1, isContainer: true)
                {
                    Key = groupKey,
                    Step = step,
                    Group = group,
                    GroupRuleText = GroupRuleText(group),
                    IsExpanded = oldExpanded.GetValueOrDefault(groupKey, true),
                    ExpandGlyph = oldExpanded.GetValueOrDefault(groupKey, true)
                        ? FomodOptionNode.ChevronExpandedGlyph()
                        : FomodOptionNode.ChevronCollapsedGlyph()
                });
                if (!oldExpanded.GetValueOrDefault(groupKey, true))
                    continue;

                foreach (var plugin in group.Plugins)
                {
                    if (!_resolver.IsPluginVisible(plugin, flags, files))
                        continue;
                    var type = _resolver.ResolvePluginType(plugin, flags, files);
                    nodes.Add(new FomodOptionNode(plugin.Name, 2, isContainer: false)
                    {
                        Key = groupKey + ">" + plugin.Name,
                        Step = step,
                        Group = group,
                        Plugin = plugin,
                        Description = plugin.Description ?? string.Empty,
                        TypeText = TypeText(type),
                        IsChecked = oldChecked.GetValueOrDefault(groupKey + ">" + plugin.Name, plugin.DefaultSelected),
                        IsEnabled = type is not (FomodPluginType.Required or FomodPluginType.NotUsable)
                    });
                }
            }
        }
        _ = module;
        return nodes;
    }

    /// <summary>
    /// Copies the derived state from <paramref name="next"/> onto the surviving
    /// <paramref name="current"/> node so the two-way bindings refresh the realized CheckBox /
    /// chevron in place. Called with the <c>_recomputing</c> guard set, so the CheckBox handlers
    /// it triggers are suppressed and no re-entrant recompute occurs.
    /// </summary>
    private static void UpdateNodeInPlace(FomodOptionNode current, FomodOptionNode next)
    {
        if (next.IsContainer)
        {
            current.IsExpanded = next.IsExpanded;
            current.ExpandGlyph = next.ExpandGlyph;
        }
        else
        {
            current.IsChecked = next.IsChecked;
            current.IsEnabled = next.IsEnabled;
            current.TypeText = next.TypeText;
        }
    }

    private Dictionary<string, string> AccumulateFlags()
    {
        var flags = new Dictionary<string, string>();
        foreach (var node in _flat.Where(n => !n.IsContainer && n.IsChecked))
            foreach (var flag in node.Plugin!.ConditionFlags)
                flags[flag.Key] = flag.Value;
        return flags;
    }

    private List<FomodStepSelection> BuildSelections(IReadOnlyList<FomodInstallStep> visibleSteps, IReadOnlyDictionary<string, string> flags)
    {
        var result = new List<FomodStepSelection>();
        foreach (var step in visibleSteps)
        {
            var groups = new List<FomodGroupSelection>();
            foreach (var group in step.Groups)
            {
                var selected = _flat
                    .Where(n => !n.IsContainer && ReferenceEquals(n.Step, step) && ReferenceEquals(n.Group, group) && n.IsChecked)
                    .Select(n => n.Plugin!)
                    .ToList();
                if (selected.Count > 0)
                    groups.Add(new FomodGroupSelection { Group = group, SelectedPlugins = selected });
            }
            if (groups.Count > 0)
                result.Add(new FomodStepSelection { Step = step, Groups = groups });
        }
        return result;
    }

    private string BuildValidationErrors(IReadOnlyList<FomodInstallStep> visibleSteps, IReadOnlyDictionary<string, string> flags)
    {
        var files = _files!;
        var errors = new List<string>();
        foreach (var step in visibleSteps)
        {
            foreach (var group in step.Groups)
            {
                var selected = _flat
                    .Where(n => !n.IsContainer && ReferenceEquals(n.Step, step) && ReferenceEquals(n.Group, group) && n.IsChecked)
                    .Select(n => n.Plugin!)
                    .ToList();
                var error = _resolver.ValidateGroup(group, selected, flags, files);
                if (error is not null)
                    errors.Add(error);
            }
        }
        return string.Join(" ", errors.Distinct());
    }

    private static string TypeText(FomodPluginType type) => type switch
    {
        FomodPluginType.Required => "Required",
        FomodPluginType.Recommended => "Recommended",
        FomodPluginType.NotUsable => "Not usable",
        _ => string.Empty
    };

    private static string GroupRuleText(FomodGroup group) => group.Type switch
    {
        FomodGroupType.SelectExactlyOne => "exactly one",
        FomodGroupType.SelectAtLeastOne => "at least one",
        FomodGroupType.SelectAtMostOne => "at most one",
        FomodGroupType.SelectAll => "all",
        _ => string.Empty
    };

    private void Check_Changed(object sender, RoutedEventArgs args)
    {
        if (_recomputing)
            return;
        RecomputeSelection();
    }

    private void Row_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { Tag: FomodOptionNode node } || !node.IsContainer)
            return;
        ToggleExpansion(node);
    }

    private void ToggleExpansion(FomodOptionNode node)
    {
        if (node.IsExpanded)
            Collapse(node);
        else
            Expand(node);
    }

    private void Expand(FomodOptionNode node)
    {
        var index = _flat.IndexOf(node);
        if (index < 0 || _module is null || _files is null)
            return;
        var flags = AccumulateFlags();
        var visibleSteps = _module.InstallSteps.Where(step => _resolver.IsStepVisible(step, flags, _files)).ToList();
        InsertChildren(node, visibleSteps, flags);
        node.IsExpanded = true;
        node.ExpandGlyph = FomodOptionNode.ChevronExpandedGlyph();
        Refresh();
    }

    private void InsertChildren(FomodOptionNode node, IReadOnlyList<FomodInstallStep> visibleSteps, IReadOnlyDictionary<string, string> flags)
    {
        var index = _flat.IndexOf(node);
        if (index < 0)
            return;
        var insertAt = index + 1;

        void InsertGroup(FomodGroup group, FomodInstallStep step)
        {
            var groupNode = new FomodOptionNode(group.Name, 1, isContainer: true)
            {
                Key = "step:" + step.Name + ">" + group.Name,
                Step = step,
                Group = group,
                GroupRuleText = GroupRuleText(group),
                IsExpanded = true,
                ExpandGlyph = FomodOptionNode.ChevronExpandedGlyph()
            };
            _flat.Insert(insertAt++, groupNode);
            foreach (var plugin in group.Plugins)
            {
                if (!_resolver.IsPluginVisible(plugin, flags, _files!))
                    continue;
                var type = _resolver.ResolvePluginType(plugin, flags, _files!);
                _flat.Insert(insertAt++, new FomodOptionNode(plugin.Name, 2, isContainer: false)
                {
                    Key = groupNode.Key + ">" + plugin.Name,
                    Step = step,
                    Group = group,
                    Plugin = plugin,
                    Description = plugin.Description ?? string.Empty,
                    TypeText = TypeText(type),
                    IsChecked = plugin.DefaultSelected,
                    IsEnabled = type is not (FomodPluginType.Required or FomodPluginType.NotUsable)
                });
            }
        }

        if (node.Group is null)
        {
            foreach (var group in node.Step!.Groups)
                InsertGroup(group, node.Step);
        }
        else
        {
            foreach (var plugin in node.Group.Plugins)
            {
                if (!_resolver.IsPluginVisible(plugin, flags, _files!))
                    continue;
                var type = _resolver.ResolvePluginType(plugin, flags, _files!);
                _flat.Insert(insertAt++, new FomodOptionNode(plugin.Name, 2, isContainer: false)
                {
                    Key = node.Key + ">" + plugin.Name,
                    Step = node.Step,
                    Group = node.Group,
                    Plugin = plugin,
                    Description = plugin.Description ?? string.Empty,
                    TypeText = TypeText(type),
                    IsChecked = plugin.DefaultSelected,
                    IsEnabled = type is not (FomodPluginType.Required or FomodPluginType.NotUsable)
                });
            }
        }
    }

    private void Collapse(FomodOptionNode node)
    {
        var index = _flat.IndexOf(node);
        if (index < 0)
            return;
        node.IsExpanded = false;
        node.ExpandGlyph = FomodOptionNode.ChevronCollapsedGlyph();

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
}