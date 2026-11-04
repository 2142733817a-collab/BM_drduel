namespace AdimiDuel_Server.Duel;

internal enum AdimiToolsDuelSpawnClass
{
    Unknown,
    Infantry,
    Ranged,
    Cavalry,
}

internal static class AdimiToolsDuelSpawnPolicy
{
    public const string InfantryTag = "spawn_troop_1";
    public const string RangedTag = "spawn_troop_2";
    public const string CavalryTag = "spawn_troop_3";

    public static bool IsExclusiveMatch(AdimiToolsDuelSpawnClass spawnClass, bool hasInfantryTag, bool hasRangedTag, bool hasCavalryTag)
    {
        int troopTagCount = (hasInfantryTag ? 1 : 0) + (hasRangedTag ? 1 : 0) + (hasCavalryTag ? 1 : 0);
        if (troopTagCount != 1)
        {
            return false;
        }

        return spawnClass switch
        {
            AdimiToolsDuelSpawnClass.Infantry => hasInfantryTag,
            AdimiToolsDuelSpawnClass.Ranged => hasRangedTag,
            AdimiToolsDuelSpawnClass.Cavalry => hasCavalryTag,
            _ => false,
        };
    }
}
