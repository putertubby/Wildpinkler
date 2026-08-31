using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class ToolDefinitionEditDialog : ContentDialog
{
    private readonly ToolDefinition _source;

    public ToolDefinitionEditDialog(ToolDefinition definition)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _source = definition;

        DefinitionBar.Title = $"{definition.DefinitionId} (v{definition.DefinitionVersion})";
        DefinitionBar.Message = definition.IsBuiltIn
            ? "Saving stores a personal copy that overrides the built-in definition."
            : $"Editing the imported definition '{definition.DefinitionId}'.";

        NameBox.Text = definition.Name;
        DescriptionBox.Text = definition.Description;
        AuthorBox.Text = definition.Author;
        SupportedGamesInput.SetTags(definition.SupportedGameDefinitions);
        ExecutableBox.Text = definition.ExecutableRelativePath;
        WorkingDirectoryBox.Text = definition.WorkingDirectory;
        LaunchArgumentsBox.Text = definition.DefaultLaunchArguments;
        DetectionMarkersInput.SetTags(definition.DetectionMarkers);
        VariablesInput.SetVariables(definition.Variables);
        MergedViewsInput.SetViews(definition.MergedViews);
        ProducesOutputSwitch.IsOn = definition.ProducesOutput;

        Validate();
    }

    /// <summary>The edited definition; the source is left untouched until the caller saves.</summary>
    public ToolDefinition BuildResult()
    {
        var result = _source.Clone();
        result.Name = NameBox.Text.Trim();
        result.Description = DescriptionBox.Text.Trim();
        result.Author = AuthorBox.Text.Trim();
        result.SupportedGameDefinitions = SupportedGamesInput.Tags.Select(tag => tag.Trim()).ToList();
        result.ProducesOutput = ProducesOutputSwitch.IsOn;
        result.ExecutableRelativePath = ExecutableBox.Text.Trim();
        result.WorkingDirectory = WorkingDirectoryBox.Text.Trim();
        result.DefaultLaunchArguments = LaunchArgumentsBox.Text.Trim();
        result.DetectionMarkers = DetectionMarkersInput.Tags.ToList();
        result.Variables = VariablesInput.Variables.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        result.MergedViews = MergedViewsInput.Views.ToList();
        return result;
    }


    private void Validate()
    {
        var error = FindError();
        ErrorText.Text = error;
        ErrorText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = error.Length == 0;
    }

    private void Field_Changed(object sender, TextChangedEventArgs args) => Validate();

    private void Tags_Changed(object sender, EventArgs args) => Validate();

    private void Editor_Changed(object sender, EventArgs args) => Validate();

    private void ProducesOutput_Toggled(object sender, RoutedEventArgs args) => Validate();

    private string FindError()
    {
        // Duplicates collapse into the built collections, so they have to be caught before validating the draft.
        if (VariablesInput.FindError() is { } variableError)
            return variableError;

        if (MergedViewsInput.FindError() is { } viewError)
            return viewError;

        return ToolDefinitionValidator.TryValidate(BuildResult(), out var error) ? string.Empty : error;
    }
}
