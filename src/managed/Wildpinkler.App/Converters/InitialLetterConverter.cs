using System;
using Microsoft.UI.Xaml.Data;

namespace Wildpinkler.App.Converters;

// Presentation-only: derives a row avatar's single letter from its display name.
public sealed class InitialLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string { Length: > 0 } text ? text[..1].ToUpperInvariant() : "?";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
