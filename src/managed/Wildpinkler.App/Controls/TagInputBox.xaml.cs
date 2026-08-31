using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Free-form text entry that turns into removable chips on Enter/Tab (a tokenizing text box);
/// Backspace on an empty entry or double-clicking a chip turns it back into editable text.
/// </summary>
public sealed partial class TagInputBox : UserControl
{
    private readonly List<string> _tags = new();
    private readonly TextBox _entryBox;

    public event EventHandler? TagsChanged;

    public TagInputBox()
    {
        InitializeComponent();

        _entryBox = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        _entryBox.KeyDown += EntryBox_KeyDown;
        ChipsHost.Children.Add(_entryBox);
        ContainerBorder.Tapped += Container_Tapped;
    }

    public string Header
    {
        get => HeaderBlock.Text;
        set => HeaderBlock.Text = value;
    }

    public string PlaceholderText
    {
        get => _entryBox.PlaceholderText;
        set => _entryBox.PlaceholderText = value;
    }

    public IReadOnlyList<string> Tags => _tags;

    public void SetTags(IEnumerable<string> tags)
    {
        _tags.Clear();
        _tags.AddRange(tags);
        RebuildChips();
    }

    private void Container_Tapped(object sender, TappedRoutedEventArgs args)
    {
        // Only refocus for taps that land on the container/panel itself, not on a chip or its remove button.
        if (ReferenceEquals(args.OriginalSource, ContainerBorder) || ReferenceEquals(args.OriginalSource, ChipsHost))
            _entryBox.Focus(FocusState.Pointer);
    }

    private void EntryBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter || (args.Key == VirtualKey.Tab && _entryBox.Text.Trim().Length > 0))
        {
            CommitEntry();
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Back && _entryBox.Text.Length == 0 && _tags.Count > 0)
        {
            EditTag(_tags.Count - 1);
            args.Handled = true;
        }
    }

    private void CommitEntry()
    {
        var text = _entryBox.Text.Trim();
        _entryBox.Text = string.Empty;
        if (text.Length == 0 || _tags.Any(tag => string.Equals(tag, text, StringComparison.OrdinalIgnoreCase)))
            return;

        _tags.Add(text);
        RebuildChips();
        // Tab would otherwise move focus to the next control; Enter and Tab both keep it in the entry box so typing can continue.
        _entryBox.Focus(FocusState.Keyboard);
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditTag(int index)
    {
        var text = _tags[index];
        _tags.RemoveAt(index);
        RebuildChips();

        _entryBox.Text = text;
        _entryBox.SelectionStart = text.Length;
        _entryBox.Focus(FocusState.Programmatic);
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveTag(string tag)
    {
        _tags.Remove(tag);
        RebuildChips();
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildChips()
    {
        ChipsHost.Children.Clear();
        foreach (var tag in _tags)
            ChipsHost.Children.Add(CreateChip(tag));
        ChipsHost.Children.Add(_entryBox);
    }

    private Border CreateChip(string tag)
    {
        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        removeButton.Click += (_, _) => RemoveTag(tag);
        ToolTipService.SetToolTip(removeButton, "Remove");

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(removeButton);

        var chip = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 4, 4, 4),
            Child = content
        };
        chip.DoubleTapped += (_, _) => EditTag(_tags.IndexOf(tag));
        return chip;
    }
}
