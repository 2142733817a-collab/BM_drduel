namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsTeamBattlePolicy
{
    public const string GroupPrefix = "adimi_team_group_";
    public const string SideA = "adimi_team_side_a";
    public const string SideB = "adimi_team_side_b";
    public const string Area = "adimi_team_area";
    public const string Barrier = "adimi_team_barrier";

    public static bool TryGetGroupId(IEnumerable<string> tags, out int id)
    {
        id = 0;
        string[] groups = tags.Where(tag => tag.StartsWith(GroupPrefix, StringComparison.Ordinal)).ToArray();
        if (groups.Length != 1)
        {
            return false;
        }

        string suffix = groups[0][GroupPrefix.Length..];
        return int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id)
            && id > 0 && suffix == id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal enum AdimiToolsTeamBattlePhase
{
    Waiting,
    Settling,
    CountingDown,
    Preparing,
    Running,
    RoundWaiting,
    Ending,
}

internal sealed class AdimiToolsTeamBattleReadiness<T> where T : notnull
{
    public const double StableSeconds = 3d;
    public const double CountdownSeconds = 3d;
    private readonly IEqualityComparer<T> _comparer;
    private HashSet<T> _sideA;
    private HashSet<T> _sideB;
    private bool _ready;

    public AdimiToolsTeamBattleReadiness(IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _sideA = new HashSet<T>(_comparer);
        _sideB = new HashSet<T>(_comparer);
    }

    public AdimiToolsTeamBattlePhase Phase { get; private set; }
    public double? StableSince { get; private set; }
    public double? CountdownEndsAt => StableSince + StableSeconds + CountdownSeconds;
    public IReadOnlyCollection<T> SideA => _sideA;
    public IReadOnlyCollection<T> SideB => _sideB;

    public bool Observe(IEnumerable<T> sideA, IEnumerable<T> sideB, double now)
    {
        if (Phase >= AdimiToolsTeamBattlePhase.Preparing)
        {
            return false;
        }

        var nextA = new HashSet<T>(sideA, _comparer);
        var nextB = new HashSet<T>(sideB, _comparer);
        bool changed = !_sideA.SetEquals(nextA) || !_sideB.SetEquals(nextB);
        _sideA = nextA;
        _sideB = nextB;
        _ready = false;
        if (_sideA.Count == 0 || _sideB.Count == 0 || _sideA.Overlaps(_sideB))
        {
            StableSince = null;
            Phase = AdimiToolsTeamBattlePhase.Waiting;
            return false;
        }

        if (changed || !StableSince.HasValue)
        {
            StableSince = now;
            Phase = AdimiToolsTeamBattlePhase.Settling;
        }

        if (now >= StableSince!.Value + StableSeconds)
        {
            Phase = AdimiToolsTeamBattlePhase.CountingDown;
        }

        _ready = now >= CountdownEndsAt!.Value;
        return _ready;
    }

    public void Lock()
    {
        if (Phase != AdimiToolsTeamBattlePhase.CountingDown || !_ready)
        {
            throw new InvalidOperationException("A team battle can lock only after its readiness countdown.");
        }

        Phase = AdimiToolsTeamBattlePhase.Preparing;
    }

    public void Start()
    {
        if (Phase != AdimiToolsTeamBattlePhase.Preparing)
        {
            throw new InvalidOperationException("A team battle can start only after preparation.");
        }

        Phase = AdimiToolsTeamBattlePhase.Running;
    }

    public void End() => Phase = AdimiToolsTeamBattlePhase.Ending;

    public void WaitForNextRound()
    {
        if (Phase != AdimiToolsTeamBattlePhase.Running)
        {
            throw new InvalidOperationException("Only a running team round can enter the next-round wait.");
        }

        Phase = AdimiToolsTeamBattlePhase.RoundWaiting;
    }

    public void StartNextRound()
    {
        if (Phase != AdimiToolsTeamBattlePhase.RoundWaiting)
        {
            throw new InvalidOperationException("Only a waiting team series can begin another round.");
        }

        Phase = AdimiToolsTeamBattlePhase.Running;
    }

    public void Reset()
    {
        _sideA.Clear();
        _sideB.Clear();
        StableSince = null;
        _ready = false;
        Phase = AdimiToolsTeamBattlePhase.Waiting;
    }
}

internal sealed class AdimiToolsTeamBattleSeries
{
    public AdimiToolsTeamBattleSeries(int targetWins)
    {
        if (targetWins < 1 || targetWins > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWins));
        }

        TargetWins = targetWins;
    }

    public int TargetWins { get; }
    public int SideAWins { get; private set; }
    public int SideBWins { get; private set; }
    public int Draws { get; private set; }
    public int RoundsPlayed { get; private set; }
    public bool IsComplete { get; private set; }
    public bool? WinnerSideA { get; private set; }

    public bool RecordRound(bool? sideAWon)
    {
        if (IsComplete)
        {
            return true;
        }

        RoundsPlayed++;
        if (!sideAWon.HasValue)
        {
            Draws++;
            return false;
        }

        if (sideAWon.Value)
        {
            SideAWins++;
        }
        else
        {
            SideBWins++;
        }

        if (SideAWins >= TargetWins || SideBWins >= TargetWins)
        {
            IsComplete = true;
            WinnerSideA = SideAWins >= TargetWins;
        }

        return IsComplete;
    }

    public void Forfeit(bool? winnerSideA)
    {
        if (!IsComplete)
        {
            IsComplete = true;
            WinnerSideA = winnerSideA;
        }
    }
}
