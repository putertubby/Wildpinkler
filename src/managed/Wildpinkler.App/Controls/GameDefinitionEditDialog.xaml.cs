using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class GameDefinitionEditDialog : ContentDialog
{
    private readonly GameDefinition _source;

    public GameDefinitionEditDialog(GameDefinition definition)
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
        SteamAppIdBox.Text = definition.SteamAppId;
        ExecutableBox.Text = definition.ExecutableRelativePath;
        DetectionMarkersInput.SetTags(definition.DetectionMarkers);
        VariablesInput.SetVariables(definition.Variables);
        MergedViewsInput.SetViews(definition.MergedViews);

        Validate();
    }

    /// <summary>The edited definition; the source is left untouched until the caller saves.</summary>
    public GameDefinition BuildResult()
    {
        var result = _source.Clone();
        result.Name = NameBox.Text.Trim();
        result.Description = DescriptionBox.Text.Trim();
        result.Author = AuthorBox.Text.Trim();
        result.SteamAppId = SteamAppIdBox.Text.Trim();
        result.ExecutableRelativePath = ExecutableBox.Text.Trim();
        result.DetectionMarkers = DetectionMarkersInput.Tags.ToList();
        result.Variables = VariablesInput.Variables.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        result.MergedViews = MergedViewsInput.Views.ToList();
        return result;
    }

    private void Field_Changed(object sender, TextChangedEventArgs args) => Validate();

    private void DetectionMarkers_Changed(object sender, EventArgs args) => Validate();

    private void Editor_Changed(object sender, EventArgs args) => Validate();

    private void Validate()
    {
        var error = FindError();
        ErrorText.Text = error;
        ErrorText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = error.Length == 0;
    }

    private string FindError()
    {
        // Duplicates collapse into the built collections, so they have to be caught before validating the draft.
        if (VariablesInput.FindError() is { } variableError)
            return variableError;

        if (MergedViewsInput.FindError() is { } viewError)
            return viewError;

        return GameDefinitionValidator.TryValidate(BuildResult(), out var error) ? string.Empty : error;
    }
}
