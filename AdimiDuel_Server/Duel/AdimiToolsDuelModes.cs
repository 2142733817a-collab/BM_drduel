namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsDuelModes
{
    public const string CycleTag = "adimi_duel_mode_cycle";

    public static IReadOnlyList<int> AnvilLimits { get; } = Array.AsReadOnly(new[] { 1, 2, 3 });

    public static bool IsValidLimit(int limit)
    {
        return limit >= 1 && limit <= 3;
    }

    public static int GetNextLimit(int currentLimit)
    {
        return IsValidLimit(currentLimit) ? (currentLimit % 3) + 1 : 1;
    }

    public static int GetInteractionLimit(IEnumerable<string> tags, int currentLimit)
    {
        HashSet<string> entityTags = new(tags, StringComparer.Ordinal);
        if (!entityTags.Contains(CycleTag))
        {
            return GetAnvilLimit(entityTags);
        }

        // Cycling is a separate interaction, not an override for fixed-mode props.
        if (AnvilLimits.SelectMany(GetAnvilTags).Any(entityTags.Contains))
        {
            return 0;
        }

        return GetNextLimit(currentLimit);
    }

    public static string GetModeName(int limit)
    {
        return limit switch
        {
            1 => "1局定胜负",
            2 => "3局2胜",
            3 => "5局3胜",
            _ => "未知赛制",
        };
    }

    public static string GetAnvilTag(int limit)
    {
        return limit switch
        {
            1 => "adimi_bo1_anvil",
            2 => "adimi_bo3_anvil",
            3 => "adimi_bo5_anvil",
            _ => string.Empty,
        };
    }

    public static IEnumerable<string> GetAnvilTags(int limit)
    {
        if (IsValidLimit(limit))
        {
            yield return GetAnvilTag(limit);

            // Preserve the three original map slots, with their new best-of modes.
            yield return $"adimi_ft{(limit * 2) + 1}_anvil";
        }
    }

    // An entity must select exactly one mode; conflicting tags disable it.
    public static int GetAnvilLimit(IEnumerable<string> tags)
    {
        HashSet<string> entityTags = new(tags, StringComparer.Ordinal);
        int selectedLimit = 0;
        foreach (int limit in AnvilLimits)
        {
            if (GetAnvilTags(limit).Any(entityTags.Contains))
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

    public static int GetTeamLimit(IEnumerable<int> limits)
    {
        int selectedLimit = 0;
        foreach (int limit in limits)
        {
            if (!IsValidLimit(limit) || (selectedLimit != 0 && selectedLimit != limit))
            {
                return 1;
            }

            selectedLimit = limit;
        }

        return selectedLimit == 0 ? 1 : selectedLimit;
    }

    public static bool CanDuel(int requesterLimit, int requesteeLimit)
    {
        return IsValidLimit(requesterLimit) && requesterLimit == requesteeLimit;
    }
}
