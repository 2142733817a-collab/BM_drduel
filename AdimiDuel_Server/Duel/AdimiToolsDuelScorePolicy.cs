namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsDuelScorePolicy
{
    public const int MinimumBountyGain = 100;

    public static float CalculateWinReward(int opponentBounty, int ownTroopCost, int opponentTroopCost, int currentWins)
    {
        // Same float operations as the existing Native-derived PvP formula.
        // TaleWorlds.Library.MathF.Pow delegates to System.Math.Pow then casts.
        return Math.Max(MinimumBountyGain, opponentBounty)
            * Math.Max(1f, (float)ownTroopCost / opponentTroopCost)
            * (float)Math.Pow(MathF.E, currentWins / 10f);
    }

    public static float CalculateAiWinReward(int currentWins)
    {
        // AI clones the player's class: equal cost, no opponent bounty.
        return CalculateWinReward(0, 1, 1, currentWins);
    }

    public static float LimitWinReward(float requested, int score, int bounty, int maximumScore)
    {
        if (float.IsNaN(requested) || requested <= 0f)
        {
            return 0f;
        }

        long scoreRoom = Math.Max(0L, (long)maximumScore - score);
        long bountyRoom = Math.Max(0L, (long)maximumScore - bounty);
        long maximumIntegerGain = Math.Min(int.MaxValue, Math.Min(scoreRoom, ((bountyRoom + 1L) * 5L) - 1L));
        float maximum = maximumIntegerGain;
        if ((double)maximum > maximumIntegerGain)
        {
            maximum = MathF.BitDecrement(maximum);
        }

        return Math.Min(requested, maximum);
    }

    public static bool IsRepresentable(int value, int maximum)
    {
        // Duel representative counters start at zero and never go negative.
        return value >= 0 && value <= maximum;
    }
}
