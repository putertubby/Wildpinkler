using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Drives a FOMOD's install steps one visible step at a time. One <see cref="ContentDialog"/> instance
/// is reused for every step (no nested dialogs); Next/Back never close the dialog themselves, only the
/// final "Install" click (at the summary) does, via <see cref="ContentDialogResult.Primary"/>.
/// </summary>
public sealed partial class FomodInstallWizardDialog : ContentDialog
{
    private sealed class GroupBinding
    {
        public required FomodGroup Group { get; init; }

        /// <summary>SelectExactlyOne/SelectAtMostOne: RadioButtons sharing one GroupName; Tag = FomodPlugin, or null for "(none)".</summary>
        public List<RadioButton>? Radios { get; init; }

        /// <summary>SelectAny/SelectAtLeastOne/SelectAll: one CheckBox per plugin; Tag = FomodPlugin.</summary>
        public List<CheckBox>? Checks { get; init; }
    }

    private readonly FomodModule _module;
    private readonly IFomodFileStateProvider _files;
    private readonly FomodSelectionResolver _engine = new();
    private readonly List<FomodStepSelection> _committed = new();
    private readonly List<GroupBinding> _currentBindings = new();
    private int _groupNameSeed;
    private int _displayedStepIndex = -2; // -2 = not yet started, -1 = summary, >=0 = a real step index

    public FomodInstallWizardDialog(FomodModule module, IFomodFileStateProvider fileStateProvider)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _module = module;
        _files = fileStateProvider;

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

    public string SelectionSignature => _engine.ComputeSelectionSignature(_committed);

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_displayedStepIndex == -1)
            return; // At the summary: let the dialog actually close with Result = Primary.

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

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // Back never closes the dialog.
        if (_committed.Count == 0)
            return;

        var previous = _committed[^1];
        _committed.RemoveAt(_committed.Count - 1);
        var rawIndex = _module.InstallSteps.IndexOf(previous.Step);
        ErrorText.Visibility = Visibility.Collapsed;
        RenderStep(rawIndex, previous);
    }

    private bool TryCommitCurrentStep(out string? error)
    {
        error = null;
        var step = _module.InstallSteps[_displayedStepIndex];
        var selection = new FomodStepSelection { Step = step };

        foreach (var binding in _currentBindings)
        {
            var chosen = ReadSelectedPlugins(binding);
            var validation = _engine.ValidateGroup(binding.Group, chosen);
            if (validation is not null)
            {
                error = validation;
                return false;
            }

            selection.Groups.Add(new FomodGroupSelection { Group = binding.Group, SelectedPlugins = chosen });
        }

        _committed.Add(selection);
        return true;
    }

    private static List<FomodPlugin> ReadSelectedPlugins(GroupBinding binding)
    {
        if (binding.Radios is { } radios)
        {
            var picked = radios.FirstOrDefault(radio => radio.IsChecked == true);
            return picked?.Tag is FomodPlugin plugin ? new List<FomodPlugin> { plugin } : new List<FomodPlugin>();
        }

        return binding.Checks!.Where(check => check.IsChecked == true).Select(check => (FomodPlugin)check.Tag).ToList();
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
                RenderStep(index, previous: null);
                return;
            }
        }

        RenderSummary();
    }

    private void RenderStep(int rawIndex, FomodStepSelection? previous)
    {
        _displayedStepIndex = rawIndex;
        var step = _module.InstallSteps[rawIndex];

        StepScroller.Visibility = Visibility.Visible;
        SummaryHost.Visibility = Visibility.Collapsed;
        StepTitle.Text = step.Name;
        PrimaryButtonText = "Next";
        IsSecondaryButtonEnabled = _committed.Count > 0;

        GroupsHost.Children.Clear();
        _currentBindings.Clear();

        foreach (var group in step.Groups)
        {
            var previousGroup = previous?.Groups.FirstOrDefault(g => g.Group == group);
            GroupsHost.Children.Add(BuildGroupCard(group, previousGroup));
        }
    }

    private void RenderSummary()
    {
        _displayedStepIndex = -1;
        StepTitle.Text = string.Empty;
        StepScroller.Visibility = Visibility.Collapsed;
        SummaryHost.Visibility = Visibility.Visible;
        PrimaryButtonText = "Install";
        IsSecondaryButtonEnabled = _committed.Count > 0;

        var fileCount = ResolvedFiles.Count;
        SummaryText.Text = fileCount == 1 ? "1 file/folder will be installed." : $"{fileCount} files/folders will be installed.";
    }

    private Border BuildGroupCard(FomodGroup group, FomodGroupSelection? previous)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };

        var stack = new StackPanel { Spacing = 8 };
        card.Child = stack;

        stack.Children.Add(new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Text = group.Name, TextWrapping = TextWrapping.Wrap });

        var previouslySelected = previous?.SelectedPlugins ?? new List<FomodPlugin>();
        var groupName = $"fomod-group-{_groupNameSeed++}";

        if (group.Type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtMostOne)
        {
            var radios = new List<RadioButton>();
            if (group.Type == FomodGroupType.SelectAtMostOne)
            {
                var none = new RadioButton { Content = "(none)", GroupName = groupName, Tag = null, IsChecked = previouslySelected.Count == 0 };
                radios.Add(none);
                stack.Children.Add(none);
            }

            foreach (var plugin in group.Plugins)
            {
                var radio = new RadioButton
                {
                    Content = BuildPluginContent(plugin),
                    GroupName = groupName,
                    Tag = plugin,
                    IsChecked = previouslySelected.Contains(plugin)
                };
                radios.Add(radio);
                stack.Children.Add(radio);
            }

            if (group.Type == FomodGroupType.SelectExactlyOne && radios.All(radio => radio.IsChecked != true) && radios.Count > 0)
                radios[0].IsChecked = true;

            _currentBindings.Add(new GroupBinding { Group = group, Radios = radios });
        }
        else
        {
            var isAll = group.Type == FomodGroupType.SelectAll;
            var checks = new List<CheckBox>();
            foreach (var plugin in group.Plugins)
            {
                var check = new CheckBox
                {
                    Content = BuildPluginContent(plugin),
                    Tag = plugin,
                    IsChecked = isAll || previouslySelected.Contains(plugin),
                    IsEnabled = !isAll
                };
                checks.Add(check);
                stack.Children.Add(check);
            }

            _currentBindings.Add(new GroupBinding { Group = group, Checks = checks });
        }

        return card;
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
