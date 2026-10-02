using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
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
/// tree. <see cref="StepList"/> is a virtualized overview (one row per visible step, plus a final
/// summary row) and <see cref="EditorHost"/> renders the active step's groups as radios or
/// checkboxes that write straight into the draft.
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

    /// <summary>One overview row in <see cref="StepList"/>: the step's heading plus its live selection summary.</summary>
    private sealed class StepRow : INotifyPropertyChanged
    {
        /// <summary>Index into <see cref="FomodModule.InstallSteps"/>, or null for the summary row.</summary>
        public int? StepIndex { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }

        private string _selectionSummary = string.Empty;

        public string SelectionSummary
        {
            get => _selectionSummary;
            set
            {
                if (_selectionSummary == value)
                    return;

                _selectionSummary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly FomodModule _module;
    private readonly IFomodFileStateProvider _files;
    private readonly FomodSelectionResolver _engine = new();
    private readonly List<FomodStepSelection> _committed = new();
    private readonly List<FomodStepDraft> _drafts = new();
    private string _defaultDestination = string.Empty;
    private int _editorSeed;
    private int _displayedStepIndex = -2; // -2 = not yet started, -1 = summary, >=0 = a real step index

    public FomodInstallWizardDialog(FomodModule module, IFomodFileStateProvider fileStateProvider, string defaultDestination = "")
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _module = module;
        _files = fileStateProvider;
        _defaultDestination = defaultDestination;

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

        AdvanceTo(0);
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
        ErrorText.Visibility = Visibility.Collapsed;
        Render(rawIndex); // The step's draft still holds the user's picks, so they are restored.
    }

    /// <summary>Validates the active step from its model-owned draft and records the result in <see cref="_committed"/></summary>
    private bool TryCommitCurrentStep(out string? error)
    {
        error = null;
        var step = _module.InstallSteps[_displayedStepIndex];
        var draft = GetDraft(step);
        var selection = new FomodStepSelection { Step = step };

        foreach (var group in step.Groups)
        {
            var chosen = draft.Groups.First(g => ReferenceEquals(g.Group, group)).SelectedPlugins;
            var validation = _engine.ValidateGroup(group, chosen);
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

    /// <summary>Returns (creating on first use) the model-owned draft for <paramref name="step"/></summary>
    private FomodStepDraft GetDraft(FomodInstallStep step)
    {
        foreach (var existing in _drafts)
        {
            if (ReferenceEquals(existing.Step, step))
                return existing;
        }

        var draft = new FomodStepDraft { Step = step };
        foreach (var group in step.Groups)
        {
            draft.Groups.Add(new FomodGroupDraft
            {
                Group = group,
                // "Select all" groups are not user-editable; they always install every plugin.
                SelectedPlugins = group.Type == FomodGroupType.SelectAll ? new List<FomodPlugin>(group.Plugins) : new List<FomodPlugin>()
            });
        }

        _drafts.Add(draft);
        return draft;
    }

    /// <summary>Rebuilds <see cref="EditorHost"/> for <paramref name="step"/>; every control writes straight into <paramref name="draft"/></summary>
    private void BuildEditor(FomodInstallStep step, FomodStepDraft draft)
    {
        EditorHost.Children.Clear();

        foreach (var group in step.Groups)
        {
            var groupDraft = draft.Groups.First(g => ReferenceEquals(g.Group, group));
            EditorHost.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

            if (group.Type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtMostOne)
            {
                var groupName = $"fomod-editor-{_editorSeed++}";
                RadioButton? firstRadio = null;

                if (group.Type == FomodGroupType.SelectAtMostOne)
                {
                    var none = new RadioButton { Content = "(none)", GroupName = groupName, IsChecked = groupDraft.SelectedPlugins.Count == 0 };
                    none.Checked += (_, _) =>
                    {
                        groupDraft.SelectedPlugins.Clear();
                        UpdateActiveRowSummary(step);
                    };
                    EditorHost.Children.Add(none);
                }

                foreach (var plugin in group.Plugins)
                {
                    var radio = new RadioButton
                    {
                        Content = BuildPluginContent(plugin),
                        GroupName = groupName,
                        IsChecked = groupDraft.SelectedPlugins.Contains(plugin)
                    };
                    firstRadio ??= radio;

                    var capturedPlugin = plugin;
                    radio.Checked += (_, _) =>
                    {
                        groupDraft.SelectedPlugins.Clear();
                        groupDraft.SelectedPlugins.Add(capturedPlugin);
                        UpdateActiveRowSummary(step);
                    };
                    EditorHost.Children.Add(radio);
                }

                // "Select exactly one" must never render with nothing picked: default to the first option.
                if (group.Type == FomodGroupType.SelectExactlyOne && groupDraft.SelectedPlugins.Count == 0 && firstRadio is not null)
                    firstRadio.IsChecked = true;
            }
            else
            {
                var isAll = group.Type == FomodGroupType.SelectAll;
                foreach (var plugin in group.Plugins)
                {
                    var check = new CheckBox
                    {
                        Content = BuildPluginContent(plugin),
                        IsChecked = isAll || groupDraft.SelectedPlugins.Contains(plugin),
                        IsEnabled = !isAll
                    };

                    if (!isAll)
                    {
                        var capturedPlugin = plugin;
                        check.Checked += (_, _) =>
                        {
                            if (!groupDraft.SelectedPlugins.Contains(capturedPlugin))
                                groupDraft.SelectedPlugins.Add(capturedPlugin);
                            UpdateActiveRowSummary(step);
                        };
                        check.Unchecked += (_, _) =>
                        {
                            groupDraft.SelectedPlugins.Remove(capturedPlugin);
                            UpdateActiveRowSummary(step);
                        };
                    }

                    EditorHost.Children.Add(check);
                }
            }
        }
    }

    /// <summary>Walks forward from <paramref name="fromIndex"/>, skipping steps whose visibility fails, to the summary if none remain.</summary>
    private void AdvanceTo(int fromIndex)
    {
        var flags = _engine.AccumulateFlags(_committed);
        for (var index = fromIndex; index < _module.InstallSteps.Count; index++)
        {
            var step = _module.InstallSteps[index];
            if (_engine.IsStepVisible(step, flags, _files))
            {
                Render(index);
                return;
            }
        }

        Render(-1);
    }

    /// <summary>Shows <paramref name="rawIndex"/> (or the summary when -1) and refreshes the overview rows.</summary>
    private void Render(int rawIndex)
    {
        _displayedStepIndex = rawIndex;
        IsSecondaryButtonEnabled = _committed.Count > 0;

        if (rawIndex == -1)
        {
            PrimaryButtonText = "Install";
            StepHeader.Text = "Ready to install";
            EditorHost.Children.Clear();
            DestinationSection.Visibility = Visibility.Visible;
            RebuildRows();
            return;
        }

        var step = _module.InstallSteps[rawIndex];
        PrimaryButtonText = "Next";
        StepHeader.Text = $"Step {rawIndex + 1} of {_module.InstallSteps.Count}: {step.Name}";
        DestinationSection.Visibility = Visibility.Collapsed;
        BuildEditor(step, GetDraft(step));
        RebuildRows();
    }

    /// <summary>
    /// Rebuilds the virtualized overview: committed steps (marked done), then currently visible
    /// pending steps, then the summary row when nothing is pending.
    /// </summary>
    private void RebuildRows()
    {
        var total = _module.InstallSteps.Count;
        var flags = _engine.AccumulateFlags(_committed);
        var rows = new List<StepRow>();

        foreach (var committed in _committed)
        {
            var index = _module.InstallSteps.IndexOf(committed.Step);
            rows.Add(new StepRow
            {
                StepIndex = index,
                Title = $"Step {index + 1} of {total}: {committed.Step.Name} (done)",
                Subtitle = GroupsCountText(committed.Step.Groups.Count),
                SelectionSummary = CommittedSummary(committed)
            });
        }

        var pendingVisible = 0;
        for (var index = _committed.Count; index < total; index++)
        {
            var step = _module.InstallSteps[index];
            if (!_engine.IsStepVisible(step, flags, _files))
                continue;

            pendingVisible++;
            rows.Add(new StepRow
            {
                StepIndex = index,
                Title = $"Step {index + 1} of {total}: {step.Name}",
                Subtitle = GroupsCountText(step.Groups.Count),
                SelectionSummary = DraftSummary(step)
            });
        }

        if (pendingVisible == 0)
        {
            var fileCount = ResolvedFiles.Count;
            rows.Add(new StepRow
            {
                StepIndex = null,
                Title = "Ready to install",
                Subtitle = fileCount == 1 ? "1 file/folder will be installed." : $"{fileCount} files/folders will be installed."
            });
        }

        StepList.ItemsSource = rows;
        StepList.SelectedIndex = FindRowIndex(rows, _displayedStepIndex);
    }

    /// <summary>Index of the row matching the displayed step (or the summary row), or -1 when none.</summary>
    private static int FindRowIndex(List<StepRow> rows, int displayedStepIndex) =>
        rows.FindIndex(r => displayedStepIndex == -1 ? r.StepIndex is null : r.StepIndex == displayedStepIndex);

    /// <summary>
    /// The overview is read-only: tapping any row re-pins the selection to the active row (or the
    /// summary row), so the active step always stays highlighted.
    /// </summary>
    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepList.ItemsSource is not List<StepRow> rows)
            return;

        var index = FindRowIndex(rows, _displayedStepIndex);
        if (index < 0)
            return;

        if (StepList.SelectedIndex != index)
            StepList.SelectedIndex = index;
    }

    private static string GroupsCountText(int count) => count == 1 ? "1 group" : $"{count} groups";

    private static string CommittedSummary(FomodStepSelection committed) =>
        string.Join(" · ", committed.Groups.Select(g => $"{g.Group.Name}: {FormatPlugins(g.SelectedPlugins)}"));

    private string DraftSummary(FomodInstallStep step)
    {
        var draft = GetDraft(step);
        return string.Join(" · ", draft.Groups.Select(g => $"{g.Group.Name}: {FormatPlugins(g.SelectedPlugins)}"));
    }

    private static string FormatPlugins(IReadOnlyList<FomodPlugin> plugins) =>
        plugins.Count == 0 ? "(none)" : string.Join(", ", plugins.Select(p => p.Name));

    /// <summary>Refreshes the active step's overview row after the user edits a radio or checkbox.</summary>
    private void UpdateActiveRowSummary(FomodInstallStep step)
    {
        var index = _module.InstallSteps.IndexOf(step);
        if (StepList.ItemsSource is not List<StepRow> rows)
            return;

        var row = rows.FirstOrDefault(r => r.StepIndex == index);
        if (row is not null)
            row.SelectionSummary = DraftSummary(step);
    }

    private static object BuildPluginContent(FomodPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Description))
            return plugin.Name;

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = plugin.Name });
        stack.Children.Add(new TextBlock { Text = plugin.Description, Opacity = 0.62, TextWrapping = TextWrapping.Wrap });
        return stack;
    }
}
