namespace GameServer.Core.Social;

/// <summary>Text checks shared by chat and group fields. Any language is fine, but no control or invisible/bidi-override characters.</summary>
internal static class TextRules
{
    // U+200C (zero-width non-joiner) is deliberately allowed because Persian text needs it.
    public static bool IsForbidden(char ch) =>
        char.IsControl(ch)
        || ch == '​'
        || ch == '﻿'
        || ch is >= '‪' and <= '‮'
        || ch is >= '⁦' and <= '⁩';

    public static bool IsClean(string text, bool allowNewlines = false)
    {
        foreach (var ch in text)
        {
            if (allowNewlines && ch is '\n' or '\r' or '\t')
                continue;
            if (IsForbidden(ch))
                return false;
        }

        return true;
    }
}
