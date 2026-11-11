using System;

namespace AdimiDuel_Server.Duel;

// Messages are selected on the dedicated server. Native multiplayer does not
// expose each player's client language, so one deployment setting applies to
// everyone; an unset or unsupported setting preserves the existing Chinese.
internal static class AdimiToolsMessageLanguage
{
    internal const string EnvironmentVariable = "ADIMI_MESSAGE_LANGUAGE";

    // Keep a running mission in one language, including its cached notices.
    // Deployment updates take effect when the server process starts again.
    private static readonly bool EnglishEnabled = IsEnglishSetting(Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static bool IsEnglish => EnglishEnabled;

    internal static bool IsEnglishSetting(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return false;
        }

        string value = setting.Trim();
        return value.Equals("en", StringComparison.OrdinalIgnoreCase)
            || value.Equals("english", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Select(string chinese, string english) => IsEnglish ? english : chinese;

    // Native multiplayer troop names normally carry an English fallback after
    // their localization ID. Do not interpret variables or markup here: if a
    // modded or malformed name is not plainly Latin text, retain the name
    // already resolved by the server's original localization system.
    internal static string NativeName(string? rawValue, string displayed)
    {
        if (!IsEnglish || string.IsNullOrEmpty(rawValue) || !rawValue.StartsWith("{=", StringComparison.Ordinal))
        {
            return displayed;
        }

        int end = rawValue.IndexOf('}', 2);
        if (end <= 2 || end == rawValue.Length - 1)
        {
            return displayed;
        }

        for (int index = 2; index < end; index++)
        {
            char c = rawValue[index];
            bool literalId = end == 3 && c == '!';
            if (!literalId && !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return displayed;
            }
        }

        string fallback = rawValue.Substring(end + 1);
        bool hasLatinLetter = false;
        foreach (char c in fallback)
        {
            System.Globalization.UnicodeCategory category = char.GetUnicodeCategory(c);
            if (c is '{' or '}' or '<' or '>' || char.IsControl(c) || char.IsSurrogate(c)
                || category is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                return displayed;
            }

            if (!char.IsLetter(c))
            {
                continue;
            }

            bool latin = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '\u00c0' && c <= '\u024f') || (c >= '\u1e00' && c <= '\u1eff');
            if (!latin)
            {
                return displayed;
            }

            hasLatinLetter = true;
        }

        return hasLatinLetter ? fallback : displayed;
    }
}
