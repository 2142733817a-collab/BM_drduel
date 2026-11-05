namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsCultureSelectionTags
{
    public const string Prefix = "adimi_culture_";

    public static bool TryGetCultureId(IEnumerable<string> tags, out string cultureId)
    {
        string[] selectors = tags.Where(tag => tag.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        cultureId = string.Empty;
        if (selectors.Length != 1)
        {
            return false;
        }

        string value = selectors[0][Prefix.Length..];
        if (value.Length == 0 || value.Any(c => (c < 'a' || c > 'z') && c != '_'))
        {
            return false;
        }

        cultureId = value;
        return true;
    }
}
