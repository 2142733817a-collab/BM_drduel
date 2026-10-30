namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsDuelModes
{
    public static IReadOnlyList<int> AnvilLimits { get; } = Array.AsReadOnly(new[] { 3, 5, 7 });

    public static bool IsValidLimit(int limit)
    {
        return limit >= 1 && limit <= 10;
    }

    public static string GetAnvilTag(int limit)
    {
        return $"adimi_ft{limit}_anvil";
    }

    // An entity must select exactly one mode; conflicting tags disable it.
    public static int GetAnvilLimit(IEnumerable<string> tags)
    {
        int selectedLimit = 0;
        foreach (int limit in AnvilLimits)
        {
            if (tags.Contains(GetAnvilTag(limit)))
            {
                if (selectedLimit != 0)
                {
                    return 0;
                }

                selectedLimit = limit;
            }
        }

        return selectedLimit;
    }

    public static int ToggleLimit(int currentLimit, int anvilLimit)
    {
        return currentLimit == anvilLimit ? 1 : anvilLimit;
    }

    public static bool CanDuel(int requesterLimit, int requesteeLimit)
    {
        return IsValidLimit(requesterLimit) && requesterLimit == requesteeLimit;
    }
}
