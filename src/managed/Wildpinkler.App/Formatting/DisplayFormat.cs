using System;
using System.Globalization;

namespace Wildpinkler.App.Formatting;

/// <summary>Shared presentation formatting so every surface renders the same value the same way.</summary>
internal static class DisplayFormat
{
    public static string ShortDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string ShortDateTime(DateTimeOffset? value, string fallback) =>
        value is null ? fallback : ShortDateTime(value.Value);

    public static string Count(int value) => value.ToString(CultureInfo.CurrentCulture);
}
