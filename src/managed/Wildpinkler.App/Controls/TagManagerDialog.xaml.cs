using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// The global "manage tags" dialog: add, remove, or reset the mod-tag set offered everywhere.
/// Backed by <see cref="TagCatalog"/> so the pure list operations are testable; this control only
/// collects the result. The caller persists it (typically to <see cref="AppSettings.CustomTags"/>).
/// </summary>
public sealed partial class TagManagerDialog : ContentDialog
{
    /// <param name="currentTags">
    /// The current tag list (null/empty seeds the defaults). Not mutated; the result is in
    /// <see cref="Tags"/>.
    /// </param>
    public TagManagerDialog(IReadOnlyList<string>? currentTags)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        TagsInput.SetTags(TagCatalog.Ensure(currentTags is { } c ? c.ToList() : null));
    }

    /// <summary>The tag list after the user's edits (a fresh copy each access).</summary>
    public IReadOnlyList<string> Tags => new List<string>(TagsInput.Tags);

    private void TagsInput_TagsChanged(object sender, EventArgs e)
        => IsPrimaryButtonEnabled = TagsInput.Tags.Count > 0;

    private void ResetButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TagsInput.SetTags(TagCatalog.Reset(new List<string>(TagsInput.Tags)));
    }
}
