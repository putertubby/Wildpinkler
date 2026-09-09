using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Wildpinkler.App.Services;

/// <summary>Converts provider-authored text into bounded plain text before it enters an AI prompt.</summary>
public static partial class RemoteTextSanitizer
{
    private static readonly Regex TagPattern = TagRegex();
    private static readonly Regex UrlPattern = UrlRegex();
    private static readonly Regex WhitespacePattern = WhitespaceRegex();

    public static string ToPlainText(string? input, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(input) || maxCharacters <= 0)
            return string.Empty;

        var withoutTags = TagPattern.Replace(input, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags) ?? string.Empty;
        decoded = decoded.Replace('<', ' ').Replace('>', ' ');
        var withoutUrls = UrlPattern.Replace(decoded, " ");
        var normalized = WhitespacePattern.Replace(withoutUrls, " ").Trim();
        if (normalized.Length <= maxCharacters)
            return normalized;

        var limit = Math.Max(0, maxCharacters - " [truncated]".Length);
        var boundary = normalized.LastIndexOf(' ', limit);
        if (boundary < 1)
            boundary = limit;

        return normalized[..boundary].TrimEnd() + " [truncated]";
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?:(?:https?|ftp|file|data|nxm):[^\s<>]+|(?:www\.)?[^\s<>]+\.[a-zA-Z]{2,}(?:/[^\s<>]*)?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[\s\p{Cc}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
