using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Drives a FOMOD's install steps one visible step at a time. One <see cref="ContentDialog"/> instance
/// is reused for every step (no nested dialogs); Next/Back never close the dialog themselves, only the
/// final "Install" click (at the summary) does, via <see cref="ContentDialogResult.Primary"/>.
///
/// Selection state is model-owned: every step keeps a <see cref="FomodStepDraft"/>, so Next/Back
/// commit/restore drafts from the model instead of reading <c>IsChecked</c> back out of the visual
/// tree. A compact step indicator (header, counter, progress bar) shows position, and
/// <see cref="EditorHost"/> renders the active step's groups as cards of radio/checkbox option
/// rows with thumbnails; the summary step shows a read-only recap of every committed step.
/// </summary>
public sealed partial class FomodInstallWizardDialog : ContentDialog
{
    /// <summary>Model-owned draft for one step: per-group selected plugins, independent of the visual tree.</summary>
    private sealed class FomodStepDraft
    {
        public required FomodInstallStep Step { get; init; }
        public List<FomodGroupDraft> Groups { get; } = new();
    }

    /// <summary>One group's picked plugins; an empty list means "(none)" (or nothing picked yet).</summary>
    private sealed class FomodGroupDraft
    {
        public required FomodGroup Group { get; init; }
        public List<FomodPlugin> SelectedPlugins { get; init; } = new();
    }

    private readonly FomodModule _module;
    private readonly IFomodFileStateProvider _files;
    private readonly IFomodVersionProvider _versions;
    private readonly FomodSelectionResolver _engine;
    private readonly List<FomodStepSelection> _committed = new();
    private readonly List<FomodStepDraft> _drafts = new();
    private readonly string _archivePath;
    private int _editorSeed;
    private int _displayedStepIndex = -2; // -2 = not yet started, -1 = summary, >=0 = a real step index

    // Decoded images finished by PreloadImagesAsync, keyed by normalized entry path. Written
    // from threadpool continuations while Render reads it on the UI thread -> ConcurrentDictionary.
    private readonly ConcurrentDictionary<string, FomodImageService.FomodImageData> _preloadedImages = new();

    public FomodInstallWizardDialog(
        FomodModule module, IFomodFileStateProvider fileStateProvider, string archivePath,
        string defaultDestination = "", string? gameExecutablePath = null)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _module = module;
        _files = fileStateProvider;
        _versions = new FomodVersionProvider(gameExecutablePath);
        _engine = new FomodSelectionResolver(_versions);
        _archivePath = archivePath ?? string.Empty;

        // The destination base is the TARGET FOLDER chosen at the summary (default the game's
        // PluginDataFolder). Every file's FOMOD destination is relative to it: an empty
        // destination places the file directly in the base, and a non-empty one nests under
        // it (e.g. "SKSE" -> "Data/SKSE"). It also feeds the reuse signature, the recorded
        // install path (install root + base), and the remembered LastInstallPath.
        if (!string.IsNullOrWhiteSpace(defaultDestination))
            DestinationBox.Text = defaultDestination;
        UpdateDestinationValidity();

        if (module.Warnings.Count > 0)
        {
            WarningsBar.Message = string.Join(" ", module.Warnings);
            WarningsBar.IsOpen = true;
        }

        // Pre-install check of the module's own gate (e.g. a minimum game version). The FOMOD
        // spec marks moduleDependencies informational - we surface it as a non-blocking warning
        // rather than silently ignoring a failed gate.
        if (module.ModuleDependency is not null &&
            !new FomodDependencyResolver().Evaluate(module.ModuleDependency, new Dictionary<string, string>(), _files, _versions))
        {
            WarningsBar.Message = WarningsBar.IsOpen
                ? WarningsBar.Message + " This mod declares a module requirement (e.g. a minimum game version) that may not be met."
                : "This mod declares a module requirement (e.g. a minimum game version) that may not be met.";
            WarningsBar.IsOpen = true;
        }

        // Start decoding every image in the archive now — before the dialog renders its first
        // step — so completed images appear instantly and in-flight ones pop in as they finish.
        // Fire-and-forget: the dialog shows immediately and never waits on this.
        _ = PreloadImagesAsync();

        BuildHeader();
        AdvanceTo(0);
    }

    /// <summary>
    /// Pre-extracts every FOMOD image in the archive (module image plus all option previews) in
    /// ONE pass — a solid 7z archive must not be re-decoded per image — and stores completed
    /// results in <see cref="_preloadedImages"/>. Each decoded image is also registered in the
    /// service's per (archive, entry, size) cache, so a later
    /// <see cref="LoadModuleImageAsync"/>/<see cref="LoadPluginThumbnailAsync"/> that lands before
    /// the preload finishes is a dict hit instead of an archive read.
    /// </summary>
    private async Task PreloadImagesAsync()
    {
        var paths = _module.InstallSteps
                .SelectMany(step => step.Groups)
                .SelectMany(group => group.Plugins)
                .Select(plugin => plugin.ImagePath)
                .Append(_module.ModuleImage)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!.Replace('\\', '/'))
                .Distinct()
                .ToList();
        if (paths.Count == 0)
            return;

        try
        {
            var images = await AppServices.FomodImageService.PreloadAsync(_archivePath, paths, 128);
            foreach (var (path, data) in images)
                _preloadedImages[path] = data;
        }
        catch (OperationCanceledException)
        {
            // Dialog closed while decoding; the dialog is being torn down.
        }
        catch (Exception)
        {
            // Undecodable archive: rows show without thumbnails; install is unaffected.
        }
    }

    /// <summary>
    /// Populates the identity header from the module (name + Author · Version · Description) and,
    /// when the FOMOD carries a module image, loads it lazily — a missing/undecodable image simply
    /// leaves the frame hidden.
    /// </summary>
    private void BuildHeader()
    {
        ModuleName.Text = _module.Name;

        var subtitle = new List<string>();
        if (!string.IsNullOrWhiteSpace(_module.Author))
            subtitle.Add(_module.Author);
        if (!string.IsNullOrWhiteSpace(_module.Version))
            subtitle.Add(_module.Version);
        if (!string.IsNullOrWhiteSpace(_module.Description))
            subtitle.Add(_module.Description);
        ModuleSubtitle.Text = string.Join("  ·  ", subtitle);
        ModuleSubtitle.Visibility = subtitle.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Optional FOMOD colour on the title. The XSD default is "000000" (RRGGBB); authors may
        // also supply 8 hex digits with a leading alpha byte (AARRGGBB), which we strip.
        if (_module.ModuleTitleColor is { } color)
        {
            if (color > 0xFFFFFF)
                color >>= 8;
            var r = (byte)((color >> 16) & 0xFF);
            var g = (byte)((color >> 8) & 0xFF);
            var b = (byte)(color & 0xFF);
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            ModuleName.Foreground = brush;
            ModuleSubtitle.Foreground = brush;
        }

        // Optional FOMOD moduleImage height: override the fixed 64px frame when the author
        // declared one. The image is still drawn with Uniform stretch so aspect is preserved.
        if (_module.ModuleImageHeight > 0)
        {
            ModuleImageFrame.Height = _module.ModuleImageHeight;
            ModuleImageFrame.Width = _module.ModuleImageHeight;
        }

        var showImage = !string.IsNullOrWhiteSpace(_module.ModuleImage) && !_module.ModuleImageHidden;
        if (showImage)
        {
            ModuleImageFrame.Visibility = Visibility.Visible;
            // Fire-and-forget: load off the UI thread; the awaited continuation marshals back.
            _ = LoadModuleImageAsync();
        }

        // Optional FOMOD title position. Left/RightOfImage use the default layout (image on the
        // left, title block beside it); Right mirrors it: the title block moves to the far right
        // and the image sits between the title and the left edge. We reorder the two children
        // of the header panel to achieve this.
        if (showImage && string.Equals(_module.ModuleTitlePosition, "Right", StringComparison.OrdinalIgnoreCase))
        {
            // Default layout: [image, title]. Right layout: [title, image].
            var title = HeaderPanel.Children[^1];
            HeaderPanel.Children.RemoveAt(HeaderPanel.Children.Count - 1);
            HeaderPanel.Children.Insert(0, title);
        }
    }

    private async Task LoadModuleImageAsync()
    {
        // Fast path: the background preload may already hold the decoded image — wrap it straight
        // away on this UI thread, no await.
        if (TryGetPreloaded(_module.ModuleImage, out var preloaded))
        {
            ModuleImage.Source = FomodImageService.CreateBitmapSource(preloaded);
            return;
        }

        // Decode off-thread; the awaited continuation returns to the UI thread, where the
        // WriteableBitmap must be created.
        var data = await AppServices.FomodImageService.LoadArchiveImageAsync(_archivePath, _module.ModuleImage, 128);
        if (data is null)
            return;
        ModuleImage.Source = FomodImageService.CreateBitmapSource(data);
    }

    /// <summary>True when the background preload already produced <paramref name="data"/> for this entry path.</summary>
    private bool TryGetPreloaded(string? entryPathRaw, out FomodImageService.FomodImageData? data)
    {
        var normalized = entryPathRaw?.Replace('\\', '/');
        data = normalized is null ? null : _preloadedImages.TryGetValue(normalized, out var cached) ? cached : null;
        return data is not null;
    }

    /// <summary>Valid only after the dialog closed with <see cref="ContentDialogResult.Primary"/>.</summary>
    public IReadOnlyList<FomodStepSelection> Selections => _committed;

    public IReadOnlyList<FomodFileInstall> ResolvedFiles => _engine.ResolveFileInstalls(_module, _committed, _files);

    /// <summary>
    /// The destination base the user chose (trimmed, install-root-relative, validated empty-or-safe).
    /// Valid only after the dialog closed with <see cref="ContentDialogResult.Primary"/>.
    /// </summary>
    public string Destination => DestinationBox.Text.Trim();

    /// <summary>Whether the user chose to remember the destination base for future installs.</summary>
    public bool RememberPath => RememberPathCheck.IsChecked == true;

    /// <summary>Deterministic reuse key for the current selections and destination base.</summary>
    public string SelectionSignature => _engine.ComputeSelectionSignature(_committed, Destination);

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_displayedStepIndex == -1)
        {
            // At the summary: validate the destination before letting the dialog close with Primary.
            if (!TryValidateDestination())
            {
                args.Cancel = true;
                return;
            }
            return; // Let the dialog actually close with Result = Primary.
        }

        args.Cancel = true;
        if (!TryCommitCurrentStep(out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        AdvanceTo(_displayedStepIndex + 1);
    }

    private void DestinationBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateDestinationValidity();

    /// <summary>Blocks the Install click while the destination base is an unsafe relative path.</summary>
    private bool TryValidateDestination()
    {
        var value = DestinationBox.Text.Trim();
        var valid = value.Length == 0 || DefinitionValidation.IsSafeRelativePath(value);
        DestinationErrorText.Text = valid
            ? string.Empty
            : "Destination must be a safe relative path (no leading/trailing separators or absolute paths).";
        DestinationErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        return valid;
    }

    private void UpdateDestinationValidity()
    {
        var value = DestinationBox.Text.Trim();
        var valid = value.Length == 0 || DefinitionValidation.IsSafeRelativePath(value);
        IsPrimaryButtonEnabled = valid;
        DestinationErrorText.Text = valid
            ? string.Empty
            : "Destination must be a safe relative path (no leading/trailing separators or absolute paths).";
        DestinationErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // Back never closes the dialog.
        if (_committed.Count == 0)
            return;

        var previous = _committed[^1];
        _committed.RemoveAt(_committed.Count - 1);
        var rawIndex = _module.InstallSteps.IndexOf(previous.Step);

        // The steps before this one stay committed and their flags keep flowing into this step.
        // Any later step the user already visited keeps neither: a changed choice here can change
        // which later options are visible or usable, so its draft is dropped and it re-defaults
        // from the new flags when the user reaches it again.
        DiscardDraftsFrom(rawIndex + 1);

        ErrorText.Visibility = Visibility.Collapsed;
        Render(rawIndex, _engine.AccumulateFlags(_committed)); // This step's draft still holds the user's picks.
    }

    /// <summary>Validates the active step from its model-owned draft and records the result in <see cref="_committed"/></summary>
    private bool TryCommitCurrentStep(out string? error)
    {
        error = null;
        var step = _module.InstallSteps[_displayedStepIndex];
        var flags = _engine.AccumulateFlags(_committed);
        var draft = GetDraft(step, flags);
        var selection = new FomodStepSelection { Step = step };

        foreach (var group in step.Groups)
        {
            var chosen = draft.Groups.First(g => ReferenceEquals(g.Group, group)).SelectedPlugins;
            var validation = _engine.ValidateGroup(group, chosen, flags, _files);
            if (validation is not null)
            {
                error = validation;
                return false;
            }

            // Copy: later edits to the draft must not mutate an already-committed selection.
            selection.Groups.Add(new FomodGroupSelection { Group = group, SelectedPlugins = new List<FomodPlugin>(chosen) });
        }

        _committed.Add(selection);
        return true;
    }

    /// <summary>
    /// Returns (recreating on first use) the model-owned draft for <paramref name="step"/>.
    /// Re-entering a step via Back reuses its draft so the user's previous picks are restored;
    /// moving forward discards later steps' drafts first (<see cref="DiscardDraftsFrom"/>) so
    /// any step whose earlier dependencies changed re-defaults from the fresh flags.
    /// </summary>
    private FomodStepDraft GetDraft(FomodInstallStep step, IReadOnlyDictionary<string, string> flags)
    {
        foreach (var existing in _drafts)
        {
            if (ReferenceEquals(existing.Step, step))
                return existing;
        }

        var draft = new FomodStepDraft { Step = step };
        foreach (var group in step.Groups)
        {
            var initial = group.Plugins
                .Where(plugin => _engine.IsPluginVisible(plugin, flags, _files))
                .Where(plugin => plugin.DefaultSelected)
                .Where(plugin => _engine.ResolvePluginType(plugin, flags, _files) != FomodPluginType.NotUsable)
                .ToList();
            if (group.Type == FomodGroupType.SelectAll)
            {
                // "Select all" groups are not user-editable; they always install every visible plugin.
                initial = group.Plugins
                    .Where(plugin => _engine.IsPluginVisible(plugin, flags, _files))
                    .ToList();
            }
            else if (group.Type == FomodGroupType.SelectExactlyOne && initial.Count == 0)
            {
                // "Select exactly one" must never render with nothing picked: default to the
                // first usable option when the FOMOD declares no default.
                initial = group.Plugins
                    .Where(plugin => _engine.IsPluginVisible(plugin, flags, _files))
                    .FirstOrDefault(plugin => _engine.ResolvePluginType(plugin, flags, _files) != FomodPluginType.NotUsable) is { } usable
                        ? new List<FomodPlugin> { usable }
                        : new List<FomodPlugin>();
            }

            draft.Groups.Add(new FomodGroupDraft
            {
                Group = group,
                SelectedPlugins = initial
            });
        }

        _drafts.Add(draft);
        return draft;
    }

    /// <summary>
    /// Rebuilds <see cref="EditorHost"/> for <paramref name="step"/> against <paramref name="flags"/>.
    /// Each group becomes a titled card with a cardinality badge; each option is a row (thumbnail
    /// + name/description + a radio or checkbox) that writes straight into <paramref name="draft"/>.
    /// Options are filtered by their <c>visible</c> gate and typed against the current flags:
    /// <c>NotUsable</c>/<c>CouldBeUsable</c> options render disabled (but readable) and cannot be
    /// picked; the resolved type is shown in the fixed badge column.
    /// </summary>
    private void BuildEditor(FomodInstallStep step, FomodStepDraft draft, IReadOnlyDictionary<string, string> flags)
    {
        EditorHost.Children.Clear();

        foreach (var group in step.Groups)
        {
            var groupDraft = draft.Groups.First(g => ReferenceEquals(g.Group, group));
            var isRadioGroup = group.Type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtMostOne;
            var radioGroupName = $"fomod-editor-{_editorSeed++}";

            // Group header: name plus a small badge making the cardinality rule explicit.
            var headerRow = new Grid { ColumnSpacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 2) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBlock = new TextBlock
            {
                Text = group.Name,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            headerRow.Children.Add(nameBlock);
            Grid.SetColumn(nameBlock, 0);
            headerRow.Children.Add(BuildGroupBadge(group.Type));

            // Options that fail their <visible> gate are absent from the page entirely, and a
            // stale draft can never select a currently-hidden option (defense in depth).
            var visiblePlugins = group.Plugins
                .Where(plugin => _engine.IsPluginVisible(plugin, flags, _files))
                .ToList();
            groupDraft.SelectedPlugins.RemoveAll(plugin => !visiblePlugins.Contains(plugin));

            if (isRadioGroup)
            {
                if (group.Type == FomodGroupType.SelectAtMostOne)
                {
                    var none = new RadioButton
                    {
                        Content = "(none)",
                        GroupName = radioGroupName,
                        IsChecked = groupDraft.SelectedPlugins.Count == 0
                    };
                    none.Checked += (_, _) => groupDraft.SelectedPlugins.Clear();
                    EditorHost.Children.Add(none);
                }

                foreach (var plugin in visiblePlugins)
                {
                    var resolvedType = _engine.ResolvePluginType(plugin, flags, _files);
                    var radio = new RadioButton
                    {
                        Content = BuildOptionRow(plugin, resolvedType),
                        GroupName = radioGroupName,
                        IsChecked = groupDraft.SelectedPlugins.Contains(plugin),
                        // NotUsable (and CouldBeUsable) options are unselectable: picking them
                        // is exactly what their type descriptor warns about.
                        IsEnabled = resolvedType is not FomodPluginType.NotUsable and not FomodPluginType.CouldBeUsable
                    };
                    var captured = plugin;
                    radio.Checked += (_, _) =>
                    {
                        groupDraft.SelectedPlugins.Clear();
                        groupDraft.SelectedPlugins.Add(captured);
                    };
                    EditorHost.Children.Add(radio);
                }
            }
            else
            {
                var isAll = group.Type == FomodGroupType.SelectAll;
                foreach (var plugin in visiblePlugins)
                {
                    var resolvedType = _engine.ResolvePluginType(plugin, flags, _files);
                    var unusable = resolvedType is FomodPluginType.NotUsable or FomodPluginType.CouldBeUsable;
                    var check = new CheckBox
                    {
                        Content = BuildOptionRow(plugin, resolvedType, isAll),
                        IsChecked = isAll || groupDraft.SelectedPlugins.Contains(plugin),
                        // "All selected" rows are fixed, not user-editable. We keep IsEnabled true
                        // so the theme's disabled grey-out does NOT wash out the thumbnail and
                        // text (which made them hard to read); IsHitTestVisible=false just makes
                        // the box unclickable while it stays fully opaque and clearly checked.
                        // Unusable options are genuinely disabled: the badge chip explains why.
                        IsHitTestVisible = !isAll,
                        IsEnabled = !unusable
                    };

                    if (!isAll && !unusable)
                    {
                        var captured = plugin;
                        check.Checked += (_, _) =>
                        {
                            if (!groupDraft.SelectedPlugins.Contains(captured))
                                groupDraft.SelectedPlugins.Add(captured);
                        };
                        check.Unchecked += (_, _) => groupDraft.SelectedPlugins.Remove(captured);
                    }

                    EditorHost.Children.Add(check);
                }
            }

            // Visual divider between groups.
            EditorHost.Children.Add(new Border
            {
                Height = 1,
                // Theme resources are brushes (SolidColorBrush), not Colors.
                Background = ThemeBrush("CardStrokeColorDefaultBrush"),
                Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 8)
            });
        }
    }

    /// <summary>The group's selection rule, rendered as a consistent right-aligned chip in the group header.</summary>
    private static UIElement BuildGroupBadge(FomodGroupType groupType)
    {
        var text = groupType switch
        {
            FomodGroupType.SelectAny => "Pick any",
            FomodGroupType.SelectAtLeastOne => "Pick one or more",
            FomodGroupType.SelectAtMostOne => "Pick at most one",
            FomodGroupType.SelectExactlyOne => "Pick exactly one",
            FomodGroupType.SelectAll => "All selected",
            _ => string.Empty
        };
        var badge = BuildChip(text);
        Grid.SetColumn(badge, 1);
        return badge;
    }

    /// <summary>
    /// A small rounded chip for badges and type tags. Every badge in the wizard uses this one
    /// style so the eye reads them as one consistent visual language instead of scattered
    /// plain text.
    /// </summary>
    private static Border BuildChip(string text, string? glyph = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        UIElement content = label;
        if (glyph is not null)
        {
            // A leading glyph makes the chip read as a status marker rather than a plain
            // author tag (same FontIcon convention as the rest of the app).
            var pair = new StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            };
            pair.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
            pair.Children.Add(label);
            content = pair;
        }
        return new Border
        {
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(10),
            Padding = new Microsoft.UI.Xaml.Thickness(8, 2, 8, 2),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
            Child = content
        };
    }

    /// <summary>
    /// The per-option row: a 64px thumbnail (async-decoded from the archive when present), the
    /// plugin name and optional description, and — when set — the resolved type tag. The type
    /// tag lives in a fixed right-aligned badge slot so tags line up down the column rather
    /// than scattering after each name. Optional (the implicit type) shows no chip, which keeps
    /// the page uncluttered.
    /// </summary>
    private UIElement BuildOptionRow(FomodPlugin plugin, FomodPluginType resolvedType, bool isAll = false)
    {
        var row = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The badge column is always present so the name column stays aligned across rows even
        // when some options carry no tag.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Fixed 64x64 frame with Uniform stretch: every option image occupies the identical
        // on-screen footprint and is never distorted (odd aspect ratios letterbox inside the
        // frame). The service decodes at 128px (2x) for high-DPI crispness.
        var thumb = new Image
        {
            Width = 64,
            Height = 64,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        row.Children.Add(thumb);
        _ = LoadPluginThumbnailAsync(thumb, plugin);

        // The name is single-line (ellipsized) so a dense step can never force horizontal
        // overflow; the description below it may wrap.
        var nameText = new TextBlock
        {
            Text = plugin.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        text.Children.Add(nameText);
        TextBlock? descriptionText = null;
        if (!string.IsNullOrWhiteSpace(plugin.Description))
        {
            descriptionText = new TextBlock
            {
                Text = plugin.Description,
                // Readable secondary text: 0.85 keeps it clearly distinct from the name without
                // dropping to the low-contrast grey that is hard to read.
                Opacity = 0.85,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            text.Children.Add(descriptionText);
        }
        row.Children.Add(text);
        Grid.SetColumn(text, 1);

        // Right-aligned badge slot: the resolved type tag, in the same chip style as the group
        // header badge so both read as one consistent set of tags. Optional is the implicit
        // type and gets no chip — showing a tag on every row would scatter the page.
        var badgeSlot = new StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        var isDisabled = resolvedType is FomodPluginType.NotUsable or FomodPluginType.CouldBeUsable;
        var chip = resolvedType != FomodPluginType.Optional
            ? BuildChip(TypeTagText(resolvedType), glyph: isDisabled ? "\uE7BA" : null)
            : null;
        if (chip is not null)
            badgeSlot.Children.Add(chip);

        // "All selected" rows are locked, not merely pre-checked: a lock chip makes the
        // unselectable state visible (IsHitTestVisible=false alone has no visual cue). The
        // thumbnail stays full-opacity - the selection IS active, it just can't be changed.
        if (isAll)
            badgeSlot.Children.Add(BuildChip("Always installed", glyph: "\uE72E"));
        row.Children.Add(badgeSlot);
        Grid.SetColumn(badgeSlot, 2);

        if (isDisabled)
        {
            // The disabled state would dim the text out, so restore its fill first; the muted
            // 0.72 below then does the "locked" signalling. The rest of the not-selectable
            // signal comes from non-text parts: a dimmed thumbnail reads at a glance down a
            // dense list, the chip's warning glyph flags the reason, and a hover tooltip
            // explains the FOMOD's description in context.
            thumb.Opacity = 0.5;
            if (!string.IsNullOrWhiteSpace(plugin.Description))
                ToolTipService.SetToolTip(row, plugin.Description);

            nameText.Foreground = ThemeBrush("TextFillColorPrimaryBrush")!;
            if (descriptionText is not null)
                descriptionText.Foreground = ThemeBrush("TextFillColorSecondaryBrush")!;
        }

        if (isDisabled || isAll)
        {
            // Muted text is the shared "locked" cue for both unusable and always-installed
            // rows: 0.6 is clearly dimmer than the full-color selectable rows yet still
            // comfortably readable (well above the washed-out disabled grey we started from).
            // Thumbnails keep their own per-class cues (0.5 unusable / full always-installed),
            // so the two row classes stay visually distinct.
            nameText.Opacity = 0.6;
            if (descriptionText is not null)
                descriptionText.Opacity = 0.6;

            foreach (var child in badgeSlot.Children)
            {
                if (child is not Border chipBorder)
                    continue;
                if (chipBorder.Child is TextBlock label)
                    label.Opacity = 0.6;
                else if (chipBorder.Child is StackPanel pair)
                {
                    // Glyph+label chip: dim the label (and the glyph reads with it).
                    foreach (var part in pair.Children)
                        if (part is TextBlock pt)
                            pt.Opacity = 0.6;
                }
            }
        }

        return row;
    }

    /// <summary>User-facing wording for a resolved plugin type, matching FOMOD semantics.</summary>
    private static string TypeTagText(FomodPluginType type) => type switch
    {
        FomodPluginType.Required => "Required",
        FomodPluginType.Recommended => "Recommended",
        FomodPluginType.NotUsable => "Not usable",
        FomodPluginType.CouldBeUsable => "May conflict",
        _ => "Optional"
    };

    private async Task LoadPluginThumbnailAsync(Image target, FomodPlugin plugin)
    {
        try
        {
            // Fast path: the background preload may already hold the decoded image — wrap it
            // straight away on this UI thread, no await.
            if (TryGetPreloaded(plugin.ImagePath, out var preloaded))
            {
                target.Source = FomodImageService.CreateBitmapSource(preloaded);
                return;
            }

            // Decode off-thread; the awaited continuation returns to the UI thread, where the
            // WriteableBitmap must be created.
            var data = await AppServices.FomodImageService.LoadOptionThumbnailAsync(_archivePath, plugin);
            if (data is not null)
                target.Source = FomodImageService.CreateBitmapSource(data);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed while decoding; the dialog is being torn down.
        }
        catch (Exception)
        {
            // Undecodable image: leave the row without a thumbnail.
        }
    }

    private static Brush? ThemeBrush(string resourceKey) =>
        Application.Current.Resources.TryGetValue(resourceKey, out var value) ? value as Brush : null;

    /// <summary>Walks forward from <paramref name="fromIndex"/>, skipping steps whose visibility fails, to the summary if none remain.</summary>
    private void AdvanceTo(int fromIndex)
    {
        var flags = _engine.AccumulateFlags(_committed);
        DiscardDraftsFrom(fromIndex);
        for (var index = fromIndex; index < _module.InstallSteps.Count; index++)
        {
            var step = _module.InstallSteps[index];
            if (_engine.IsStepVisible(step, flags, _files))
            {
                Render(index, flags);
                return;
            }
        }

        Render(-1, flags);
    }

    /// <summary>Drops model-owned drafts for steps at or after <paramref name="rawIndex"/> so they re-default from current flags.</summary>
    private void DiscardDraftsFrom(int rawIndex)
    {
        for (var index = _module.InstallSteps.Count - 1; index >= rawIndex && index >= 0; index--)
        {
            var step = _module.InstallSteps[index];
            _drafts.RemoveAll(d => ReferenceEquals(d.Step, step));
        }
    }

    /// <summary>Shows <paramref name="rawIndex"/> (or the summary when -1), updates the step indicator, and rebuilds the body.</summary>
    private void Render(int rawIndex, IReadOnlyDictionary<string, string> flags)
    {
        _displayedStepIndex = rawIndex;
        IsSecondaryButtonEnabled = _committed.Count > 0;

        if (rawIndex == -1)
        {
            PrimaryButtonText = "Install";
            StepHeader.Text = "Ready to install";
            StepCounter.Text = string.Empty;
            StepProgress.Value = 100;
            EditorHost.Children.Clear();
            EditorHost.Children.Add(BuildSummaryRecap());
            DestinationSection.Visibility = Visibility.Visible;
            return;
        }

        var step = _module.InstallSteps[rawIndex];
        PrimaryButtonText = "Next";
        StepHeader.Text = step.Name;
        StepCounter.Text = $"Step {rawIndex + 1} of {_module.InstallSteps.Count}";
        StepProgress.Value = 100.0 * rawIndex / _module.InstallSteps.Count;
        DestinationSection.Visibility = Visibility.Collapsed;
        BuildEditor(step, GetDraft(step, flags), flags);
    }

    /// <summary>
    /// The summary step's recap: a read-only list of every committed step with its selections and
    /// the total number of files/folders the install will place. Replaces the old overview list.
    /// </summary>
    private UIElement BuildSummaryRecap()
    {
        var recap = new StackPanel { Spacing = 6, Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 4) };
        recap.Children.Add(new TextBlock { Text = "Your selections", FontWeight = FontWeights.SemiBold });

        foreach (var committed in _committed)
        {
            var index = _module.InstallSteps.IndexOf(committed.Step);
            recap.Children.Add(new TextBlock
            {
                Text = $"Step {index + 1}: {committed.Step.Name}",
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.85,
                FontSize = 12,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0)
            });
            recap.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", committed.Groups.Select(g => $"{g.Group.Name}: {FormatPlugins(g.SelectedPlugins)}")),
                Opacity = 0.92,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var fileCount = ResolvedFiles.Count;
        recap.Children.Add(new TextBlock
        {
            Text = fileCount == 1 ? "1 file/folder will be installed." : $"{fileCount} files/folders will be installed.",
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap
        });

        return recap;
    }

    private static string FormatPlugins(IReadOnlyList<FomodPlugin> plugins) =>
        plugins.Count == 0 ? "(none)" : string.Join(", ", plugins.Select(p => p.Name));
}
