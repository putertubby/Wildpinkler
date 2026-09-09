using System;
using System.Collections.Generic;

namespace Wildpinkler.App.Services;

public static class NxmActivationResolver
{
    private const int MaximumUriLength = 8192;

    public static Uri? FromProcessArguments(IReadOnlyList<string> arguments) =>
        FindSingleCandidate(arguments, 1);

    public static Uri? FromLaunchArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        var value = arguments.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];

        return TryCreate(value);
    }

    public static Uri? FromProtocolUri(Uri? uri) => IsValid(uri) ? uri : null;

    private static Uri? FindSingleCandidate(IReadOnlyList<string> arguments, int startIndex)
    {
        Uri? candidate = null;
        for (var index = startIndex; index < arguments.Count; index++)
        {
            var current = TryCreate(arguments[index]);
            if (current is null)
                continue;
            if (candidate is not null)
                return null;
            candidate = current;
        }

        return candidate;
    }

    private static Uri? TryCreate(string? value) =>
        value is { Length: > 0 and <= MaximumUriLength } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsValid(uri)
            ? uri
            : null;

    private static bool IsValid(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.OriginalString.Length <= MaximumUriLength &&
        string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase);
}