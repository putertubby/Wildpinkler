using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Wildpinkler.App.Controls;

/// <summary>
/// The header block of a collection card: search, an optional filter slot, an optional active-filter
/// chip slot, and the result/selected counts. It lives inside the card so it disappears together with
/// the collection it acts on when a narrow layout drills into the details pane.
/// </summary>
public sealed partial class CollectionListHeader : UserControl
{
    private bool _isSyncingSearchText;

    public event EventHandler? QueryChanged;

    public CollectionListHeader() => InitializeComponent();

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(CollectionListHeader),
        new PropertyMetadata(string.Empty, OnPlaceholderTextChanged));

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(CollectionListHeader),
        new PropertyMetadata(string.Empty, OnSearchTextChanged));

    public static readonly DependencyProperty ResultCountTextProperty = DependencyProperty.Register(
        nameof(ResultCountText), typeof(string), typeof(CollectionListHeader),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedCountTextProperty = DependencyProperty.Register(
        nameof(SelectedCountText), typeof(string), typeof(CollectionListHeader),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FiltersContentProperty = DependencyProperty.Register(
        nameof(FiltersContent), typeof(object), typeof(CollectionListHeader),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ChipsContentProperty = DependencyProperty.Register(
        nameof(ChipsContent), typeof(object), typeof(CollectionListHeader),
        new PropertyMetadata(null));

    public static readonly DependencyProperty HasChipsProperty = DependencyProperty.Register(
        nameof(HasChips), typeof(bool), typeof(CollectionListHeader),
        new PropertyMetadata(false));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string ResultCountText
    {
        get => (string)GetValue(ResultCountTextProperty);
        set => SetValue(ResultCountTextProperty, value);
    }

    public string SelectedCountText
    {
        get => (string)GetValue(SelectedCountTextProperty);
        set => SetValue(SelectedCountTextProperty, value);
    }

    public object? FiltersContent
    {
        get => GetValue(FiltersContentProperty);
        set => SetValue(FiltersContentProperty, value);
    }

    public object? ChipsContent
    {
        get => GetValue(ChipsContentProperty);
        set => SetValue(ChipsContentProperty, value);
    }

    public bool HasChips
    {
        get => (bool)GetValue(HasChipsProperty);
        set => SetValue(HasChipsProperty, value);
    }

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    private static void OnPlaceholderTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var header = (CollectionListHeader)sender;
        var text = args.NewValue as string ?? string.Empty;
        header.SearchBox.PlaceholderText = text;
        AutomationProperties.SetName(header.SearchBox, text);
    }

    private static void OnSearchTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var header = (CollectionListHeader)sender;
        var text = args.NewValue as string ?? string.Empty;

        if (!header._isSyncingSearchText && header.SearchBox.Text != text)
            header.SearchBox.Text = text;

        header.ClearButton.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        header.QueryChanged?.Invoke(header, EventArgs.Empty);
    }

    // The control owns the TextBox, so SearchText is pushed here before QueryChanged is raised.
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        _isSyncingSearchText = true;
        try
        {
            SearchText = SearchBox.Text;
        }
        finally
        {
            _isSyncingSearchText = false;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || SearchBox.Text.Length == 0)
            return;

        SearchBox.Text = string.Empty;
        args.Handled = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs args)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }
}
