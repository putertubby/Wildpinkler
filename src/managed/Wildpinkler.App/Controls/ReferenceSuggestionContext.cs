using System;
using System.Linq;

namespace Wildpinkler.App.Controls;

internal static class ReferenceSuggestionContext
{
    public static bool TryGetQuery(string text, int caretPosition, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrEmpty(text))
            return false;

        caretPosition = Math.Clamp(caretPosition, 0, text.Length);
        var at = text.LastIndexOf('@', Math.Max(0, caretPosition - 1));
        if (at < 0 || text[(at + 1)..caretPosition].Any(char.IsWhiteSpace))
            return false;

        query = text[(at + 1)..caretPosition];
        return true;
    }
}