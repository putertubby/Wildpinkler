using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Models;

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

/// <summary>Dims rows the user set aside; other rows are fully opaque.</summary>
public sealed class RowStatusToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string? language)
        => value is PlanRowState.SetAside ? 0.55 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string? language)
        => throw new NotSupportedException();
}

/// <summary>True when a row is not set aside (drives enable/disable of row chrome).</summary>
public sealed class NotExcludedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string? language)
        => value is not PlanRowState.SetAside;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string? language)
        => throw new NotSupportedException();
}

/// <summary>Maps a derived <see cref="PlanRowState"/> to the theme brush that conveys its urgency.</summary>
public sealed class PlanStatusToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string? language)
    {
        if (value is not PlanRowState state)
            return null;
        // The brushes are defined in App.xaml (theme-aware) because the WPF
        // SystemControl*TextForegroundBrush keys do not exist in WinUI 3.
        string key = state switch
        {
            PlanRowState.Attention => "PlanStatusErrorBrush",
            PlanRowState.ToDownload => "PlanStatusAttentionBrush",
            PlanRowState.Ready or PlanRowState.Installed => "PlanStatusReadyBrush",
            _ => "PlanStatusNeutralBrush"
        };
        if (Application.Current is not { } app)
            return null;
        return app.Resources[key] as Brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, string? language)
        => throw new NotSupportedException();
}

/// <summary>Maps a selection flag to the row highlight brush (or transparent when not selected).</summary>
public sealed class BoolToHighlightBrushConverter : IValueConverter
{
    // The highlight brush is defined per theme in App.xaml. The ResourceDictionary indexer
    // throws a COMException for missing keys, so the lookup must never assume the key exists.
    public object? Convert(object? value, Type targetType, object? parameter, string? language)
    {
        if (value is true && Application.Current is { } app)
        {
            try
            {
                if (app.Resources["PlanRowHighlightBrush"] is Brush highlight)
                    return highlight;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Missing resource: fall through to the neutral fill below.
            }
        }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(24, 205, 205, 205));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, string? language)
        => throw new NotSupportedException();
}
