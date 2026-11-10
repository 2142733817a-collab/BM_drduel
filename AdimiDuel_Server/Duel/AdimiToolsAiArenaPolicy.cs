using System.Globalization;

namespace AdimiDuel_Server.Duel;

internal enum AdimiToolsAiArenaKind
{
    Original,
    Spear,
    Archer,
    HorseArcher,
    Cavalry,
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "StyleCop.CSharp.MaintainabilityRules", "SA1649:File name should match first type name",
    Justification = "Scene group identity belongs to the shared arena policy types.")]
internal readonly record struct AdimiToolsAiArenaSceneGroup(AdimiToolsAiArenaKind Kind, int Number)
{
    public override string ToString() => AdimiToolsAiArenaTags.GetSceneGroupTag(this);
}

internal enum AdimiToolsAiArenaState
{
    Idle,
    CountingDown,
    Running,
}

internal enum AdimiToolsAiArenaHitResult
{
    Reserved,
    Counted,
    Debounced,
    InvalidOwner,
    InvalidTime,
    OwnedByAnother,
    AlreadyRunning,
    MaximumReached,
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "StyleCop.CSharp.MaintainabilityRules",
    "SA1649:File name should match first type name",
    Justification = "This file intentionally keeps the tightly coupled AI arena policy types together.")]
internal static class AdimiToolsAiArenaTags
{
    public const string GroupPrefix = "adimi_ai_group_";
    public const string SpearGroupPrefix = "adimi_ai_spear_group_";
    public const string ArcherGroupPrefix = "adimi_ai_archer_group_";
    public const string HorseArcherGroupPrefix = "adimi_ai_horse_archer_group_";
    public const string CavalryGroupPrefix = "adimi_ai_cavalry_group_";
    public const string TriggerRoleTag = "adimi_ai_trigger";
    public const string EnhancedTriggerRoleTag = "adimi_ai_trigger_enhanced";
    public const string SimpleTriggerRoleTag = "adimi_ai_trigger_1";
    public const string NormalTriggerRoleTag = "adimi_ai_trigger_2";
    public const string HardTriggerRoleTag = "adimi_ai_trigger_3";
    public const string SpearTriggerRoleTag = "adimi_ai_trigger_spear";
    public const string ArcherTriggerRoleTag = "adimi_ai_trigger_archer";
    public const string HardArcherTriggerRoleTag = "adimi_ai_trigger_archer_hard";
    public const string HorseArcherTriggerRoleTag = "adimi_ai_trigger_horse_archer";
    public const string HardCavalryTriggerRoleTag = "adimi_ai_trigger_cavalry_hard";
    public const string SpawnRoleTag = "adimi_ai_spawn";
    public const string SpearSpawnRoleTag = "adimi_ai_spear_spawn";
    public const string ArcherSpawnRoleTag = "adimi_ai_archer_spawn";
    public const string HorseArcherSpawnRoleTag = "adimi_ai_horse_archer_spawn";
    public const string CavalrySpawnRoleTag = "adimi_ai_cavalry_spawn";
    public const string BarrierRoleTag = "adimi_ai_barrier";
    public const string LadderRoleTag = "adimi_ai_ladder";
    public const string AreaRoleTag = "adimi_ai_area";

    public static bool IsValidRoleCounts(int triggerCount, int spawnCount, int barrierCount, int areaCount, int enhancedTriggerCount = 0, int hardTriggerCount = 0, int ladderCount = 0, int spearTriggerCount = 0)
    {
        if (spearTriggerCount != 0)
        {
            return spearTriggerCount == 1 && spawnCount == 1
                && triggerCount == 0 && enhancedTriggerCount == 0 && hardTriggerCount == 0
                && barrierCount == 0 && ladderCount == 0 && areaCount == 0;
        }

        return triggerCount is 0 or 1
            && enhancedTriggerCount is 0 or 1
            && hardTriggerCount is 0 or 1
            && spearTriggerCount is 0 or 1
            && triggerCount + enhancedTriggerCount + hardTriggerCount + spearTriggerCount > 0
            && spawnCount == 1
            && barrierCount >= 0
            && ladderCount >= 0
            && areaCount is 0 or 1;
    }

    public static bool IsTriggerTag(string tag)
    {
        return tag == TriggerRoleTag || tag.StartsWith(TriggerRoleTag + "_", StringComparison.Ordinal);
    }

    public static bool TryGetTriggerDifficulty(IEnumerable<string> tags, out int difficulty)
        => TryGetTriggerSelection(tags, out difficulty, out _);

    public static bool TryGetTriggerSelection(IEnumerable<string> tags, out int difficulty, out bool spearInfantry)
    {
        bool valid = TryGetTriggerKind(tags, out difficulty, out AdimiToolsAiArenaKind kind);
        spearInfantry = kind == AdimiToolsAiArenaKind.Spear;
        return valid;
    }

    public static bool TryGetTriggerKind(IEnumerable<string> tags, out int difficulty, out AdimiToolsAiArenaKind kind)
    {
        difficulty = 0;
        kind = AdimiToolsAiArenaKind.Original;
        string[] tagArray = tags.ToArray();
        string[] specialSelectors = tagArray.Where(tag => tag is SpearTriggerRoleTag or ArcherTriggerRoleTag
            or HardArcherTriggerRoleTag or HorseArcherTriggerRoleTag or HardCavalryTriggerRoleTag)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (specialSelectors.Length > 1)
        {
            return false;
        }

        if (specialSelectors.Length == 1)
        {
            kind = specialSelectors[0] == SpearTriggerRoleTag ? AdimiToolsAiArenaKind.Spear
                : specialSelectors[0] is ArcherTriggerRoleTag or HardArcherTriggerRoleTag ? AdimiToolsAiArenaKind.Archer
                : specialSelectors[0] == HardCavalryTriggerRoleTag ? AdimiToolsAiArenaKind.Cavalry : AdimiToolsAiArenaKind.HorseArcher;
        }

        string[] selectors = tagArray.Where(tag => tag == SimpleTriggerRoleTag || tag == NormalTriggerRoleTag || tag == HardTriggerRoleTag)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (selectors.Length > 1 || (kind != AdimiToolsAiArenaKind.Original && (selectors.Length != 0 || tagArray.Contains(EnhancedTriggerRoleTag)))
            || tagArray.Any(tag => IsTriggerTag(tag) && tag != TriggerRoleTag && tag != SpearTriggerRoleTag
            && tag != ArcherTriggerRoleTag && tag != HardArcherTriggerRoleTag && tag != HorseArcherTriggerRoleTag
            && tag != HardCavalryTriggerRoleTag
            && tag != EnhancedTriggerRoleTag && tag != SimpleTriggerRoleTag && tag != NormalTriggerRoleTag && tag != HardTriggerRoleTag))
        {
            return false;
        }

        if (kind != AdimiToolsAiArenaKind.Original)
        {
            difficulty = specialSelectors[0] is HardArcherTriggerRoleTag or HardCavalryTriggerRoleTag
                ? AdimiToolsAiArenaDifficulty.Hard : AdimiToolsAiArenaDifficulty.Simple;
            return true;
        }

        if (selectors.Length == 1)
        {
            // A numbered selector overrides the old enhanced alias, so copying
            // an existing statue only requires replacing its numbered tag.
            difficulty = selectors[0] == SimpleTriggerRoleTag ? 1 : selectors[0] == NormalTriggerRoleTag ? 2 : 3;
            return true;
        }

        difficulty = tagArray.Contains(EnhancedTriggerRoleTag) ? 2 : tagArray.Contains(TriggerRoleTag) ? 1 : 0;
        return difficulty != 0;
    }

    public static bool TryParseGroupTag(string? tag, out int groupId)
        => TryParseTaggedGroup(tag, GroupPrefix, out groupId);

    public static bool TryParseSpearGroupTag(string? tag, out int groupId)
        => TryParseTaggedGroup(tag, SpearGroupPrefix, out groupId);

    // Positive keys belong to ordinary arenas; negative keys belong to the
    // independent spear namespace. Matching scene numbers never share state.
    public static bool TryParseArenaGroupTag(string? tag, out int arenaId)
    {
        if (TryParseGroupTag(tag, out arenaId))
        {
            return true;
        }

        if (TryParseSpearGroupTag(tag, out int spearId))
        {
            arenaId = -spearId;
            return true;
        }

        arenaId = 0;
        return false;
    }

    public static bool IsArenaGroupTag(string tag)
        => tag.StartsWith(GroupPrefix, StringComparison.Ordinal)
            || tag.StartsWith(SpearGroupPrefix, StringComparison.Ordinal)
            || tag.StartsWith(ArcherGroupPrefix, StringComparison.Ordinal)
            || tag.StartsWith(HorseArcherGroupPrefix, StringComparison.Ordinal)
            || tag.StartsWith(CavalryGroupPrefix, StringComparison.Ordinal);

    public static bool TryParseSceneGroupTag(string? tag, out AdimiToolsAiArenaSceneGroup group)
    {
        foreach (AdimiToolsAiArenaKind kind in Enum.GetValues<AdimiToolsAiArenaKind>())
        {
            if (TryParseTaggedGroup(tag, GetGroupPrefix(kind), out int number))
            {
                group = new(kind, number);
                return true;
            }
        }

        group = default;
        return false;
    }

    public static string GetSceneGroupTag(AdimiToolsAiArenaSceneGroup group)
        => FormatGroupTag(GetGroupPrefix(group.Kind), group.Number);

    public static string GetSpawnRole(AdimiToolsAiArenaKind kind) => kind switch
    {
        AdimiToolsAiArenaKind.Original => SpawnRoleTag,
        AdimiToolsAiArenaKind.Spear => SpearSpawnRoleTag,
        AdimiToolsAiArenaKind.Archer => ArcherSpawnRoleTag,
        AdimiToolsAiArenaKind.HorseArcher => HorseArcherSpawnRoleTag,
        AdimiToolsAiArenaKind.Cavalry => CavalrySpawnRoleTag,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static AdimiToolsAiArenaKind GetRoleKind(IEnumerable<string> tags)
    {
        if (tags.Contains(SpearTriggerRoleTag) || tags.Contains(SpearSpawnRoleTag))
        {
            return AdimiToolsAiArenaKind.Spear;
        }

        if (tags.Contains(ArcherTriggerRoleTag) || tags.Contains(HardArcherTriggerRoleTag) || tags.Contains(ArcherSpawnRoleTag))
        {
            return AdimiToolsAiArenaKind.Archer;
        }

        if (tags.Contains(HorseArcherTriggerRoleTag) || tags.Contains(HorseArcherSpawnRoleTag))
        {
            return AdimiToolsAiArenaKind.HorseArcher;
        }

        if (tags.Contains(HardCavalryTriggerRoleTag) || tags.Contains(CavalrySpawnRoleTag))
        {
            return AdimiToolsAiArenaKind.Cavalry;
        }

        return AdimiToolsAiArenaKind.Original;
    }

    public static string GetGroupTag(int groupId)
        => FormatGroupTag(GroupPrefix, groupId);

    public static string GetSpearGroupTag(int groupId)
        => FormatGroupTag(SpearGroupPrefix, groupId);

    public static string GetArenaGroupTag(int arenaId)
        => arenaId < 0 && arenaId != int.MinValue ? GetSpearGroupTag(-arenaId) : GetGroupTag(arenaId);

    private static string GetGroupPrefix(AdimiToolsAiArenaKind kind) => kind switch
    {
        AdimiToolsAiArenaKind.Original => GroupPrefix,
        AdimiToolsAiArenaKind.Spear => SpearGroupPrefix,
        AdimiToolsAiArenaKind.Archer => ArcherGroupPrefix,
        AdimiToolsAiArenaKind.HorseArcher => HorseArcherGroupPrefix,
        AdimiToolsAiArenaKind.Cavalry => CavalryGroupPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryParseTaggedGroup(string? tag, string prefix, out int groupId)
    {
        groupId = 0;
        if (tag == null || !tag.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = tag.AsSpan(prefix.Length);
        if (suffix.IsEmpty || suffix[0] < '1' || suffix[0] > '9')
        {
            return false;
        }

        for (int i = 1; i < suffix.Length; i++)
        {
            if (suffix[i] < '0' || suffix[i] > '9')
            {
                return false;
            }
        }

        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out groupId)
            && groupId > 0;
    }

    private static string FormatGroupTag(string prefix, int groupId)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        return prefix + groupId.ToString(CultureInfo.InvariantCulture);
    }
}

internal static class AdimiToolsAiArenaDifficulty
{
    public const int Simple = 1;
    public const int Normal = 2;
    public const int Hard = 3;
    public const int EnhancedSkillBonus = 50;
    public const int HardSkillValue = 330;
    public const float EnhancedHealthMultiplier = 2f;

    public static string GetName(int difficulty)
    {
        return difficulty switch { Simple => "简单", Normal => "一般", Hard => "困难", _ => "未知" };
    }
}

internal static class AdimiToolsDuelInterference
{
    public const float WarningDistance = 3f;
    public const float EvictionDistance = 1f;
    public const float WarningIntervalSeconds = 5f;
}

internal sealed class AdimiToolsAiArenaSeries
{
    public AdimiToolsAiArenaSeries(int aiCount, int selectedLimit)
    {
        Reset(aiCount, selectedLimit);
    }

    public int FirstToLimit { get; private set; }

    public int PlayerWins { get; private set; }

    public int AiWins { get; private set; }

    public bool IsComplete { get; private set; }

    public bool? WinnerPlayer { get; private set; }

    public static int ResolveLimit(int aiCount, int selectedLimit)
    {
        return aiCount == 1 && selectedLimit is >= 1 and <= 10
            ? selectedLimit
            : 1;
    }

    public bool RecordRound(bool playerWon)
    {
        if (IsComplete)
        {
            return true;
        }

        if (playerWon)
        {
            PlayerWins++;
        }
        else
        {
            AiWins++;
        }

        if (PlayerWins >= FirstToLimit || AiWins >= FirstToLimit)
        {
            IsComplete = true;
            WinnerPlayer = playerWon;
        }

        return IsComplete;
    }

    public void ForfeitPlayer()
    {
        if (!IsComplete)
        {
            IsComplete = true;
            WinnerPlayer = false;
        }
    }

    public void Reset(int aiCount, int selectedLimit)
    {
        FirstToLimit = ResolveLimit(aiCount, selectedLimit);
        PlayerWins = 0;
        AiWins = 0;
        IsComplete = false;
        WinnerPlayer = null;
    }
}

internal sealed class AdimiToolsAiArenaNavigationGuard
{
    public const double GraceSeconds = 5d;

    private double _outsideSince = double.NaN;
    private double _lastCheckedAt = double.NaN;

    public bool Check(bool enabled, bool isOnNavigationMesh, double now)
    {
        if (!enabled || isOnNavigationMesh || !double.IsFinite(now) || now < 0d)
        {
            Reset();
            return false;
        }

        if (double.IsNaN(_outsideSince) || now < _lastCheckedAt)
        {
            _outsideSince = now;
        }

        _lastCheckedAt = now;
        return now - _outsideSince >= GraceSeconds;
    }

    public void Reset()
    {
        _outsideSince = double.NaN;
        _lastCheckedAt = double.NaN;
    }
}

internal sealed class AdimiToolsAiArenaSession<T>
    where T : class
{
    public const float CountdownSeconds = 3f;
    public const float BarrierRiseMeters = 3f;
    public const float BarrierTravelSeconds = 1.5f;
    public const int MaximumAiCount = 10;
    public const float HitDebounceSeconds = 0.25f;

    private double _lastAcceptedHitAt = double.NaN;

    public AdimiToolsAiArenaState State { get; private set; } = AdimiToolsAiArenaState.Idle;

    public T? Owner { get; private set; }

    public int RequestedCount { get; private set; }

    public double ReadyAt { get; private set; } = double.NaN;

    public bool IsReserved => State != AdimiToolsAiArenaState.Idle;

    public AdimiToolsAiArenaHitResult TryHit(T owner, double now)
    {
        if (owner == null)
        {
            return AdimiToolsAiArenaHitResult.InvalidOwner;
        }

        if (!IsValidTime(now))
        {
            return AdimiToolsAiArenaHitResult.InvalidTime;
        }

        if (State == AdimiToolsAiArenaState.Running)
        {
            return AdimiToolsAiArenaHitResult.AlreadyRunning;
        }

        if (State == AdimiToolsAiArenaState.Idle)
        {
            Owner = owner;
            RequestedCount = 1;
            _lastAcceptedHitAt = now;
            ReadyAt = now + CountdownSeconds;
            State = AdimiToolsAiArenaState.CountingDown;
            return AdimiToolsAiArenaHitResult.Reserved;
        }

        if (!ReferenceEquals(Owner, owner))
        {
            return AdimiToolsAiArenaHitResult.OwnedByAnother;
        }

        if (now < _lastAcceptedHitAt)
        {
            return AdimiToolsAiArenaHitResult.InvalidTime;
        }

        if (now - _lastAcceptedHitAt < HitDebounceSeconds)
        {
            return AdimiToolsAiArenaHitResult.Debounced;
        }

        if (RequestedCount >= MaximumAiCount)
        {
            return AdimiToolsAiArenaHitResult.MaximumReached;
        }

        RequestedCount++;
        _lastAcceptedHitAt = now;
        ReadyAt = now + CountdownSeconds;
        return AdimiToolsAiArenaHitResult.Counted;
    }

    public bool ShouldStart(double now)
    {
        return State == AdimiToolsAiArenaState.CountingDown
            && double.IsFinite(now)
            && now >= ReadyAt;
    }

    public bool MarkStarted(double now)
    {
        if (!ShouldStart(now))
        {
            return false;
        }

        State = AdimiToolsAiArenaState.Running;
        return true;
    }

    public void Reset()
    {
        State = AdimiToolsAiArenaState.Idle;
        Owner = null;
        RequestedCount = 0;
        ReadyAt = double.NaN;
        _lastAcceptedHitAt = double.NaN;
    }

    private static bool IsValidTime(double now)
    {
        if (!double.IsFinite(now) || now < 0d)
        {
            return false;
        }

        double deadline = now + CountdownSeconds;
        return double.IsFinite(deadline) && deadline > now;
    }
}
