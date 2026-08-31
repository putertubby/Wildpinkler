using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wildpinkler.App.Controls;

public sealed partial class VariableRow : ObservableObject
{
    private string _name = string.Empty;
    private string _value = string.Empty;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>Name/value editor for the variables a definition contributes to a profile.</summary>
public sealed partial class VariablesEditor : UserControl
{
    private readonly ObservableCollection<VariableRow> _rows = new();

    public event EventHandler? Changed;

    public VariablesEditor()
    {
        InitializeComponent();
        VariableList.ItemsSource = _rows;
        UpdateEmptyState();
    }

    public string ValueHeader
    {
        get => ValueHeaderBlock.Text;
        set => ValueHeaderBlock.Text = value;
    }

    public string NamePlaceholderText { get; set; } = string.Empty;

    public string ValuePlaceholderText { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string> Variables =>
        _rows.ToDictionary(row => row.Name.Trim(), row => row.Value.Trim(), StringComparer.Ordinal);

    public void SetVariables(IEnumerable<KeyValuePair<string, string>> variables)
    {
        _rows.Clear();
        foreach (var variable in variables.OrderBy(item => item.Key, StringComparer.Ordinal))
            _rows.Add(new VariableRow { Name = variable.Key, Value = variable.Value });
        UpdateEmptyState();
    }

    /// <summary>
    /// Duplicates collapse silently into the resulting dictionary, so they must be caught before the
    /// draft definition is handed to a validator.
    /// </summary>
    public string? FindError()
    {
        var duplicate = _rows
            .Select(row => row.Name.Trim())
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"'{duplicate.Key}' is declared more than once.";
    }

    private void AddVariable_Click(object sender, RoutedEventArgs args)
    {
        _rows.Add(new VariableRow());
        Notify();
    }

    private void DeleteVariable_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is VariableRow row)
        {
            _rows.Remove(row);
            Notify();
        }
    }

    // Placeholders come from a control property, so they can only be applied once a row is realized.
    private void NameBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBox box)
            box.PlaceholderText = NamePlaceholderText;
    }

    private void ValueBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBox box)
            box.PlaceholderText = ValuePlaceholderText;
    }

    private void Field_Changed(object sender, TextChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    private void Notify()
    {
        UpdateEmptyState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
