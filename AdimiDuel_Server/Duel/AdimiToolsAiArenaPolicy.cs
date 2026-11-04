using System.Globalization;

namespace AdimiDuel_Server.Duel;

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
    public const string TriggerRoleTag = "adimi_ai_trigger";
    public const string SpawnRoleTag = "adimi_ai_spawn";
    public const string BarrierRoleTag = "adimi_ai_barrier";
    public const string AreaRoleTag = "adimi_ai_area";

    public static bool IsValidRoleCounts(int triggerCount, int spawnCount, int barrierCount, int areaCount)
    {
        return triggerCount == 1
            && spawnCount == 1
            && barrierCount is 0 or 1
            && areaCount is 0 or 1;
    }

    public static bool TryParseGroupTag(string? tag, out int groupId)
    {
        groupId = 0;
        if (tag == null || !tag.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = tag.AsSpan(GroupPrefix.Length);
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

    public static string GetGroupTag(int groupId)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        return GroupPrefix + groupId.ToString(CultureInfo.InvariantCulture);
    }
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

    public void Reset(int aiCount, int selectedLimit)
    {
        FirstToLimit = ResolveLimit(aiCount, selectedLimit);
        PlayerWins = 0;
        AiWins = 0;
        IsComplete = false;
        WinnerPlayer = null;
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
