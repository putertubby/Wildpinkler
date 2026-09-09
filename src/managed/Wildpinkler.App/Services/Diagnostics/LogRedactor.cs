using System;
using System.Text.RegularExpressions;

namespace Wildpinkler.App.Services.Diagnostics;

/// <summary>
/// Strips values from log lines that should never leave the machine. Logs are attached to bug
/// reports, so the user's name and any site credential must not survive into the file.
/// </summary>
internal static partial class LogRedactor
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string UserName = Environment.UserName;

    [GeneratedRegex(@"(?i)\b(api[_-]?key|apikey|token|password|secret|authorization)\b\s*[:=]\s*""?[^\s""&,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)([?&](?:key|apikey|api_key|token|access_token)=)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretQueryParameter();

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = SecretAssignment().Replace(value, "$1=<redacted>");
        result = SecretQueryParameter().Replace(result, "$1<redacted>");

        if (UserProfile.Length > 0)
            result = result.Replace(UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (UserName.Length > 2)
            result = result.Replace(UserName, "<user>", StringComparison.OrdinalIgnoreCase);

        return result;
    }
}
