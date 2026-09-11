using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.Storage;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

/// <summary>
/// Visual, editable view of every mod's dependency edges at once - a supplement to the fully
/// accessible per-mod <see cref="ModDependencyEditDialog"/>, not a replacement for it. Every edit
/// made here (drag to connect, right-click to remove) goes through the same <see cref="ModEntry.Dependencies"/>
/// list and is saved through <see cref="ModStore"/>, so the two editors can never drift apart.
/// </summary>
public sealed partial class ModDependencyGraphPage : PageBase
{
    private const double NodeWidth = 168;
    private const double NodeHeight = 56;
    private const double LayerSpacingX = 220;
    private const double RowSpacingY = 84;
    private const double CanvasMargin = 24;
    private const double HandleSize = 10;
    private const double NudgeStep = 8;
    private const double FineNudgeStep = 1;
    private const double EdgeHitTolerance = 5;

    private readonly ModStore _store = AppServices.ModStore;
    private List<ModEntry> _mods = new();
    private readonly Dictionary<string, Grid> _nodeContainers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EdgeVisual> _edgeVisuals = new(StringComparer.Ordinal); // keyed by ModDependency.Id
    private readonly Dictionary<string, Point> _nodePositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point> _edgeBendPoints = new(StringComparer.Ordinal); // keyed by ModDependency.Id
    private readonly HashSet<string> _selectedModIds = new(StringComparer.Ordinal); // mod IDs
    private readonly HashSet<string> _selectedDependencyIds = new(StringComparer.Ordinal); // ModDependency IDs
    private readonly Dictionary<string, DependencyCatalogIssueKind> _catalogIssuesByModId = new(StringComparer.Ordinal);
    private List<ModDependency> _dependencyClipboard = new();
    private IReadOnlyList<DependencyPreset> _presets = Array.Empty<DependencyPreset>();
    private bool _isSyncingZoom;
    private readonly MenuFlyout _presetFlyout = new();
    private readonly MenuFlyout _arrangeFlyout = new();
    private List<string> _searchResults = new();
    private int _searchIndex = -1;
    private List<GraphIssueRow> _validationIssueRows = new();
    private int _validationIssueIndex = -1;
    private readonly DependencyEditHistory _editHistory = new();
    private int _selectionCount;
    private string? _lastSelectedNodeId; // For Ctrl+Shift+Click range selection

    private enum DragMode { None, Move, Connect, ReattachEdge, BendEdge, Marquee, Pan }
    private DragMode _dragMode;
    private ModEntry? _dragSourceMod;
    private Point _dragPointerStart;
    private Point _dragNodeStart;
    private Line? _dragPreviewLine;
    private Rectangle? _marqueeRectangle;
    private Point _panPointerStart;
    private Point _panScrollStart;
    private string? _focusModId;
    private bool _layoutDirty;

    // EdgeVisual groups a line and arrowhead marker into a single visual unit
    private class EdgeVisual
    {
        public Line Line { get; set; } = null!;
        public Line? BendLine { get; set; }
        public Point StraightTarget { get; set; }
        public TextBlock? Label { get; set; }
        public Polygon? Arrowhead { get; set; }
        public Ellipse? BendHandle { get; set; }
        public Ellipse? SourceHandle { get; set; } // Reattach endpoint
        public Ellipse? TargetHandle { get; set; } // Reattach endpoint
        public ModDependency Dependency { get; set; } = null!;
        public ModEntry Owner { get; set; } = null!;
        public List<ModDependency>? CombinedDependencies { get; set; } // If this edge combines multiple dependencies
    }

    private bool _isLoading = true;
    private bool _hasNoMods;

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool HasNoMods { get => _hasNoMods; private set => SetProperty(ref _hasNoMods, value); }
    public int SelectionCount { get => _selectionCount; private set => SetProperty(ref _selectionCount, value); }

    public ModDependencyGraphPage()
    {
        InitializeComponent();
        RegisterKeyboardAccelerators();
    }

    // The mods list is owned by ModsPage/ModStore, so this cached page re-reads it on every visit
    // instead of trusting a constructor-time snapshot.
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _focusModId = e.Parameter as string;
        UiTask.Run(LoadGraphAsync, nameof(OnNavigatedTo), ShowLoadError);
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Keyboard nudges only mark the layout dirty, so flush them before the page goes away.
        if (_layoutDirty)
            PersistLayout();
    }

    private async Task LoadGraphAsync()
    {
        IsLoading = true;
        try
        {
            _mods = (await _store.LoadAsync()).ToList();
            await RestoreSavedLayoutAsync();
            _presets = await AppServices.DependencyPresetStore.LoadAsync();
        }
        finally
        {
            // Rebuild even on failure so the page stays usable while the InfoBar explains what went wrong.
            IsLoading = false;
            HasNoMods = _mods.Count == 0;
            _editHistory.Clear();
            InitializeGraph();
            FocusRequestedMod();
        }
    }

    /// <summary>Seeds saved placements before layout runs, so a computed position only fills genuine gaps.</summary>
    private async Task RestoreSavedLayoutAsync()
    {
        var snapshot = await AppServices.GraphLayoutStore.LoadAsync();
        var knownModIds = _mods.Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
        var knownDependencyIds = _mods
            .SelectMany(mod => mod.Dependencies)
            .Select(dependency => dependency.Id)
            .ToHashSet(StringComparer.Ordinal);

        _nodePositions.Clear();
        foreach (var (modId, point) in snapshot.Nodes.Where(entry => knownModIds.Contains(entry.Key)))
            _nodePositions[modId] = new Point(point.X, point.Y);

        _edgeBendPoints.Clear();
        foreach (var (dependencyId, point) in snapshot.Bends.Where(entry => knownDependencyIds.Contains(entry.Key)))
            _edgeBendPoints[dependencyId] = new Point(point.X, point.Y);
    }

    private void PersistLayout()
    {
        var snapshot = new GraphLayoutSnapshot();
        foreach (var (modId, point) in _nodePositions)
            snapshot.Nodes[modId] = new GraphLayoutPoint(point.X, point.Y);
        foreach (var (dependencyId, point) in _edgeBendPoints)
            snapshot.Bends[dependencyId] = new GraphLayoutPoint(point.X, point.Y);

        UiTask.Run(() => AppServices.GraphLayoutStore.SaveAsync(snapshot), nameof(PersistLayout));
        _layoutDirty = false;
    }

    private void ShowLoadError(Exception exception) =>
        ShowInfo($"Unable to load the mod dependencies. {exception.Message}", InfoBarSeverity.Error);

    private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    private static string Dependencies(int count) => count == 1 ? "1 dependency" : $"{count} dependencies";

    // Reattaching an edge mutates Target in place, so the clipboard keeps its own copies.
    private static ModDependency CopyDependency(ModDependency dependency) => new()
    {
        Id = dependency.Id,
        SourceModId = dependency.SourceModId,
        Kind = dependency.Kind,
        Target = dependency.Target,
        VersionConstraint = dependency.VersionConstraint,
        Origin = dependency.Origin
    };
    private static string Mods(int count) => count == 1 ? "1 mod" : $"{count} mods";

    /// <summary>Full graph rebuild - recreates all visuals. Node positions and edge bends are keyed by id so they survive.</summary>
    private void InitializeGraph()
    {
        // A rebuild follows every edit, so the selection is carried across it rather than dropped.
        var previousModIds = _selectedModIds.ToList();
        var previousDependencyIds = _selectedDependencyIds.ToList();
        var previousAnchor = _lastSelectedNodeId;

        GraphCanvas.Children.Clear();
        _nodeContainers.Clear();
        _edgeVisuals.Clear();
        _selectedModIds.Clear();
        _selectedDependencyIds.Clear();
        SelectionCount = 0;
        _dragMode = DragMode.None;
        _dragSourceMod = null;
        _dragPreviewLine = null;
        _lastSelectedNodeId = null;
        RefreshCatalogValidation();

        if (_mods.Count == 0)
            return;

        ApplyAutoLayoutForNewNodes();

        var modIds = _mods.Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _nodePositions.Keys.Where(id => !modIds.Contains(id)).ToList())
            _nodePositions.Remove(staleId);

        var maxX = _nodePositions.Values.Max(point => point.X) + NodeWidth + CanvasMargin;
        var maxY = _nodePositions.Values.Max(point => point.Y) + NodeHeight + CanvasMargin;
        GraphCanvas.Width = Math.Max(maxX, GraphScrollViewer.ActualWidth);
        GraphCanvas.Height = Math.Max(maxY, GraphScrollViewer.ActualHeight);

        // Build a combined-edges map: (ownerId, targetId) -> list of all dependencies
        var edgesByPair = new Dictionary<(string, string), List<ModDependency>>(new TupleEqualityComparer());
        foreach (var mod in _mods)
        {
            foreach (var dep in mod.Dependencies)
            {
                if (dep.Kind == ModDependencyKind.GameVersion || dep.Target?.ModId is not { } targetId)
                    continue;
                if (!_nodePositions.ContainsKey(targetId))
                    continue;
                
                var key = (mod.Id, targetId);
                if (!edgesByPair.TryGetValue(key, out var list))
                    edgesByPair[key] = list = new();
                list.Add(dep);
            }
        }

        // Edges drawn first so node containers render on top
        var renderedDependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((ownerId, targetId), dependencies) in edgesByPair)
        {
            var owner = _mods.FirstOrDefault(m => m.Id == ownerId);
            if (owner is null || dependencies.Count == 0)
                continue;

            if (dependencies.Count == 1)
            {
                // Single edge: render normally
                AddEdgeVisual(owner, dependencies[0]);
                renderedDependencyIds.Add(dependencies[0].Id!);
            }
            else
            {
                // Multiple edges for same pair: render as combined visual
                AddCombinedEdgeVisual(owner, dependencies);
                foreach (var dep in dependencies)
                    renderedDependencyIds.Add(dep.Id!);
            }
        }

        RestoreBendPoints();

        foreach (var mod in _mods)
        {
            if (!_nodePositions.TryGetValue(mod.Id, out var point))
                continue;

            var container = CreateNode(mod);
            Canvas.SetLeft(container, point.X);
            Canvas.SetTop(container, point.Y);
            _nodeContainers[mod.Id] = container;
            GraphCanvas.Children.Add(container);
        }

        RestoreSelection(previousModIds, previousDependencyIds, previousAnchor);
    }

    /// <summary>Reapplies a pre-rebuild selection, dropping anything the edit removed.</summary>
    private void RestoreSelection(List<string> modIds, List<string> dependencyIds, string? anchorModId)
    {
        foreach (var modId in modIds.Where(_nodeContainers.ContainsKey))
            _selectedModIds.Add(modId);
        foreach (var dependencyId in dependencyIds.Where(_edgeVisuals.ContainsKey))
            _selectedDependencyIds.Add(dependencyId);

        _lastSelectedNodeId = anchorModId is not null && _selectedModIds.Contains(anchorModId) ? anchorModId : null;

        UpdateSelectionCount();
        UpdateNodeVisuals();
        UpdateEdgeVisuals();
    }

    private class TupleEqualityComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) => x.Item1 == y.Item1 && x.Item2 == y.Item2;
        public int GetHashCode((string, string) obj) => HashCode.Combine(obj.Item1, obj.Item2);
    }

    /// <summary>Fills in a position only for a mod id not already placed, so a manual drag survives later edits.</summary>
    private void ApplyAutoLayoutForNewNodes()
    {
        var layout = GraphLayout.Compute(AppServices.AppSettings.GraphLayoutKind, _mods, LayerSpacingX, RowSpacingY, CanvasMargin);
        foreach (var (modId, point) in layout)
        {
            if (!_nodePositions.ContainsKey(modId))
                _nodePositions[modId] = point;
        }
    }

    /// <summary>Layers mods by Requires depth (Kahn-style relaxation); a cycle simply stops refining and keeps its last positions.</summary>
    private Grid CreateNode(ModEntry mod)
    {
        var button = new Button
        {
            Tag = mod,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = new TextBlock
            {
                Text = mod.Name,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            }
        };

        if (mod.HasDependencyIssue || _catalogIssuesByModId.ContainsKey(mod.Id))
        {
            button.BorderBrush = BrushForMod(mod);
            button.BorderThickness = new Thickness(2);
        }

        AutomationProperties.SetName(button, mod.HasDependencyIssue ? $"{mod.Name}. {mod.DependencyIssueSummary}" : mod.Name);
        var tooltip = $"{mod.Name}\n\nClick to select | Ctrl+click to toggle | Shift+click to range select | Drag to move";
        ToolTipService.SetToolTip(button, tooltip);

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Node_PointerPressed), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Node_PointerMoved), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Node_PointerReleased), true);
        button.DoubleTapped += Node_DoubleTapped;
        button.KeyDown += Node_KeyDown;

        var container = new Grid { Width = NodeWidth, Height = NodeHeight, Tag = mod };
        container.Children.Add(button);

        // Four connector handles at cardinal directions (N/E/S/W)
        var handles = new (double X, double Y, string Tooltip)[]
        {
            (NodeWidth / 2, 0, "Drag to connect (top)"),
            (NodeWidth, NodeHeight / 2, "Drag to connect (right)"),
            (NodeWidth / 2, NodeHeight, "Drag to connect (bottom)"),
            (0, NodeHeight / 2, "Drag to connect (left)")
        };

        foreach (var (hx, hy, handleTooltip) in handles)
        {
            var handle = new Ellipse
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(hx - HandleSize / 2, hy - HandleSize / 2, 0, 0),
                Opacity = 0.55,
                Tag = mod
            };
            ToolTipService.SetToolTip(handle, handleTooltip);
            AutomationProperties.SetName(handle, $"Connect {mod.Name} from {handleTooltip.Split(" (")[1].TrimEnd(')')}");
            handle.PointerEntered += (_, _) => handle.Opacity = 1;
            handle.PointerExited += (_, _) => handle.Opacity = 0.55;
            handle.PointerPressed += Handle_PointerPressed;
            handle.PointerMoved += Handle_PointerMoved;
            handle.PointerReleased += Handle_PointerReleased;
            container.Children.Add(handle);
        }

        return container;
    }

    /// <summary>Returns the point where a line from source-center toward target-center crosses the source node's rectangle boundary.</summary>
    private Point GetBoundaryPoint(Point sourceCenter, Point targetCenter, Rect nodeRect) =>
        GraphGeometry.BoundaryPoint(sourceCenter, targetCenter, new Size(nodeRect.Width, nodeRect.Height));

    private void AddEdgeVisual(ModEntry owner, ModDependency dependency)
    {
        if (dependency.Id is null || dependency.Target?.ModId is not { } targetId)
            return;
        if (!_nodePositions.TryGetValue(owner.Id, out var fromPos) || !_nodePositions.TryGetValue(targetId, out var toPos))
            return;

        var fromCenter = new Point(fromPos.X + NodeWidth / 2, fromPos.Y + NodeHeight / 2);
        var toCenter = new Point(toPos.X + NodeWidth / 2, toPos.Y + NodeHeight / 2);

        var fromRect = new Rect(0, 0, NodeWidth, NodeHeight);
        var toRect = new Rect(0, 0, NodeWidth, NodeHeight);
        var fromBoundary = GetBoundaryPoint(fromCenter, toCenter, fromRect);
        var toBoundary = GetBoundaryPoint(toCenter, fromCenter, toRect);

        var line = new Line
        {
            StrokeThickness = 2,
            Stroke = BrushForKind(dependency.Kind),
            StrokeDashArray = DashPatternForKind(dependency.Kind),
            ContextFlyout = BuildEdgeContextFlyout(owner, dependency),
            IsHitTestVisible = true,
            Tag = dependency.Id
        };

        line.X1 = fromBoundary.X;
        line.Y1 = fromBoundary.Y;
        line.X2 = toBoundary.X;
        line.Y2 = toBoundary.Y;

        ToolTipService.SetToolTip(line, DescribeEdge(dependency));
        line.PointerPressed += (_, args) => Edge_PointerPressed(dependency, args);
        line.DoubleTapped += (_, args) => ClearBendPoint(dependency.Id);
        MakeEdgeFocusable(line, dependency);

        var label = CreateEdgeLabel(dependency, fromBoundary, toBoundary);

        var sourceHandle = CreateReattachHandle(fromBoundary, dependency, isSource: true);
        var targetHandle = CreateReattachHandle(toBoundary, dependency, isSource: false);
        var arrowhead = CreateArrowhead(line, dependency.Kind);
        var visual = new EdgeVisual 
        { 
            Line = line, 
            Label = label,
            StraightTarget = toBoundary,
            Arrowhead = arrowhead, 
            SourceHandle = sourceHandle, 
            TargetHandle = targetHandle, 
            Dependency = dependency, 
            Owner = owner 
        };
        _edgeVisuals[dependency.Id] = visual;

        GraphCanvas.Children.Add(line);
        GraphCanvas.Children.Add(label);
        if (arrowhead is not null)
            GraphCanvas.Children.Add(arrowhead);
        GraphCanvas.Children.Add(sourceHandle);
        GraphCanvas.Children.Add(targetHandle);
    }

    private Ellipse CreateReattachHandle(Point position, ModDependency dependency, bool isSource)
    {
        var handle = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true,
            Tag = (dependency.Id, isSource)
        };

        Canvas.SetLeft(handle, position.X - 4);
        Canvas.SetTop(handle, position.Y - 4);

        handle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ReattachHandle_PointerPressed), true);
        handle.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ReattachHandle_PointerMoved), true);
        handle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ReattachHandle_PointerReleased), true);

        return handle;
    }

    private void AddCombinedEdgeVisual(ModEntry owner, List<ModDependency> dependencies)
    {
        // Render one combined visual for multiple dependencies with the same (owner, target) pair
        if (dependencies.Count == 0 || dependencies[0].Target?.ModId is not { } targetId)
            return;
        if (!_nodePositions.TryGetValue(owner.Id, out var fromPos) || !_nodePositions.TryGetValue(targetId, out var toPos))
            return;

        var fromCenter = new Point(fromPos.X + NodeWidth / 2, fromPos.Y + NodeHeight / 2);
        var toCenter = new Point(toPos.X + NodeWidth / 2, toPos.Y + NodeHeight / 2);

        var fromRect = new Rect(0, 0, NodeWidth, NodeHeight);
        var toRect = new Rect(0, 0, NodeWidth, NodeHeight);
        var fromBoundary = GetBoundaryPoint(fromCenter, toCenter, fromRect);
        var toBoundary = GetBoundaryPoint(toCenter, fromCenter, toRect);

        // Render each underlying dependency as a slightly offset line (two-tone visual)
        var primaryDep = dependencies.FirstOrDefault(d => d.Kind == ModDependencyKind.Requires) ?? dependencies[0];
        foreach (var (i, dep) in dependencies.Select((dep, idx) => (idx, dep)))
        {
            var offsetX = i > 0 ? 2 : -2; // Offset alternating dependencies
            var line = new Line
            {
                StrokeThickness = i == 0 ? 2.5 : 1.5,
                Stroke = BrushForKind(dep.Kind),
                StrokeDashArray = DashPatternForKind(dep.Kind),
                ContextFlyout = BuildEdgeContextFlyout(owner, dep),
                IsHitTestVisible = true,
                Tag = dep.Id
            };

            line.X1 = fromBoundary.X + offsetX;
            line.Y1 = fromBoundary.Y;
            line.X2 = toBoundary.X + offsetX;
            line.Y2 = toBoundary.Y;

            ToolTipService.SetToolTip(line, $"{DescribeEdge(dep)} (combined)");
            line.PointerPressed += (_, args) => Edge_PointerPressed(dep, args);
            line.DoubleTapped += (_, _) => ClearBendPoint(dep.Id);
            MakeEdgeFocusable(line, dep);

            var label = CreateEdgeLabel(dep, linePoint: new Point((line.X1 + line.X2) / 2, (line.Y1 + line.Y2) / 2), lineEnd: new Point(line.X2, line.Y2));

            var visual = new EdgeVisual 
            { 
                Line = line, 
                Label = label,
                StraightTarget = new Point(line.X2, line.Y2),
                Dependency = dep, 
                Owner = owner, 
                CombinedDependencies = dependencies 
            };
            _edgeVisuals[dep.Id!] = visual;
            GraphCanvas.Children.Add(line);
            GraphCanvas.Children.Add(label);
        }
    }

    private TextBlock CreateEdgeLabel(ModDependency dependency, Point linePoint, Point lineEnd)
    {
        var label = new TextBlock
        {
            Text = dependency.Kind.ToString(),
            FontSize = 11,
            Padding = new Thickness(4, 1, 4, 1),
            Foreground = BrushForKind(dependency.Kind),
            IsHitTestVisible = false,
            Opacity = 0.9,
            Tag = dependency.Id
        };
        if (!AppServices.AppSettings.ShowGraphEdgeLabels)
            label.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(label, linePoint.X - 28);
        Canvas.SetTop(label, linePoint.Y - 9);
        return label;
    }

    private Polygon? CreateArrowhead(Line line, ModDependencyKind kind)
    {
        if (kind == ModDependencyKind.Conflicts)
        {
            // Symmetric diamond marker at midpoint for conflicts
            var midX = (line.X1 + line.X2) / 2;
            var midY = (line.Y1 + line.Y2) / 2;
            var points = new PointCollection();
            points.Add(new Point(midX, midY - 5));
            points.Add(new Point(midX + 5, midY));
            points.Add(new Point(midX, midY + 5));
            points.Add(new Point(midX - 5, midY));
            var marker = new Polygon
            {
                Points = points,
                Fill = BrushForKind(kind),
                IsHitTestVisible = false
            };
            return marker;
        }
        else
        {
            // Triangle arrowhead pointing from owner to target
            var dx = line.X2 - line.X1;
            var dy = line.Y2 - line.Y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1)
                return null;

            dx /= len;
            dy /= len;

            var arrowLen = 8;
            var arrowWid = 6;
            var tip = new Point(line.X2, line.Y2);
            var base1 = new Point(tip.X - arrowLen * dx + arrowWid * dy, tip.Y - arrowLen * dy - arrowWid * dx);
            var base2 = new Point(tip.X - arrowLen * dx - arrowWid * dy, tip.Y - arrowLen * dy + arrowWid * dx);

            var points = new PointCollection();
            points.Add(tip);
            points.Add(base1);
            points.Add(base2);
            var marker = new Polygon
            {
                Points = points,
                Fill = BrushForKind(kind),
                IsHitTestVisible = false
            };
            return marker;
        }
    }

    private void RemoveEdgeVisual(ModDependency dependency)
    {
        if (dependency.Id is null || !_edgeVisuals.TryGetValue(dependency.Id, out var visual))
            return;

        GraphCanvas.Children.Remove(visual.Line);
        if (visual.BendLine is not null)
            GraphCanvas.Children.Remove(visual.BendLine);
        if (visual.Label is not null)
            GraphCanvas.Children.Remove(visual.Label);
        if (visual.Arrowhead is not null)
            GraphCanvas.Children.Remove(visual.Arrowhead);
        if (visual.BendHandle is not null)
            GraphCanvas.Children.Remove(visual.BendHandle);

        _edgeVisuals.Remove(dependency.Id);
        _edgeBendPoints.Remove(dependency.Id);
    }


    private static Brush BrushForKind(ModDependencyKind kind) => kind switch
    {
        ModDependencyKind.Conflicts => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        ModDependencyKind.LoadAfter or ModDependencyKind.LoadBefore => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
        _ => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
    };

    private static Brush BrushForDependencyState(DependencyState state) => state switch
    {
        DependencyState.Cycle or DependencyState.MissingRequirement or DependencyState.Conflict =>
            (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        DependencyState.Ok => new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        _ => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
    };

    /// <summary>Catalog problems outrank the profile-scoped state because they are broken regardless of load order.</summary>
    private Brush BrushForMod(ModEntry mod)
    {
        if (_catalogIssuesByModId.TryGetValue(mod.Id, out var kind))
        {
            return DependencyCatalogValidator.IsAdvisory(kind)
                ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
                : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        return mod.HasDependencyIssue
            ? BrushForDependencyState(mod.DependencyState)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void RefreshCatalogValidation()
    {
        _catalogIssuesByModId.Clear();

        var issues = AppServices.DependencyCatalogValidator.Validate(_mods, AppServices.AppSettings.ShowAdvisoryDependencyWarnings);
        DependencyCatalogValidator.ApplySummaries(_mods, issues);
        foreach (var issue in issues)
        {
            // DuplicateEdge is the only advisory kind, so anything else wins the slot.
            if (!_catalogIssuesByModId.TryGetValue(issue.ModId, out var existing) || DependencyCatalogValidator.IsAdvisory(existing))
                _catalogIssuesByModId[issue.ModId] = issue.Kind;
        }

        if (ValidationPanel is null)
            return;

        if (issues.Count == 0)
        {
            _validationIssueRows.Clear();
            _validationIssueIndex = -1;
            ValidationIssueList.ItemsSource = null;
            ValidationPanel.IsExpanded = false;
            ValidationPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var caution = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        _validationIssueRows = issues
            .Select(issue => new GraphIssueRow
            {
                ModId = issue.ModId,
                Message = issue.Message,
                Glyph = GraphIssueRow.GlyphFor(issue.Kind),
                SeverityBrush = DependencyCatalogValidator.IsAdvisory(issue.Kind) ? caution : critical
            })
            .ToList();
        _validationIssueIndex = -1;
        ValidationIssueList.ItemsSource = _validationIssueRows;

        ValidationSummaryText.Text = $"{issues.Count} dependency problem{(issues.Count == 1 ? string.Empty : "s")}";
        ValidationSummaryText.Foreground = _catalogIssuesByModId.Values.Any(kind => !DependencyCatalogValidator.IsAdvisory(kind))
            ? critical
            : caution;
        ValidationPanel.Visibility = Visibility.Visible;
    }

    private void SelectNextValidationIssue()
    {
        if (_validationIssueRows.Count == 0)
        {
            ShowInfo("There are no dependency issues to review.");
            return;
        }

        _validationIssueIndex = (_validationIssueIndex + 1) % _validationIssueRows.Count;
        var row = _validationIssueRows[_validationIssueIndex];
        SelectMod(row.ModId, false);
        BringModIntoView(row.ModId);
        ValidationIssueList.ScrollIntoView(row);
    }

    /// <summary>Honours a mod id passed by another page, then forgets it so a later refresh does not re-focus.</summary>
    private void FocusRequestedMod()
    {
        if (_focusModId is not { } modId)
            return;

        _focusModId = null;
        if (!_nodeContainers.ContainsKey(modId))
            return;

        SelectMod(modId, false);
        BringModIntoView(modId);
    }

    private void ValidationIssue_Click(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not GraphIssueRow row)
            return;

        SelectMod(row.ModId, false);
        BringModIntoView(row.ModId);
    }

    private void BringModIntoView(string modId)
    {
        if (!_nodePositions.TryGetValue(modId, out var position))
            return;

        var zoom = GraphScrollViewer.ZoomFactor;
        var left = position.X * zoom + NodeWidth * zoom / 2 - GraphScrollViewer.ViewportWidth / 2;
        var top = position.Y * zoom + NodeHeight * zoom / 2 - GraphScrollViewer.ViewportHeight / 2;
        GraphScrollViewer.ChangeView(Math.Max(0, left), Math.Max(0, top), null);

        if (_nodeContainers.TryGetValue(modId, out var container))
            container.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Programmatic);
    }

    private static DoubleCollection? DashPatternForKind(ModDependencyKind kind) =>
        kind == ModDependencyKind.Conflicts ? new DoubleCollection { 5, 3 } : null;

    private static string DescribeEdge(ModDependency dependency) => dependency.Kind switch
    {
        ModDependencyKind.Requires => $"Requires {dependency.Target?.DisplayName}",
        ModDependencyKind.LoadAfter => $"Loads after {dependency.Target?.DisplayName}",
        ModDependencyKind.LoadBefore => $"Loads before {dependency.Target?.DisplayName}",
        ModDependencyKind.Conflicts => $"Conflicts with {dependency.Target?.DisplayName}",
        _ => "Dependency"
    };

    private MenuFlyout BuildEdgeContextFlyout(ModEntry owner, ModDependency dependency)
    {
        var flyout = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = $"Remove: {DescribeEdge(dependency)}" };
        delete.Click += (_, _) => RemoveDependency(owner, dependency);
        flyout.Items.Add(delete);
        return flyout;
    }

    private Grid? FindNodeAt(Point canvasPoint)
    {
        foreach (var container in _nodeContainers.Values)
        {
            var left = Canvas.GetLeft(container);
            var top = Canvas.GetTop(container);
            if (canvasPoint.X >= left && canvasPoint.X <= left + NodeWidth &&
                canvasPoint.Y >= top && canvasPoint.Y <= top + NodeHeight)
                return container;
        }

        return null;
    }

    private void UpdateEdgesForNode(string modId, double left, double top)
    {
        if (!_nodeContainers.TryGetValue(modId, out _))
            return;

        _nodePositions[modId] = new Point(left, top);

        var nodeRect = new Rect(0, 0, NodeWidth, NodeHeight);

        // Find all edges that touch this mod (either as owner or target)
        foreach (var visual in _edgeVisuals.Values)
        {
            var dependency = visual.Dependency;
            var owner = visual.Owner;

            if (!_nodeContainers.TryGetValue(owner.Id, out var ownerContainer) ||
                !_nodeContainers.TryGetValue(dependency.Target?.ModId ?? "", out var targetContainer))
                continue;

            var ownerPos = new Point(Canvas.GetLeft(ownerContainer), Canvas.GetTop(ownerContainer));
            var targetPos = new Point(Canvas.GetLeft(targetContainer), Canvas.GetTop(targetContainer));
            var ownerCenter = new Point(ownerPos.X + NodeWidth / 2, ownerPos.Y + NodeHeight / 2);
            var targetCenter = new Point(targetPos.X + NodeWidth / 2, targetPos.Y + NodeHeight / 2);

            var fromBoundary = GetBoundaryPoint(ownerCenter, targetCenter, nodeRect);
            var toBoundary = GetBoundaryPoint(targetCenter, ownerCenter, nodeRect);
            visual.Line.X1 = fromBoundary.X;
            visual.Line.Y1 = fromBoundary.Y;
            visual.StraightTarget = toBoundary;
            visual.Line.X2 = toBoundary.X;
            visual.Line.Y2 = toBoundary.Y;
            if (_edgeBendPoints.TryGetValue(dependency.Id, out var bendPoint))
                ApplyBendGeometry(visual, bendPoint);
            else if (visual.Arrowhead is not null)
                UpdateArrowheadPosition(visual);

            UpdateEndpointHandle(visual.SourceHandle, fromBoundary);
            UpdateEndpointHandle(visual.TargetHandle, toBoundary);
            UpdateEdgeLabelPosition(visual);
        }
    }

    private static void UpdateEndpointHandle(Ellipse? handle, Point position)
    {
        if (handle is null)
            return;

        Canvas.SetLeft(handle, position.X - handle.Width / 2);
        Canvas.SetTop(handle, position.Y - handle.Height / 2);
    }

    private static void UpdateEdgeLabelPosition(EdgeVisual visual)
    {
        if (visual.Label is null)
            return;

        Canvas.SetLeft(visual.Label, (visual.Line.X1 + visual.Line.X2) / 2 - 28);
        Canvas.SetTop(visual.Label, (visual.Line.Y1 + visual.Line.Y2) / 2 - 9);
    }

    private void UpdateArrowheadPosition(EdgeVisual visual)
    {
        var dependency = visual.Dependency;
        var line = visual.Line;

        if (dependency.Kind == ModDependencyKind.Conflicts)
        {
            var midX = (line.X1 + line.X2) / 2;
            var midY = (line.Y1 + line.Y2) / 2;
            var poly = visual.Arrowhead as Polygon;
            if (poly is not null)
            {
                var points = new PointCollection();
                points.Add(new Point(midX, midY - 5));
                points.Add(new Point(midX + 5, midY));
                points.Add(new Point(midX, midY + 5));
                points.Add(new Point(midX - 5, midY));
                poly.Points = points;
            }
        }
        else
        {
            var dx = line.X2 - line.X1;
            var dy = line.Y2 - line.Y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1)
                return;

            dx /= len;
            dy /= len;

            var arrowLen = 8;
            var arrowWid = 6;
            var tip = new Point(line.X2, line.Y2);
            var base1 = new Point(tip.X - arrowLen * dx + arrowWid * dy, tip.Y - arrowLen * dy - arrowWid * dx);
            var base2 = new Point(tip.X - arrowLen * dx - arrowWid * dy, tip.Y - arrowLen * dy + arrowWid * dx);

            var poly = visual.Arrowhead as Polygon;
            if (poly is not null)
            {
                var points = new PointCollection();
                points.Add(tip);
                points.Add(base1);
                points.Add(base2);
                poly.Points = points;
            }
        }
    }

    private void SelectMod(string modId, bool ctrlHeld, bool shiftHeld = false)
    {
        if (shiftHeld && _lastSelectedNodeId is not null)
        {
            // Range selection from last selected to current
            RangeSelectNodes(_lastSelectedNodeId, modId);
        }
        else if (ctrlHeld)
        {
            // Toggle selection
            if (!_selectedModIds.Remove(modId))
                _selectedModIds.Add(modId);
        }
        else
        {
            // Single selection
            _selectedModIds.Clear();
            _selectedModIds.Add(modId);
        }

        _selectedDependencyIds.Clear();
        _lastSelectedNodeId = modId;
        UpdateSelectionCount();
        UpdateNodeVisuals();
    }

    private void RangeSelectNodes(string fromModId, string toModId)
    {
        // Get visual order of all mods in grid
        var orderedMods = _nodePositions
            .OrderBy(kvp => Canvas.GetTop(_nodeContainers[kvp.Key]))
            .ThenBy(kvp => Canvas.GetLeft(_nodeContainers[kvp.Key]))
            .Select(kvp => kvp.Key)
            .ToList();

        var fromIdx = orderedMods.IndexOf(fromModId);
        var toIdx = orderedMods.IndexOf(toModId);

        if (fromIdx < 0 || toIdx < 0)
            return;

        _selectedModIds.Clear();
        var startIdx = Math.Min(fromIdx, toIdx);
        var endIdx = Math.Max(fromIdx, toIdx);

        for (int i = startIdx; i <= endIdx; i++)
            _selectedModIds.Add(orderedMods[i]);
    }

    private void SelectDependency(string depId, bool ctrlHeld)
    {
        if (ctrlHeld)
        {
            if (!_selectedDependencyIds.Remove(depId))
                _selectedDependencyIds.Add(depId);
        }
        else
        {
            _selectedDependencyIds.Clear();
            _selectedDependencyIds.Add(depId);
        }

        _selectedModIds.Clear();
        UpdateSelectionCount();
        UpdateEdgeVisuals();
    }

    private void ClearSelection()
    {
        _selectedModIds.Clear();
        _selectedDependencyIds.Clear();
        _lastSelectedNodeId = null;
        UpdateSelectionCount();
        UpdateNodeVisuals();
        UpdateEdgeVisuals();
    }

    private void UpdateSelectionCount()
    {
        SelectionCount = _selectedModIds.Count + _selectedDependencyIds.Count;
        if (SelectionCountText is null)
            return;

        SelectionCountText.Text = $"{SelectionCount} selected";
        SelectionCountText.Visibility = SelectionCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNodeVisuals()
    {
        foreach (var (modId, container) in _nodeContainers)
        {
            var button = container.Children.OfType<Button>().FirstOrDefault();
            if (button is null)
                continue;

            var isSelected = _selectedModIds.Contains(modId);
            button.BorderBrush = isSelected
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (_mods.FirstOrDefault(m => m.Id == modId) is { } mod
                    ? BrushForMod(mod)
                    : (Brush)Application.Current.Resources["TransparentBrush"]);
            button.BorderThickness = isSelected ? new Thickness(3) : new Thickness(2);
        }
    }

    private void UpdateEdgeVisuals()
    {
        foreach (var (depId, visual) in _edgeVisuals)
        {
            var isSelected = _selectedDependencyIds.Contains(depId);
            visual.Line.StrokeThickness = isSelected ? 4 : (visual.CombinedDependencies?.Count > 1 ? 2.5 : 2);
            visual.Line.Opacity = isSelected ? 1 : 0.7;

            // Show/hide reattach handles based on selection
            if (visual.SourceHandle is not null)
                visual.SourceHandle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            if (visual.TargetHandle is not null)
                visual.TargetHandle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;

            if (isSelected && visual.BendHandle is null)
                AddBendHandle(visual);
            else if (!isSelected && visual.BendHandle is not null)
            {
                GraphCanvas.Children.Remove(visual.BendHandle);
                visual.BendHandle = null;
            }
        }
    }

    /// <summary>Edges are pointer-selectable, so they also need to be reachable and operable from the keyboard.</summary>
    private void MakeEdgeFocusable(Line line, ModDependency dependency)
    {
        line.IsTabStop = true;
        line.UseSystemFocusVisuals = true;
        AutomationProperties.SetName(line, DescribeEdge(dependency));
        line.KeyDown += Edge_KeyDown;
    }

    private void Edge_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Line { Tag: string dependencyId })
            return;

        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            SelectDependency(dependencyId, IsKeyDown(VirtualKey.Control));
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Delete)
        {
            if (!_selectedDependencyIds.Contains(dependencyId))
                SelectDependency(dependencyId, false);
            DeleteSelectedDependencies();
            args.Handled = true;
        }
    }

    private void AddBendHandle(EdgeVisual visual)
    {
        var point = _edgeBendPoints.TryGetValue(visual.Dependency.Id, out var bend)
            ? bend
            : new Point((visual.Line.X1 + visual.Line.X2) / 2, (visual.Line.Y1 + visual.Line.Y2) / 2);
        var handle = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            StrokeThickness = 2,
            IsHitTestVisible = true,
            Tag = visual.Dependency.Id
        };
        Canvas.SetLeft(handle, point.X - 6);
        Canvas.SetTop(handle, point.Y - 6);
        ToolTipService.SetToolTip(handle, "Drag to bend edge, or use the arrow keys");
        AutomationProperties.SetName(handle, $"Bend point for {DescribeEdge(visual.Dependency)}");
        handle.IsTabStop = true;
        handle.UseSystemFocusVisuals = true;
        handle.KeyDown += BendHandle_KeyDown;
        handle.PointerPressed += BendHandle_PointerPressed;
        handle.PointerMoved += BendHandle_PointerMoved;
        handle.PointerReleased += BendHandle_PointerReleased;
        visual.BendHandle = handle;
        GraphCanvas.Children.Add(handle);
        ApplyBendGeometry(visual, point);
    }

    private void BendHandle_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Ellipse { Tag: string dependencyId } || !_edgeVisuals.TryGetValue(dependencyId, out var visual))
            return;

        if (args.Key == VirtualKey.Escape)
        {
            ClearBendPoint(dependencyId);
            args.Handled = true;
            return;
        }

        var step = IsKeyDown(VirtualKey.Shift) ? FineNudgeStep : NudgeStep;
        var (deltaX, deltaY) = args.Key switch
        {
            VirtualKey.Left => (-step, 0d),
            VirtualKey.Right => (step, 0d),
            VirtualKey.Up => (0d, -step),
            VirtualKey.Down => (0d, step),
            _ => (0d, 0d)
        };

        if (deltaX == 0 && deltaY == 0)
            return;

        var current = _edgeBendPoints.TryGetValue(dependencyId, out var bend)
            ? bend
            : new Point((visual.Line.X1 + visual.Line.X2) / 2, (visual.Line.Y1 + visual.Line.Y2) / 2);
        var moved = new Point(Math.Max(0, current.X + deltaX), Math.Max(0, current.Y + deltaY));

        _edgeBendPoints[dependencyId] = moved;
        ApplyBendGeometry(visual, moved);
        _layoutDirty = true;
        args.Handled = true;
    }

    private void BendHandle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Ellipse { Tag: string depId } || !_edgeVisuals.TryGetValue(depId, out var visual))
            return;

        _dragMode = DragMode.BendEdge;
        _dragSourceMod = visual.Owner;
        visual.BendHandle!.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void BendHandle_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragMode != DragMode.BendEdge || sender is not Ellipse { Tag: string depId } || !_edgeVisuals.TryGetValue(depId, out var visual))
            return;

        var point = args.GetCurrentPoint(GraphCanvas).Position;
        _edgeBendPoints[depId] = point;
        ApplyBendGeometry(visual, point);
    }

    private void BendHandle_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Ellipse { Tag: string depId } || !_edgeVisuals.TryGetValue(depId, out var visual))
            return;

        visual.BendHandle?.ReleasePointerCapture(args.Pointer);
        _dragMode = DragMode.None;
        _dragSourceMod = null;
        PersistLayout();
        args.Handled = true;
    }

    private void ClearBendPoint(string dependencyId)
    {
        if (!_edgeVisuals.TryGetValue(dependencyId, out var visual) || visual.BendLine is null)
            return;
        _edgeBendPoints.Remove(dependencyId);
        GraphCanvas.Children.Remove(visual.BendLine);
        visual.BendLine = null;
        _layoutDirty = true;
        visual.Line.X2 = visual.StraightTarget.X;
        visual.Line.Y2 = visual.StraightTarget.Y;

        if (visual.BendHandle is not null)
        {
            var midpoint = new Point((visual.Line.X1 + visual.Line.X2) / 2, (visual.Line.Y1 + visual.Line.Y2) / 2);
            Canvas.SetLeft(visual.BendHandle, midpoint.X - 6);
            Canvas.SetTop(visual.BendHandle, midpoint.Y - 6);
        }

        if (visual.Arrowhead is not null)
        {
            GraphCanvas.Children.Remove(visual.Arrowhead);
            visual.Arrowhead = CreateArrowhead(visual.Line, visual.Dependency.Kind);
            if (visual.Arrowhead is not null)
                GraphCanvas.Children.Add(visual.Arrowhead);
        }

        UpdateEdgeLabelPosition(visual);
    }

    private void ApplyBendGeometry(EdgeVisual visual, Point bendPoint)
    {
        if (visual.BendHandle is not null)
        {
            Canvas.SetLeft(visual.BendHandle, bendPoint.X - 6);
            Canvas.SetTop(visual.BendHandle, bendPoint.Y - 6);
        }

        if (visual.BendLine is null)
        {
            visual.BendLine = new Line
            {
                Stroke = visual.Line.Stroke,
                StrokeThickness = visual.Line.StrokeThickness,
                IsHitTestVisible = false
            };
            GraphCanvas.Children.Insert(Math.Max(0, GraphCanvas.Children.IndexOf(visual.Line) + 1), visual.BendLine);
        }

        visual.BendLine.X1 = bendPoint.X;
        visual.BendLine.Y1 = bendPoint.Y;
        visual.BendLine.X2 = visual.StraightTarget.X;
        visual.BendLine.Y2 = visual.StraightTarget.Y;
        visual.Line.X2 = bendPoint.X;
        visual.Line.Y2 = bendPoint.Y;
        if (visual.Arrowhead is not null)
        {
            GraphCanvas.Children.Remove(visual.Arrowhead);
            visual.Arrowhead = CreateArrowhead(visual.BendLine, visual.Dependency.Kind);
            if (visual.Arrowhead is not null)
                GraphCanvas.Children.Add(visual.Arrowhead);
        }
        UpdateEdgeLabelPosition(visual);
    }

    /// <summary>Bends are keyed by dependency id, so a rebuild re-applies them and drops any whose edge is gone.</summary>
    private void RestoreBendPoints()
    {
        foreach (var staleId in _edgeBendPoints.Keys.Where(id => !_edgeVisuals.ContainsKey(id)).ToList())
            _edgeBendPoints.Remove(staleId);

        foreach (var (dependencyId, bendPoint) in _edgeBendPoints)
        {
            if (_edgeVisuals.TryGetValue(dependencyId, out var visual))
                ApplyBendGeometry(visual, bendPoint);
        }
    }

    private void Node_PointerPressed_Selection(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: ModEntry mod })
            return;

        var ctrlHeld = IsKeyDown(VirtualKey.Control);
        SelectMod(mod.Id, ctrlHeld);
    }

    private void Edge_PointerPressed(ModDependency dependency, PointerRoutedEventArgs args)
    {
        if (dependency.Id is null)
            return;

        var ctrlHeld = IsKeyDown(VirtualKey.Control);
        SelectDependency(dependency.Id, ctrlHeld);
        args.Handled = true;
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        // Click on empty canvas: clear selection or start marquee
        GraphCanvas.Focus(FocusState.Pointer);
        var pointer = args.GetCurrentPoint(GraphCanvas);
        if (pointer.Properties.IsMiddleButtonPressed)
        {
            _dragMode = DragMode.Pan;
            _panPointerStart = args.GetCurrentPoint(GraphScrollViewer).Position;
            _panScrollStart = new Point(GraphScrollViewer.HorizontalOffset, GraphScrollViewer.VerticalOffset);
            GraphCanvas.CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }

        var point = pointer.Position;
        if (FindNodeAt(point) is null && FindEdgeAt(point) is null)
        {
            _dragMode = DragMode.Marquee;
            _dragPointerStart = point;
            _marqueeRectangle = new Rectangle
            {
                Fill = new SolidColorBrush(((SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]).Color) { Opacity = 0.1 },
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_marqueeRectangle, point.X);
            Canvas.SetTop(_marqueeRectangle, point.Y);
            GraphCanvas.Children.Add(_marqueeRectangle);
        }
    }

    private ModDependency? FindEdgeAt(Point canvasPoint)
    {
        foreach (var visual in _edgeVisuals.Values)
        {
            var start = new Point(visual.Line.X1, visual.Line.Y1);
            var end = new Point(visual.Line.X2, visual.Line.Y2);
            if (GraphGeometry.DistanceToSegment(canvasPoint, start, end) <= EdgeHitTolerance)
                return visual.Dependency;
        }

        return null;
    }


    // Dragging the node body repositions it; the connector handle (below) is the only way to start a new edge.
    private void Node_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: ModEntry mod } button || !_nodeContainers.TryGetValue(mod.Id, out var container))
            return;

        var ctrlHeld = IsKeyDown(VirtualKey.Control);
        var shiftHeld = IsKeyDown(VirtualKey.Shift);
        
        // Ctrl+click = select/deselect without dragging
        if (ctrlHeld)
        {
            SelectMod(mod.Id, true, shiftHeld);
            args.Handled = true;
            return;
        }

        // Shift+click = range select from last selected node
        if (shiftHeld)
        {
            SelectMod(mod.Id, false, true);
            args.Handled = true;
            return;
        }

        // Regular click: select this mod and start drag (or multi-drag if already selected)
        if (!_selectedModIds.Contains(mod.Id))
            SelectMod(mod.Id, false);

        _dragMode = DragMode.Move;
        _dragSourceMod = mod;
        _dragPointerStart = args.GetCurrentPoint(GraphCanvas).Position;
        _dragNodeStart = new Point(Canvas.GetLeft(container), Canvas.GetTop(container));
        button.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void Node_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragMode != DragMode.Move || _dragSourceMod is null || !_nodeContainers.TryGetValue(_dragSourceMod.Id, out var container))
            return;

        var position = args.GetCurrentPoint(GraphCanvas).Position;
        var delta = new Point(position.X - _dragPointerStart.X, position.Y - _dragPointerStart.Y);

        // Move all selected nodes by the same delta
        foreach (var selectedModId in _selectedModIds)
        {
            if (!_nodeContainers.TryGetValue(selectedModId, out var selectedContainer))
                continue;

            var newLeft = Canvas.GetLeft(selectedContainer) + delta.X;
            var newTop = Canvas.GetTop(selectedContainer) + delta.Y;
            Canvas.SetLeft(selectedContainer, newLeft);
            Canvas.SetTop(selectedContainer, newTop);
            UpdateEdgesForNode(selectedModId, newLeft, newTop);
        }

        // Update the baseline for next frame
        _dragPointerStart = position;
    }

    private void Node_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button button)
            return;

        button.ReleasePointerCapture(args.Pointer);
        if (_dragMode == DragMode.Move)
        {
            // Persist positions for all moved nodes
            foreach (var selectedModId in _selectedModIds)
            {
                if (_nodeContainers.TryGetValue(selectedModId, out var container))
                    _nodePositions[selectedModId] = new Point(Canvas.GetLeft(container), Canvas.GetTop(container));
            }

            PersistLayout();
        }

        _dragMode = DragMode.None;
        _dragSourceMod = null;
    }

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Ellipse { Tag: ModEntry mod } handle || !_nodeContainers.ContainsKey(mod.Id))
            return;

        _dragMode = DragMode.Connect;
        _dragSourceMod = mod;
        handle.CapturePointer(args.Pointer);

        var center = GetNodeCenter(mod.Id);
        _dragPreviewLine = new Line
        {
            X1 = center.X,
            Y1 = center.Y,
            X2 = center.X,
            Y2 = center.Y,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsHitTestVisible = false
        };
        GraphCanvas.Children.Add(_dragPreviewLine);
        args.Handled = true;
    }

    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragMode != DragMode.Connect || _dragPreviewLine is null)
            return;

        var position = args.GetCurrentPoint(GraphCanvas).Position;
        _dragPreviewLine.X2 = position.X;
        _dragPreviewLine.Y2 = position.Y;
    }

    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Ellipse handle)
            return;

        handle.ReleasePointerCapture(args.Pointer);
        if (_dragPreviewLine is not null)
        {
            GraphCanvas.Children.Remove(_dragPreviewLine);
            _dragPreviewLine = null;
        }

        var releasePoint = args.GetCurrentPoint(GraphCanvas).Position;
        var targetContainer = FindNodeAt(releasePoint);
        var sourceMod = _dragSourceMod;
        _dragMode = DragMode.None;
        _dragSourceMod = null;

        if (targetContainer is null || sourceMod is null || targetContainer.Tag is not ModEntry target || target.Id == sourceMod.Id)
            return;

        ShowAddDependencyFlyout(sourceMod, target, targetContainer);
    }

    private void ReattachHandle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Ellipse handle || handle.Tag is not (string depId, bool isSource))
            return;
        if (!_edgeVisuals.TryGetValue(depId, out var visual))
            return;

        _dragMode = DragMode.ReattachEdge;
        _dragPointerStart = args.GetCurrentPoint(GraphCanvas).Position;
        _dragPreviewLine = new Line
        {
            X1 = visual.Line.X1,
            Y1 = visual.Line.Y1,
            X2 = isSource ? visual.Line.X2 : visual.Line.X1,
            Y2 = isSource ? visual.Line.Y2 : visual.Line.Y1,
            Stroke = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            IsHitTestVisible = false
        };
        GraphCanvas.Children.Add(_dragPreviewLine);
        handle.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void ReattachHandle_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragMode != DragMode.ReattachEdge || _dragPreviewLine is null)
            return;

        var position = args.GetCurrentPoint(GraphCanvas).Position;
        _dragPreviewLine.X2 = position.X;
        _dragPreviewLine.Y2 = position.Y;
    }

    private void ReattachHandle_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Ellipse handle || handle.Tag is not (string depId, bool isSource))
            return;
        if (!_edgeVisuals.TryGetValue(depId, out var visual))
            return;

        handle.ReleasePointerCapture(args.Pointer);
        if (_dragPreviewLine is not null)
        {
            GraphCanvas.Children.Remove(_dragPreviewLine);
            _dragPreviewLine = null;
        }

        var releasePoint = args.GetCurrentPoint(GraphCanvas).Position;
        var targetContainer = FindNodeAt(releasePoint);
        _dragMode = DragMode.None;

        if (targetContainer is null || targetContainer.Tag is not ModEntry targetMod)
            return;

        // Reattach the edge to the new owner or target
        if (isSource)
        {
            if (targetMod.Id == visual.Dependency.Target?.ModId)
                return; // Can't reattach to the same target

            // Transplant from old owner to new owner
            var oldOwner = visual.Owner;
            oldOwner.Dependencies = oldOwner.Dependencies.Where(d => d.Id != visual.Dependency.Id).ToList();
            RemoveEdgeVisual(visual.Dependency);

            var newDep = new ModDependency
            {
                Id = Guid.NewGuid().ToString(),
                Kind = visual.Dependency.Kind,
                Target = visual.Dependency.Target
            };
            targetMod.Dependencies.Add(newDep);
            AddEdgeVisual(targetMod, newDep);

            _ = Task.WhenAll(SaveAsync(oldOwner), SaveAsync(targetMod));
        }
        else
        {
            if (targetMod.Id == visual.Owner.Id)
                return; // Can't reattach to the source

            // Change the target of the dependency
            visual.Dependency.Target = new ModDependencyTarget(targetMod.Id, null, targetMod.Name);
            RemoveEdgeVisual(visual.Dependency);
            AddEdgeVisual(visual.Owner, visual.Dependency);

            _ = SaveAsync(visual.Owner);
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragMode == DragMode.Pan)
        {
            var current = args.GetCurrentPoint(GraphScrollViewer).Position;
            GraphScrollViewer.ChangeView(
                _panScrollStart.X - (current.X - _panPointerStart.X),
                _panScrollStart.Y - (current.Y - _panPointerStart.Y),
                null,
                disableAnimation: true);
            return;
        }

        if (_dragMode != DragMode.Marquee || _marqueeRectangle is null)
            return;

        var position = args.GetCurrentPoint(GraphCanvas).Position;
        var x = Math.Min(_dragPointerStart.X, position.X);
        var y = Math.Min(_dragPointerStart.Y, position.Y);
        var width = Math.Abs(position.X - _dragPointerStart.X);
        var height = Math.Abs(position.Y - _dragPointerStart.Y);

        Canvas.SetLeft(_marqueeRectangle, x);
        Canvas.SetTop(_marqueeRectangle, y);
        _marqueeRectangle.Width = width;
        _marqueeRectangle.Height = height;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragMode == DragMode.Pan)
        {
            GraphCanvas.ReleasePointerCapture(args.Pointer);
            _dragMode = DragMode.None;
            return;
        }

        if (_dragMode != DragMode.Marquee || _marqueeRectangle is null)
            return;

        var marqueeRect = new Rect(Canvas.GetLeft(_marqueeRectangle), Canvas.GetTop(_marqueeRectangle), _marqueeRectangle.Width, _marqueeRectangle.Height);
        GraphCanvas.Children.Remove(_marqueeRectangle);
        _marqueeRectangle = null;
        _dragMode = DragMode.None;

        var ctrlHeld = IsKeyDown(VirtualKey.Control);
        if (!ctrlHeld)
            _selectedModIds.Clear();

        // Select all nodes whose bounds intersect the marquee rectangle
        foreach (var (modId, container) in _nodeContainers)
        {
            var nodeRect = new Rect(Canvas.GetLeft(container), Canvas.GetTop(container), NodeWidth, NodeHeight);
            if (GraphGeometry.Intersects(marqueeRect, nodeRect))
                _selectedModIds.Add(modId);
        }

        _selectedDependencyIds.Clear();
        UpdateSelectionCount();
        UpdateNodeVisuals();
    }

    private void RegisterKeyboardAccelerators()
    {
        // The canvas outlives every graph rebuild, so re-registering would stack duplicate handlers.
        GraphCanvas.KeyboardAccelerators.Clear();

        // Ctrl+A: Select all nodes
        var selectAllAccel = new KeyboardAccelerator { Key = VirtualKey.A, Modifiers = VirtualKeyModifiers.Control };
        selectAllAccel.Invoked += (_, _) => SelectAllNodes();
        GraphCanvas.KeyboardAccelerators.Add(selectAllAccel);

        // Escape: Clear selection
        var clearSelectionAccel = new KeyboardAccelerator { Key = VirtualKey.Escape };
        clearSelectionAccel.Invoked += (_, _) => ClearSelection();
        GraphCanvas.KeyboardAccelerators.Add(clearSelectionAccel);

        // F5: Refresh/Reload graph
        var refreshAccel = new KeyboardAccelerator { Key = VirtualKey.F5 };
        refreshAccel.Invoked += (_, _) => Refresh();
        GraphCanvas.KeyboardAccelerators.Add(refreshAccel);

        var nextIssueAccel = new KeyboardAccelerator { Key = VirtualKey.F8 };
        nextIssueAccel.Invoked += (_, _) => SelectNextValidationIssue();
        GraphCanvas.KeyboardAccelerators.Add(nextIssueAccel);

        var exportAccel = new KeyboardAccelerator { Key = VirtualKey.E, Modifiers = VirtualKeyModifiers.Control };
        exportAccel.Invoked += (_, _) => _ = ExportSubgraphAsync();
        GraphCanvas.KeyboardAccelerators.Add(exportAccel);

        var importAccel = new KeyboardAccelerator { Key = VirtualKey.I, Modifiers = VirtualKeyModifiers.Control };
        importAccel.Invoked += (_, _) => _ = ImportSubgraphAsync();
        GraphCanvas.KeyboardAccelerators.Add(importAccel);

        var searchAccel = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        searchAccel.Invoked += (_, _) =>
        {
            GraphSearchBox.Focus(FocusState.Keyboard);
            GraphSearchBox.SelectAll();
        };
        GraphCanvas.KeyboardAccelerators.Add(searchAccel);
    }

    private void GraphSearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        var query = GraphSearchBox.Text.Trim();
        _searchResults = string.IsNullOrEmpty(query)
            ? new List<string>()
            : _mods.Where(mod => mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(mod => mod.Id)
                .ToList();
        _searchIndex = -1;
    }

    private void GraphSearchBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Enter or VirtualKey.F3)
        {
            SelectNextSearchResult();
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            GraphSearchBox.Text = string.Empty;
            GraphCanvas.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }

    private void SelectNextSearchResult()
    {
        if (_searchResults.Count == 0)
        {
            ShowInfo("No mods match the graph search.");
            return;
        }

        _searchIndex = (_searchIndex + 1) % _searchResults.Count;
        var modId = _searchResults[_searchIndex];
        SelectMod(modId, false);
        BringModIntoView(modId);
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Graph_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Delete && _selectedDependencyIds.Count > 0)
        {
            DeleteSelectedDependencies();
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.C && IsKeyDown(VirtualKey.Control))
        {
            args.Handled = CopyDependenciesToClipboard();
        }
        else if (args.Key == VirtualKey.Z && IsKeyDown(VirtualKey.Control))
        {
            args.Handled = true;
            UiTask.Run(UndoLastEditAsync, nameof(Graph_KeyDown));
        }
        else if (args.Key == VirtualKey.Y && IsKeyDown(VirtualKey.Control))
        {
            args.Handled = true;
            UiTask.Run(RedoLastEditAsync, nameof(Graph_KeyDown));
        }
        else if (args.Key == VirtualKey.V && IsKeyDown(VirtualKey.Control))
        {
            args.Handled = true;
            UiTask.Run(PasteDependenciesAsync, nameof(Graph_KeyDown));
        }
    }

    private bool CopyDependenciesToClipboard()
    {
        if (ResolveCopySourceModId() is not { } sourceId || _mods.FirstOrDefault(mod => mod.Id == sourceId) is not { } source)
        {
            ShowInfo("Select a mod to copy dependencies from.");
            return false;
        }

        _dependencyClipboard = source.Dependencies.Select(CopyDependency).ToList();
        ShowInfo($"Copied {Dependencies(_dependencyClipboard.Count)} from {source.Name}. Select mods and press Ctrl+V.");
        return true;
    }

    private async Task PasteDependenciesAsync()
    {
        if (_dependencyClipboard.Count == 0)
        {
            ShowInfo("Copy dependencies from a mod first with Ctrl+C.");
            return;
        }

        if (_selectedModIds.Count == 0)
        {
            ShowInfo("Select the mods to paste the dependencies onto.");
            return;
        }

        var knownModIds = _mods.Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
        // A mod may have been deleted since the copy, and pasting it would create an edge to nothing.
        var pasteable = _dependencyClipboard
            .Where(dependency => dependency.Target?.ModId is { } targetId && knownModIds.Contains(targetId))
            .ToList();
        if (pasteable.Count == 0)
        {
            ShowInfo("None of the copied dependencies point at mods that are still installed.", InfoBarSeverity.Warning);
            return;
        }

        var targets = _mods.Where(mod => _selectedModIds.Contains(mod.Id)).ToList();
        _editHistory.Capture(targets);
        var result = AppServices.BatchDependencyService.PasteDependencies(pasteable, targets);
        if (result.AddedDependencies == 0)
        {
            ShowInfo("The selected mods already have those dependencies.");
            return;
        }

        await SaveChangedAsync(result.ChangedModIds);
        InitializeGraph();

        var dropped = _dependencyClipboard.Count - pasteable.Count;
        var skipped = dropped == 0 ? string.Empty : $" Skipped {dropped} pointing at uninstalled mods.";
        ShowInfo($"Pasted {Dependencies(result.AddedDependencies)} onto {Mods(result.ChangedModIds.Count)}.{skipped}", InfoBarSeverity.Success);
    }

    private void SelectAllNodes()
    {
        _selectedModIds.Clear();
        foreach (var modId in _mods.Select(m => m.Id))
            _selectedModIds.Add(modId);
        _selectedDependencyIds.Clear();
        UpdateSelectionCount();
        UpdateNodeVisuals();
    }

    private void Refresh()
    {
        PageInfoBar.IsOpen = false;
        UiTask.Run(LoadGraphAsync, nameof(Refresh), ShowLoadError);
    }

    private void DeleteSelectedDependencies()
    {
        // Collect all dependencies to delete grouped by owner
        var depsToDelete = new Dictionary<string, List<ModDependency>>(StringComparer.Ordinal);
        var deletedCount = 0;
        foreach (var depId in _selectedDependencyIds)
        {
            if (!_edgeVisuals.TryGetValue(depId, out var visual))
                continue;

            var ownerId = visual.Owner.Id;
            if (!depsToDelete.TryGetValue(ownerId, out var list))
                depsToDelete[ownerId] = list = new();
            
            list.Add(visual.Dependency);
        }

        _editHistory.Capture(_mods.Where(mod => depsToDelete.ContainsKey(mod.Id)));

        // Apply deletions
        foreach (var (ownerId, deps) in depsToDelete)
        {
            var owner = _mods.FirstOrDefault(m => m.Id == ownerId);
            if (owner is null)
                continue;

            var newDeps = owner.Dependencies.Where(d => !deps.Any(td => td.Id == d.Id)).ToList();
            deletedCount += owner.Dependencies.Count - newDeps.Count;
            owner.Dependencies = newDeps;
            foreach (var dep in deps)
                RemoveEdgeVisual(dep);

            _ = SaveAsync(owner);
        }

        _selectedDependencyIds.Clear();
        UpdateSelectionCount();
        RefreshCatalogValidation();
        if (deletedCount > 0)
            ShowInfo($"Removed {Dependencies(deletedCount)} from {Mods(depsToDelete.Count)}.", InfoBarSeverity.Success);
    }

    private Point GetNodeCenter(string modId) =>
        _nodeContainers.TryGetValue(modId, out var container)
            ? new Point(Canvas.GetLeft(container) + NodeWidth / 2, Canvas.GetTop(container) + NodeHeight / 2)
            : default;

    private void ShowAddDependencyFlyout(ModEntry source, ModEntry target, FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        foreach (var kind in new[] { ModDependencyKind.Requires, ModDependencyKind.LoadAfter, ModDependencyKind.LoadBefore, ModDependencyKind.Conflicts })
        {
            var item = new MenuFlyoutItem { Text = DescribeNewEdge(kind, target.Name) };
            item.Click += (_, _) => AddDependency(source, target, kind);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    private static string DescribeNewEdge(ModDependencyKind kind, string targetName) => kind switch
    {
        ModDependencyKind.Requires => $"Requires {targetName}",
        ModDependencyKind.LoadAfter => $"Load after {targetName}",
        ModDependencyKind.LoadBefore => $"Load before {targetName}",
        ModDependencyKind.Conflicts => $"Conflicts with {targetName}",
        _ => targetName
    };

    private void AddDependency(ModEntry source, ModEntry target, ModDependencyKind kind)
    {
        _editHistory.Capture(new[] { source });
        var newDependency = new ModDependency
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceModId = source.Id,
            Kind = kind,
            Origin = "manual",
            Target = new ModDependencyTarget(target.Id, target.Remote, target.Name)
        };

        source.Dependencies = source.Dependencies.Append(newDependency).ToList();
        AddEdgeVisual(source, newDependency);
        _ = SaveAsync(source);
    }

    private void RemoveDependency(ModEntry owner, ModDependency dependency)
    {
        _editHistory.Capture(new[] { owner });
        owner.Dependencies = owner.Dependencies.Where(candidate => candidate.Id != dependency.Id).ToList();
        RemoveEdgeVisual(dependency);
        _ = SaveAsync(owner);
    }

    private void PersistAndRebuild(ModEntry changed)
    {
        InitializeGraph();
        _ = SaveAsync(changed);
    }

    private void AutoArrange_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string kindName } && Enum.TryParse<GraphLayoutKind>(kindName, out var kind))
        {
            AppServices.AppSettings.GraphLayoutKind = kind;
            AppServices.AppSettingsStore.Save(AppServices.AppSettings);
        }

        _nodePositions.Clear();
        // Bend points are absolute canvas coordinates, so they are meaningless once every node moves.
        _edgeBendPoints.Clear();
        InitializeGraph();
        PersistLayout();
    }

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (_isSyncingZoom || GraphScrollViewer is null)
            return;

        var zoomFactor = (float)(args.NewValue / 100.0);
        GraphScrollViewer.ChangeView(null, null, zoomFactor, disableAnimation: true);
        if (ZoomText is not null)
            ZoomText.Text = $"{args.NewValue:0}%";
    }

    private void GraphScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_isSyncingZoom || ZoomSlider is null)
            return;

        var value = GraphScrollViewer.ZoomFactor * 100;
        _isSyncingZoom = true;
        try
        {
            ZoomSlider.Value = value;
            if (ZoomText is not null)
                ZoomText.Text = $"{value:0}%";
        }
        finally
        {
            _isSyncingZoom = false;
        }
    }

    private void FitGraph_Click(object sender, RoutedEventArgs args)
    {
        if (_nodePositions.Count == 0 || GraphScrollViewer.ActualWidth <= 0 || GraphScrollViewer.ActualHeight <= 0)
            return;

        var minX = _nodePositions.Values.Min(point => point.X);
        var minY = _nodePositions.Values.Min(point => point.Y);
        var maxX = _nodePositions.Values.Max(point => point.X) + NodeWidth;
        var maxY = _nodePositions.Values.Max(point => point.Y) + NodeHeight;
        var contentWidth = Math.Max(1, maxX - minX + CanvasMargin * 2);
        var contentHeight = Math.Max(1, maxY - minY + CanvasMargin * 2);
        var fitZoom = Math.Clamp(Math.Min(GraphScrollViewer.ActualWidth / contentWidth, GraphScrollViewer.ActualHeight / contentHeight), 0.25, 2.0);

        ZoomSlider.Value = fitZoom * 100;
        GraphScrollViewer.ChangeView(Math.Max(0, minX * fitZoom - CanvasMargin), Math.Max(0, minY * fitZoom - CanvasMargin), (float)fitZoom, disableAnimation: true);
    }

    private void BackToMods_Click(object sender, RoutedEventArgs args)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private void ExportSubgraph_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(ExportSubgraphAsync, nameof(ExportSubgraph_Click), ShowTransferError);

    private void ImportSubgraph_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(ImportSubgraphAsync, nameof(ImportSubgraph_Click), ShowTransferError);

    private void ShowTransferError(Exception exception) =>
        ShowInfo($"The dependency file could not be used. {exception.Message}", InfoBarSeverity.Error);

    private void RemoveSelectedConflicts_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(RemoveSelectedConflictsAsync, nameof(RemoveSelectedConflicts_Click));

    private void CopySelectedDependencies_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(CopySelectedDependenciesAsync, nameof(CopySelectedDependencies_Click));

    private void AddBatchDependency_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(AddBatchDependencyAsync, nameof(AddBatchDependency_Click));

    private void SelectDependencies_Click(object sender, RoutedEventArgs args) =>
        ExpandSelection(DependencySelectionService.ExpandDependencies(_mods, _selectedModIds));

    private void SelectDependents_Click(object sender, RoutedEventArgs args) =>
        ExpandSelection(DependencySelectionService.ExpandDependents(_mods, _selectedModIds));

    private void ExpandSelection(IReadOnlySet<string> expanded)
    {
        if (_selectedModIds.Count == 0)
        {
            ShowInfo("Select at least one mod first.");
            return;
        }

        _selectedModIds.Clear();
        foreach (var modId in expanded)
            if (_nodeContainers.ContainsKey(modId))
                _selectedModIds.Add(modId);

        _selectedDependencyIds.Clear();
        UpdateSelectionCount();
        UpdateNodeVisuals();
        ShowInfo($"Selected {Mods(_selectedModIds.Count)}.", InfoBarSeverity.Success);
    }

    private void OpenPresetsMenu_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(() => ShowPresetsAsync(sender as FrameworkElement), nameof(OpenPresetsMenu_Click), ShowTransferError);

    private void OpenArrangeMenu_Click(object sender, RoutedEventArgs args)
    {
        _arrangeFlyout.Items.Clear();
        AddArrangeItem("Hierarchical (by requirement depth)", "Hierarchical");
        AddArrangeItem("Grid (by name)", "Grid");
        AddArrangeItem("Circular", "Circular");
        _arrangeFlyout.ShowAt((FrameworkElement)sender);
    }

    private void AddArrangeItem(string text, string tag)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = tag };
        item.Click += AutoArrange_Click;
        _arrangeFlyout.Items.Add(item);
    }

    private async Task ShowPresetsAsync(FrameworkElement? anchor)
    {
        if (anchor is null)
            return;

        await BuildPresetMenuAsync();
        _presetFlyout.ShowAt(anchor);
    }

    private Task BuildPresetMenuAsync()
    {
        _presetFlyout.Items.Clear();

        var save = new MenuFlyoutItem { Text = "Save selection as preset...", IsEnabled = _selectedModIds.Count > 0 };
        save.Click += (_, _) => UiTask.Run(SavePresetAsync, nameof(SavePresetAsync), ShowTransferError);
        _presetFlyout.Items.Add(save);

        var manage = new MenuFlyoutItem { Text = "Manage presets...", IsEnabled = _presets.Count > 0 };
        manage.Click += (_, _) => UiTask.Run(ManagePresetsAsync, nameof(ManagePresetsAsync), ShowTransferError);
        _presetFlyout.Items.Add(manage);

        if (_presets.Count == 0)
        {
            _presetFlyout.Items.Add(new MenuFlyoutItem { Text = "No saved presets", IsEnabled = false });
            return Task.CompletedTask;
        }

        _presetFlyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var preset in _presets)
        {
            var item = new MenuFlyoutItem { Text = $"Apply \"{preset.Name}\"" };
            item.Click += (_, _) => UiTask.Run(() => ApplyPresetAsync(preset), nameof(ApplyPresetAsync));
            _presetFlyout.Items.Add(item);
        }

        return Task.CompletedTask;
    }

    private async Task SavePresetAsync()
    {
        if (_selectedModIds.Count == 0)
            return;

        var nameBox = new TextBox { Header = "Preset name", PlaceholderText = "SKSE base stack" };
        var dialog = new ContentDialog
        {
            Title = "Save dependency preset",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
            return;

        var json = AppServices.DependencySubgraphService.Export(_mods, _selectedModIds);
        _presets = await AppServices.DependencyPresetStore.SaveAsync(new DependencyPreset(nameBox.Text.Trim(), json));
        ShowInfo($"Saved preset \"{nameBox.Text.Trim()}\" from {Mods(_selectedModIds.Count)}.", InfoBarSeverity.Success);
    }

    private async Task ManagePresetsAsync()
    {
        var panel = new StackPanel { Spacing = 8 };
        var list = new ListView { ItemsSource = _presets, SelectionMode = ListViewSelectionMode.Single };
        panel.Children.Add(list);
        var delete = new Button { Content = "Delete selected", IsEnabled = false };
        list.SelectionChanged += (_, _) => delete.IsEnabled = list.SelectedItem is DependencyPreset;
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is DependencyPreset preset)
            {
                _presets = await AppServices.DependencyPresetStore.DeleteAsync(preset.Name);
                list.ItemsSource = _presets;
                delete.IsEnabled = false;
            }
        };
        panel.Children.Add(delete);

        var dialog = new ContentDialog
        {
            Title = "Manage dependency presets",
            Content = panel,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }
    private async Task ApplyPresetAsync(DependencyPreset preset)
    {
        _editHistory.Capture(_mods);
        var result = AppServices.DependencySubgraphService.ImportInto(preset.SubgraphJson, _mods);
        if (result.AddedEdges == 0)
        {
            ShowInfo($"\"{preset.Name}\" added nothing; those mods are missing or already connected.");
            return;
        }

        await SaveChangedAsync(result.ChangedModIds);
        InitializeGraph();
        ShowInfo($"Applied \"{preset.Name}\": {Dependencies(result.AddedEdges)} added, {result.SkippedEdges} skipped.", InfoBarSeverity.Success);
    }

    private async Task RemoveSelectedConflictsAsync()
    {
        if (_selectedModIds.Count == 0)
        {
            ShowInfo("Select at least one mod first.");
            return;
        }

        _editHistory.Capture(_mods.Where(mod => _selectedModIds.Contains(mod.Id)));
        var result = AppServices.BatchDependencyService.RemoveConflicts(_mods, _selectedModIds);
        if (result.RemovedDependencies == 0)
        {
            ShowInfo("The selected mods have no conflict connections.");
            return;
        }
        await SaveChangedAsync(result.ChangedModIds);
        InitializeGraph();
        ShowInfo($"Removed {result.RemovedDependencies} conflict connection{(result.RemovedDependencies == 1 ? string.Empty : "s")} from {Mods(result.ChangedModIds.Count)}.", InfoBarSeverity.Success);
    }

    private async Task CopySelectedDependenciesAsync()
    {
        if (_selectedModIds.Count < 2)
        {
            ShowInfo("Select the source mod and at least one mod to copy to.");
            return;
        }

        if (ResolveCopySourceModId() is not { } sourceId || _mods.FirstOrDefault(mod => mod.Id == sourceId) is not { } source)
            return;

        var targets = _mods.Where(mod => _selectedModIds.Contains(mod.Id) && mod.Id != source.Id).ToList();
        _editHistory.Capture(targets);
        var result = AppServices.BatchDependencyService.CopyDependencies(source, targets);
        if (result.AddedDependencies == 0)
        {
            ShowInfo($"The selected mods already have every dependency from {source.Name}.");
            return;
        }

        await SaveChangedAsync(result.ChangedModIds);
        InitializeGraph();
        ShowInfo($"Copied {Dependencies(result.AddedDependencies)} from {source.Name} to {Mods(result.ChangedModIds.Count)}.", InfoBarSeverity.Success);
    }

    /// <summary>Selection is an unordered set, so prefer the explicitly clicked node and fall back to layout order.</summary>
    private string? ResolveCopySourceModId()
    {
        if (_lastSelectedNodeId is { } anchor && _selectedModIds.Contains(anchor))
            return anchor;

        return _selectedModIds
            .Where(_nodeContainers.ContainsKey)
            .OrderBy(modId => Canvas.GetTop(_nodeContainers[modId]))
            .ThenBy(modId => Canvas.GetLeft(_nodeContainers[modId]))
            .ThenBy(modId => modId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task UndoLastEditAsync()
    {
        var restored = _editHistory.Undo(_mods);
        if (restored.Count == 0)
        {
            ShowInfo("There is nothing to undo.");
            return;
        }

        await SaveChangedAsync(restored);
        InitializeGraph();
        ShowInfo($"Undid the last dependency change on {Mods(restored.Count)}.", InfoBarSeverity.Success);
    }

    private async Task RedoLastEditAsync()
    {
        var restored = _editHistory.Redo(_mods);
        if (restored.Count == 0)
        {
            ShowInfo("There is nothing to redo.");
            return;
        }

        await SaveChangedAsync(restored);
        InitializeGraph();
        ShowInfo($"Redid the last dependency change on {Mods(restored.Count)}.", InfoBarSeverity.Success);
    }

    private async Task SaveChangedAsync(IReadOnlyList<string> changedModIds)
    {
        var changed = changedModIds.ToHashSet(StringComparer.Ordinal);
        foreach (var mod in _mods.Where(mod => changed.Contains(mod.Id)))
            await SaveAsync(mod);
    }

    private async Task AddBatchDependencyAsync()
    {
        if (_selectedModIds.Count == 0)
            return;

        var targetBox = new ComboBox
        {
            Header = "Target mod",
            ItemsSource = _mods.Where(mod => !_selectedModIds.Contains(mod.Id)).ToList(),
            DisplayMemberPath = nameof(ModEntry.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var kindBox = new ComboBox
        {
            Header = "Dependency kind",
            ItemsSource = new[]
            {
                ModDependencyKind.Requires,
                ModDependencyKind.LoadAfter,
                ModDependencyKind.LoadBefore,
                ModDependencyKind.Conflicts
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"Add a dependency to {_selectedModIds.Count} selected mod{(_selectedModIds.Count == 1 ? string.Empty : "s")}." });
        content.Children.Add(targetBox);
        content.Children.Add(kindBox);

        var dialog = new ContentDialog
        {
            Title = "Add dependency to selected mods",
            Content = content,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || targetBox.SelectedItem is not ModEntry target || kindBox.SelectedItem is not ModDependencyKind kind)
            return;

        _editHistory.Capture(_mods.Where(mod => _selectedModIds.Contains(mod.Id)));
        var result = AppServices.BatchDependencyService.AddDependency(_mods, _selectedModIds, target.Id, kind);
        if (result.AddedDependencies == 0)
        {
            ShowInfo($"Every selected mod already declares that dependency on {target.Name}.");
            return;
        }

        await SaveChangedAsync(result.ChangedModIds);
        InitializeGraph();
        ShowInfo($"Added {DescribeNewEdge(kind, target.Name)} to {Mods(result.ChangedModIds.Count)}.", InfoBarSeverity.Success);
    }

    private async Task ExportSubgraphAsync()
    {
        if (_mods.Count == 0)
            return;

        var selectedIds = _selectedModIds.Count > 0 ? _selectedModIds : _mods.Select(mod => mod.Id);
        var nodePositions = _nodePositions
            .Where(entry => selectedIds.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => new DependencySubgraphPoint(entry.Value.X, entry.Value.Y), StringComparer.Ordinal);
        var bendPoints = _edgeBendPoints
            .ToDictionary(entry => entry.Key, entry => new DependencySubgraphPoint(entry.Value.X, entry.Value.Y), StringComparer.Ordinal);
        var json = AppServices.DependencySubgraphService.Export(_mods, selectedIds, nodePositions, bendPoints);
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            SuggestedFileName = "dependency-subgraph.json"
        };
        picker.FileTypeChoices.Add("Dependency subgraph", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
            await FileIO.WriteTextAsync(file, json);
    }

    private async Task ImportSubgraphAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        // Check the size before reading, so a huge file cannot be pulled into memory first.
        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > (ulong)DependencySubgraphService.MaxImportBytes)
        {
            ShowInfo("That file is too large to be a dependency subgraph.", InfoBarSeverity.Error);
            return;
        }

        _editHistory.Capture(_mods);
        var result = AppServices.DependencySubgraphService.ImportInto(await FileIO.ReadTextAsync(file), _mods);
        if (result.NodePositions is not null)
        {
            foreach (var (modId, point) in result.NodePositions)
                if (_mods.Any(mod => mod.Id == modId))
                    _nodePositions[modId] = new Point(point.X, point.Y);
        }
        if (result.BendPoints is not null)
        {
            foreach (var (dependencyId, point) in result.BendPoints)
                if (_edgeVisuals.ContainsKey(dependencyId) || _mods.SelectMany(mod => mod.Dependencies).Any(dependency => dependency.Id == dependencyId))
                    _edgeBendPoints[dependencyId] = new Point(point.X, point.Y);
        }

        if (result.AddedEdges > 0 || result.NodePositions is not null || result.BendPoints is not null)
        {
            await SaveChangedAsync(result.ChangedModIds);
            InitializeGraph();
            PersistLayout();
        }

        var dialog = new ContentDialog
        {
            Title = "Dependency import complete",
            Content = $"Added {result.AddedEdges} dependenc{(result.AddedEdges == 1 ? "y" : "ies")}. Skipped {result.SkippedEdges}. Unknown nodes: {result.UnknownNodes}.",
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task SaveAsync(ModEntry changed)
    {
        try
        {
            await _store.UpsertAsync(changed);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("Failed to save a dependency graph edit.", exception);
        }
    }

    private void Node_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is Button { Tag: ModEntry mod })
            UiTask.Run(() => EditDependenciesAsync(mod), nameof(Node_DoubleTapped));
    }

    private void Node_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Button { Tag: ModEntry mod })
            return;

        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            args.Handled = true;
            UiTask.Run(() => EditDependenciesAsync(mod), nameof(Node_KeyDown));
            return;
        }

        var step = IsKeyDown(VirtualKey.Shift) ? FineNudgeStep : NudgeStep;
        var (deltaX, deltaY) = args.Key switch
        {
            VirtualKey.Left => (-step, 0d),
            VirtualKey.Right => (step, 0d),
            VirtualKey.Up => (0d, -step),
            VirtualKey.Down => (0d, step),
            _ => (0d, 0d)
        };

        if (deltaX == 0 && deltaY == 0)
            return;

        // Arrow keys are the keyboard equivalent of dragging a node.
        if (!_selectedModIds.Contains(mod.Id))
            SelectMod(mod.Id, false);

        NudgeSelectedNodes(deltaX, deltaY);
        _layoutDirty = true;
        args.Handled = true;
    }

    private void NudgeSelectedNodes(double deltaX, double deltaY)
    {
        foreach (var modId in _selectedModIds)
        {
            if (!_nodeContainers.TryGetValue(modId, out var container))
                continue;

            var left = Math.Max(0, Canvas.GetLeft(container) + deltaX);
            var top = Math.Max(0, Canvas.GetTop(container) + deltaY);
            Canvas.SetLeft(container, left);
            Canvas.SetTop(container, top);
            _nodePositions[modId] = new Point(left, top);
            UpdateEdgesForNode(modId, left, top);
        }
    }

    private async Task EditDependenciesAsync(ModEntry mod)
    {
        var dialog = new ModDependencyEditDialog(mod, _mods) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _editHistory.Capture(new[] { mod });
        mod.Dependencies = dialog.Dependencies.ToList();
        InitializeGraph();
        await SaveAsync(mod);
    }
}
