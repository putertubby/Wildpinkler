using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wildpinkler.App.Services;

public static class NxmActivationResolver
{
    private const int MaximumUriLength = 8192;

    private static readonly Regex CommandLineToken = new("\"([^\"]*)\"|(\\S+)", RegexOptions.Compiled);

    // .NET's Main(string[] args) never includes the exe path, so index 0 must be scanned too;
    // an exe-path token simply fails TryCreate and is skipped, so this also covers argv-style callers.
    public static Uri? FromProcessArguments(IReadOnlyList<string> arguments) =>
        FindSingleCandidate(arguments, 0);

    // ILaunchActivatedEventArgs.Arguments is the whole quoted command line ("exe" "nxm://..."), not just the URI.
    public static Uri? FromLaunchArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? null : FindSingleCandidate(SplitCommandLine(arguments), 0);

    public static Uri? FromProtocolUri(Uri? uri) => IsValid(uri) ? uri : null;

    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        foreach (Match match in CommandLineToken.Matches(commandLine))
            tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        return tokens;
    }

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