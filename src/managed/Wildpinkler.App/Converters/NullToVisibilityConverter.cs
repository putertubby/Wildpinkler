using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wildpinkler.App.Converters;

/// <summary>Converts null to <see cref="Visibility.Visible"/> and non-null to <see cref="Visibility.Collapsed"/>.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string? language)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string? language)
        => throw new NotSupportedException();
}

/// <summary>Converts non-null to <see cref="Visibility.Visible"/> and null to <see cref="Visibility.Collapsed"/>.</summary>
public sealed class NonNullToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string? language)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string? language)
        => throw new NotSupportedException();
}
