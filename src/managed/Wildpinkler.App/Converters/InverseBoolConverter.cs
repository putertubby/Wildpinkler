using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wildpinkler.App.Converters;

/// <summary>
/// Inverts a boolean: true becomes false and false becomes true.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is bool b ? !b : DependencyProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
        => value is bool b ? !b : DependencyProperty.UnsetValue;
}
