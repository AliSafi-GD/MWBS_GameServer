using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>Validation and normalisation for profile fields. Throws GameException with a clear error code.</summary>
public static class ProfileValidator
{
    private static readonly Regex UsernameRx = new(@"^[a-z0-9_]{3,20}\z", RegexOptions.Compiled);
    private static readonly Regex AvatarRx = new(@"^([A-Za-z0-9_\-.]{1,64}|https://\S{1,190})\z", RegexOptions.Compiled);
    private static readonly Regex LangRx = new(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?\z", RegexOptions.Compiled);
    private static readonly Regex TimezoneRx = new(@"^(UTC|[A-Za-z_]+(/[A-Za-z0-9_+\-]+){1,2})\z", RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "root", "system", "support", "moderator", "mod", "staff", "gm"
    };

    public static ProfileUpdate Normalize(ProfileUpdate update) => new(
        update.Username is null ? null : NormalizeUsername(update.Username),
        update.DisplayName is null ? null : NormalizeDisplayName(update.DisplayName),
        update.Avatar is null ? null : NormalizeAvatar(update.Avatar),
        update.Lang is null ? null : NormalizeLang(update.Lang),
        update.Timezone is null ? null : NormalizeTimezone(update.Timezone));

    /// <summary>Usernames are case-insensitive: stored lower-case, 3-20 chars of a-z, 0-9 and underscore.</summary>
    public static string NormalizeUsername(string input)
    {
        var name = input.Trim().ToLowerInvariant();
        if (!UsernameRx.IsMatch(name))
            throw new GameException("invalid_username", "Username must be 3-20 characters: English letters, digits or underscore.");
        if (Reserved.Contains(name))
            throw new GameException("invalid_username", "This username is reserved.");
        return name;
    }

    /// <summary>Any language is fine (including Persian), but no control or invisible/bidi-override characters.</summary>
    public static string NormalizeDisplayName(string input)
    {
        var name = input.Trim();
        if (name.Length > 200 || new StringInfo(name).LengthInTextElements > 32)
            throw new GameException("invalid_display_name", "Display name can be at most 32 characters.");
        foreach (var ch in name)
        {
            if (IsForbidden(ch))
                throw new GameException("invalid_display_name", "Display name contains invalid characters.");
        }
        return name;
    }

    /// <summary>An avatar id from the game's catalog (e.g. "avatar_07") or an https URL. Empty clears it.</summary>
    public static string NormalizeAvatar(string input)
    {
        var avatar = input.Trim();
        if (avatar.Length == 0) return "";
        if (!AvatarRx.IsMatch(avatar))
            throw new GameException("invalid_avatar", "Avatar must be an id or an https URL.");
        return avatar;
    }

    public static string NormalizeLang(string input)
    {
        var lang = input.Trim();
        if (!LangRx.IsMatch(lang))
            throw new GameException("invalid_lang", "Language must look like 'en' or 'fa' or 'pt-BR'.");
        return lang;
    }

    /// <summary>IANA name like "Asia/Tehran" or "UTC". Empty clears it.</summary>
    public static string NormalizeTimezone(string input)
    {
        var tz = input.Trim();
        if (tz.Length == 0) return "";
        if (!TimezoneRx.IsMatch(tz))
            throw new GameException("invalid_timezone", "Timezone must be an IANA name like 'Asia/Tehran' or 'UTC'.");
        return tz;
    }

    // Note: U+200C (zero-width non-joiner) is deliberately allowed because Persian text needs it.
    private static bool IsForbidden(char ch) =>
        char.IsControl(ch)
        || ch is '​' or '﻿'
        || ch is >= '‪' and <= '‮'
        || ch is >= '⁦' and <= '⁩';
}
