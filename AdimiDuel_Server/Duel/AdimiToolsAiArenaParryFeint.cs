using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// This is an optional post-parry input sequence, not a replacement for Native
// combat. The mission thread verifies the live actors, equipment, threats and
// action stages; the AI callback consumes only short-lived managed snapshots.
internal sealed class AdimiToolsAiArenaParryFeint : AgentComponent
{
    private enum Phase { Idle, HoldReady, CancelReady, WaitNeutral, RequestNext, HoldFinal, PulseGuard, PulseOverlap, PulseAttack, WaitRelease }

    internal const double HoldSeconds = 0.06d;
    internal const double CancelTimeoutSeconds = 0.45d;
    internal const double RequestTimeoutSeconds = 0.45d;
    internal const double ReleaseTimeoutSeconds = 0.35d;
    internal const double EpisodeTimeoutSeconds = 4d;
    private const long SnapshotMilliseconds = 100;

    private const Agent.MovementControlFlag AttackMask = Agent.MovementControlFlag.AttackMask;
    private const Agent.MovementControlFlag DefenseMask = Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock;
    private const Agent.MovementControlFlag SideSwings = Agent.MovementControlFlag.AttackLeft | Agent.MovementControlFlag.AttackRight;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;

    private static readonly Agent.MovementControlFlag[] Directions =
    {
        Agent.MovementControlFlag.AttackUp, Agent.MovementControlFlag.AttackDown,
        Agent.MovementControlFlag.AttackLeft, Agent.MovementControlFlag.AttackRight,
    };

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Func<int, int> _next;
    private readonly Agent.MovementControlFlag[] _plan = new Agent.MovementControlFlag[4];
    private Phase _phase;
    private object? _weapon;
    private object? _offhand;
    private int _target = -1;
    private int _planLength;
    private int _planIndex;
    private Agent.MovementControlFlag _current;
    private bool _cancelSent;
    private bool _retryCancel;
    private bool _requestSent;
    private bool _finalReadyObserved;
    private bool _attemptOverlap;
    private bool _pulseGuardSent;
    private bool _pulseOverlapSent;
    private bool _pulseReadyObserved;
    private int _pulseGuardLeadMs;
    private int _pulseOverlapMs;
    private int _pulseAttackHeldMs;
    private long _pulseNextAt;
    private long _pulseAttackUntil;
    private double _now;
    private double _holdUntil;
    private double _phaseUntil;
    private double _episodeUntil;
    private long _phaseClockUntil;
    private long _episodeClockUntil;
    private long _snapshotUntil;

    public AdimiToolsAiArenaParryFeint(Agent agent)
        : this(agent, Stopwatch.GetTimestamp, upper => Random.Shared.Next(upper))
    {
    }

    internal AdimiToolsAiArenaParryFeint(Agent agent, Func<long> clock, Func<int, int> next)
        : base(agent)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    // Caller must offer this at most once per confirmed parry, and only after
    // observing its first Native overhead/thrust ready. No input is emitted by
    // this method, and no action state is inferred from an input flag.
    public bool TryBegin(double now, int targetIndex, object? weapon, object? offhand, int difficulty,
        bool eligible, bool threat, bool hardStun, Agent.ActionCodeType action, Agent.ActionStage stage,
        Agent.UsageDirection direction, Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag swingAttacks,
        bool attemptGuardAttackPulse = false)
    {
        lock (_gate)
        {
            long clock = _clock();
            ExpireLocked(clock);
            Agent.MovementControlFlag first = Direction(direction);
            Agent.MovementControlFlag allowed = allowedAttacks & AttackMask;
            Agent.MovementControlFlag finalSwings = swingAttacks & allowed & SideSwings;
            if (_phase != Phase.Idle || !double.IsFinite(now) || now < 0d || targetIndex < 0 || weapon == null
                || difficulty != 3 || !eligible || threat || hardStun || action != Agent.ActionCodeType.ReadyMelee
                || stage != Agent.ActionStage.AttackReady || (first != Agent.MovementControlFlag.AttackUp
                    && first != Agent.MovementControlFlag.AttackDown) || (allowed & first) == 0
                || finalSwings == Agent.MovementControlFlag.None)
            {
                return false;
            }

            if (!TryMakePlan(first, allowed, finalSwings, minimum: 2, maximum: 4, out int count))
            {
                return false;
            }

            _target = targetIndex;
            _weapon = weapon;
            _offhand = offhand;
            _current = first;
            _planLength = count;
            _planIndex = 0;
            _now = now;
            _episodeUntil = now + EpisodeTimeoutSeconds;
            _episodeClockUntil = AddMilliseconds(clock, (long)(EpisodeTimeoutSeconds * 1000d));
            _finalReadyObserved = false;
            _attemptOverlap = attemptGuardAttackPulse && TryChoosePulseTiming(out _pulseGuardLeadMs, out _pulseOverlapMs, out _pulseAttackHeldMs);
            _pulseGuardSent = false;
            _pulseOverlapSent = false;
            _pulseReadyObserved = false;
            StartHoldLocked(now, clock, final: false);
            return true;
        }
    }

    // Caller reads Native on the mission thread. A tracked ready is accepted
    // only after this component requested its direction in an earlier callback.
    public void Observe(double now, int targetIndex, object? weapon, object? offhand, int difficulty,
        bool eligible, bool threat, bool hardStun, Agent.ActionCodeType action, Agent.ActionStage stage,
        Agent.UsageDirection direction, Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag swingAttacks)
    {
        lock (_gate)
        {
            long clock = _clock();
            ExpireLocked(clock);
            if (_phase == Phase.Idle)
            {
                return;
            }

            if (!double.IsFinite(now) || now < _now || now >= _episodeUntil || now >= _phaseUntil
                || !eligible || targetIndex != _target || !ReferenceEquals(_weapon, weapon)
                || !ReferenceEquals(_offhand, offhand) || difficulty != 3)
            {
                StopLocked();
                return;
            }

            _now = now;
            if (threat || hardStun)
            {
                StopLocked();
                return;
            }

            Agent.MovementControlFlag allowed = allowedAttacks & AttackMask;
            Agent.MovementControlFlag finalSwings = swingAttacks & allowed & SideSwings;
            if ((_current & allowed) == 0 || (_plan[_planLength - 1] & finalSwings) == 0)
            {
                StopLocked();
                return;
            }

            for (int index = _planIndex; index < _planLength; index++)
            {
                if ((_plan[index] & allowed) == 0)
                {
                    StopLocked();
                    return;
                }
            }

            Agent.MovementControlFlag actual = Direction(direction);
            bool ready = action == Agent.ActionCodeType.ReadyMelee && stage == Agent.ActionStage.AttackReady;
            bool expectedReady = ready && actual == _current;
            bool neutral = stage == Agent.ActionStage.None
                && action is Agent.ActionCodeType.Idle or Agent.ActionCodeType.Guard or Agent.ActionCodeType.Other;
            int type = (int)action;
            bool ordinaryDefense = stage == Agent.ActionStage.Defend
                && type >= (int)Agent.ActionCodeType.DefendAllBegin && type < (int)Agent.ActionCodeType.DefendAllEnd;
            bool finalRelease = action == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease
                && actual == _current;

            switch (_phase)
            {
                case Phase.HoldReady:
                case Phase.CancelReady:
                    if (!expectedReady)
                    {
                        StopLocked();
                        return;
                    }

                    if (_phase == Phase.HoldReady && now >= _holdUntil)
                    {
                        _phase = Phase.CancelReady;
                    }

                    break;
                case Phase.WaitNeutral:
                    if (!_cancelSent)
                    {
                        StopLocked();
                        return;
                    }

                    _retryCancel = expectedReady;
                    if (neutral)
                    {
                        _requestSent = false;
                        SetPhaseLocked(Phase.RequestNext, now, clock, RequestTimeoutSeconds);
                    }
                    else if (!ordinaryDefense && !expectedReady)
                    {
                        StopLocked();
                        return;
                    }

                    break;
                case Phase.RequestNext:
                    if (ready && actual == _plan[_planIndex] && _requestSent)
                    {
                        _current = actual;
                        _planIndex++;
                        bool final = _planIndex == _planLength;
                        _finalReadyObserved = final;
                        StartHoldLocked(now, clock, final);
                    }
                    else if (!neutral && !ordinaryDefense)
                    {
                        StopLocked();
                        return;
                    }

                    break;
                case Phase.HoldFinal:
                case Phase.WaitRelease:
                    if (finalRelease && _finalReadyObserved)
                    {
                        StopLocked();
                        return;
                    }

                    if (!expectedReady)
                    {
                        StopLocked();
                        return;
                    }

                    if (_phase == Phase.HoldFinal && _attemptOverlap && now >= _holdUntil)
                    {
                        SetPhaseLocked(Phase.PulseGuard, now, clock, CancelTimeoutSeconds);
                    }

                    break;
                case Phase.PulseGuard:
                case Phase.PulseOverlap:
                case Phase.PulseAttack:
                    if (finalRelease && _finalReadyObserved)
                    {
                        StopLocked();
                        return;
                    }

                    // The pulse itself can briefly enter ordinary defense or
                    // neutral. A different ready/attack never belongs to it.
                    if (!expectedReady && !ordinaryDefense && !neutral)
                    {
                        StopLocked();
                        return;
                    }

                    if (_phase == Phase.PulseGuard && _pulseGuardSent && clock >= _pulseNextAt)
                    {
                        SetPhaseLocked(Phase.PulseOverlap, now, clock, RequestTimeoutSeconds);
                    }
                    else if (_phase == Phase.PulseOverlap && _pulseOverlapSent && clock >= _pulseNextAt)
                    {
                        SetPhaseLocked(Phase.PulseAttack, now, clock, RequestTimeoutSeconds);
                    }
                    else if (_phase == Phase.PulseAttack)
                    {
                        _pulseReadyObserved |= expectedReady;
                        if (_pulseReadyObserved && clock >= _pulseAttackUntil)
                        {
                            SetPhaseLocked(Phase.WaitRelease, now, clock, ReleaseTimeoutSeconds);
                        }
                    }

                    break;
            }

            _snapshotUntil = AddMilliseconds(clock, SnapshotMilliseconds);
        }
    }

    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                ExpireLocked(_clock());
                return _phase != Phase.Idle;
            }
        }
    }

    // A future specialized pulse controller may use this read-only seam; it
    // does not itself create any input or certify Native pulse behavior.
    public bool CanConsiderGuardAttackOverlap(int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            ExpireLocked(clock);
            return _target == targetIndex && _finalReadyObserved && _phase == Phase.HoldFinal && clock < _snapshotUntil;
        }
    }

    // An interrupted sequence immediately relinquishes its input snapshot.
    public void Cancel(double now, bool dueToThreat)
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    // A surviving arena bot can carry on into the next round.
    public void ResetEpisode()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        lock (_gate)
        {
            long clock = _clock();
            ExpireLocked(clock);
            if (_phase == Phase.Idle || clock >= _snapshotUntil)
            {
                return;
            }

            if ((eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None || (movementFlag & Agent.MovementControlFlag.Action) != 0)
            {
                StopLocked();
                return;
            }

            if (_phase == Phase.WaitNeutral)
            {
                // Our previous cancel can legitimately still be Native
                // defense. If Native is still in the original ready, one
                // fresh sample permits retrying the same cancel once.
                Agent.MovementControlFlag defenseDuringCancel = movementFlag & DefenseMask;
                Agent.MovementControlFlag attackDuringCancel = movementFlag & AttackMask;
                if (_retryCancel && defenseDuringCancel == Agent.MovementControlFlag.None
                    && (attackDuringCancel == _current || attackDuringCancel == Agent.MovementControlFlag.None))
                {
                    movementFlag = (movementFlag & ~_current) | GuardFor(_current);
                    _retryCancel = false;
                }
                else if (defenseDuringCancel == Agent.MovementControlFlag.None
                    && attackDuringCancel != Agent.MovementControlFlag.None && attackDuringCancel != _current)
                {
                    StopLocked();
                }

                return;
            }

            Agent.MovementControlFlag defense = movementFlag & DefenseMask;
            Agent.MovementControlFlag attack = movementFlag & AttackMask;
            if (defense != Agent.MovementControlFlag.None || !IsSingleOrNone(attack))
            {
                StopLocked();
                return;
            }

            switch (_phase)
            {
                case Phase.CancelReady:
                    if (attack == _current || attack == Agent.MovementControlFlag.None)
                    {
                        movementFlag = (movementFlag & ~_current) | GuardFor(_current);
                        _cancelSent = true;
                        SetPhaseLocked(Phase.WaitNeutral, _now, clock, CancelTimeoutSeconds);
                    }
                    else if (attack != Agent.MovementControlFlag.None)
                    {
                        StopLocked();
                    }

                    break;
                case Phase.HoldReady:
                    if (attack == Agent.MovementControlFlag.None)
                    {
                        // This component owns the freshly observed ready;
                        // briefly keep it charged even when Native does not
                        // repeat the corresponding input bit on its own.
                        movementFlag |= _current;
                    }
                    else if (attack != _current)
                    {
                        StopLocked();
                    }

                    break;
                case Phase.RequestNext:
                    Agent.MovementControlFlag wanted = _plan[_planIndex];
                    if (attack == Agent.MovementControlFlag.None)
                    {
                        movementFlag |= wanted;
                        _requestSent = true;
                    }
                    else if (attack != wanted)
                    {
                        StopLocked();
                    }

                    break;
                case Phase.HoldFinal:
                    if (attack != Agent.MovementControlFlag.None && attack != _current)
                    {
                        StopLocked();
                    }
                    else if (_now < _holdUntil)
                    {
                        if (attack == Agent.MovementControlFlag.None)
                        {
                            movementFlag |= _current;
                        }
                    }
                    else
                    {
                        movementFlag &= ~_current;
                        SetPhaseLocked(Phase.WaitRelease, _now, clock, ReleaseTimeoutSeconds);
                    }

                    break;
                case Phase.WaitRelease:
                    if (attack == _current)
                    {
                        movementFlag &= ~_current;
                    }
                    else if (attack != Agent.MovementControlFlag.None)
                    {
                        StopLocked();
                    }

                    break;
                case Phase.PulseGuard:
                    if (attack == Agent.MovementControlFlag.None || attack == _current)
                    {
                        movementFlag = (movementFlag & ~_current) | GuardFor(_current);
                        if (!_pulseGuardSent)
                        {
                            _pulseGuardSent = true;
                            _pulseNextAt = AddMilliseconds(clock, _pulseGuardLeadMs);
                        }
                    }
                    else
                    {
                        StopLocked();
                    }

                    break;
                case Phase.PulseOverlap:
                    if (attack == Agent.MovementControlFlag.None || attack == _current)
                    {
                        movementFlag |= GuardFor(_current) | _current;
                        if (!_pulseOverlapSent)
                        {
                            _pulseOverlapSent = true;
                            _pulseNextAt = AddMilliseconds(clock, _pulseOverlapMs);
                            _pulseAttackUntil = AddMilliseconds(clock, _pulseAttackHeldMs);
                        }
                    }
                    else
                    {
                        StopLocked();
                    }

                    break;
                case Phase.PulseAttack:
                    if (attack == Agent.MovementControlFlag.None)
                    {
                        movementFlag |= _current;
                    }
                    else if (attack != _current)
                    {
                        StopLocked();
                    }

                    break;
            }
        }
    }

    internal int PlannedFeints { get { lock (_gate) { return _planLength; } } }

    private bool TryMakePlan(Agent.MovementControlFlag first, Agent.MovementControlFlag allowed,
        Agent.MovementControlFlag finalSwings, int minimum, int maximum, out int count)
    {
        count = 0;
        try
        {
            int randomCount = _next(maximum - minimum + 1);
            if (randomCount < 0 || randomCount > maximum - minimum)
            {
                return false;
            }

            int desired = minimum + randomCount;
            if (!CanFinish(first, desired, allowed, finalSwings))
            {
                desired = 0;
                for (int candidate = minimum; candidate <= maximum; candidate++)
                {
                    if (CanFinish(first, candidate, allowed, finalSwings))
                    {
                        desired = candidate;
                        break;
                    }
                }
            }

            if (desired == 0)
            {
                return false;
            }

            Agent.MovementControlFlag current = first;
            for (int index = 0; index < desired; index++)
            {
                var choices = new Agent.MovementControlFlag[4];
                int length = 0;
                foreach (Agent.MovementControlFlag candidate in Directions)
                {
                    if (candidate != current && (allowed & candidate) != 0
                        && CanFinish(candidate, desired - index - 1, allowed, finalSwings))
                    {
                        choices[length++] = candidate;
                    }
                }

                if (length == 0)
                {
                    return false;
                }

                int picked = _next(length);
                if (picked < 0 || picked >= length)
                {
                    return false;
                }

                current = choices[picked];
                _plan[index] = current;
            }

            count = desired;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryChoosePulseTiming(out int guardLeadMs, out int overlapMs, out int attackHeldMs)
    {
        guardLeadMs = 0;
        overlapMs = 0;
        attackHeldMs = 0;
        try
        {
            int lead = _next(47);
            int overlap = _next(36);
            int held = _next(91);
            if (lead < 0 || lead >= 47 || overlap < 0 || overlap >= 36 || held < 0 || held >= 91)
            {
                return false;
            }

            guardLeadMs = 10 + lead;
            overlapMs = 15 + overlap;
            attackHeldMs = 60 + held;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanFinish(Agent.MovementControlFlag current, int remaining,
        Agent.MovementControlFlag allowed, Agent.MovementControlFlag finalSwings)
    {
        if (remaining == 0)
        {
            return (current & finalSwings) != 0;
        }

        foreach (Agent.MovementControlFlag candidate in Directions)
        {
            if (candidate != current && (allowed & candidate) != 0 && CanFinish(candidate, remaining - 1, allowed, finalSwings))
            {
                return true;
            }
        }

        return false;
    }

    private void StartHoldLocked(double now, long clock, bool final)
    {
        _cancelSent = false;
        _retryCancel = false;
        _requestSent = false;
        _holdUntil = now + HoldSeconds;
        SetPhaseLocked(final ? Phase.HoldFinal : Phase.HoldReady, now, clock, CancelTimeoutSeconds);
        _snapshotUntil = AddMilliseconds(clock, SnapshotMilliseconds);
    }

    private void SetPhaseLocked(Phase phase, double now, long clock, double seconds)
    {
        _phase = phase;
        _phaseUntil = now + seconds;
        _phaseClockUntil = AddMilliseconds(clock, (long)(seconds * 1000d));
    }

    private void ExpireLocked(long clock)
    {
        if (_phase != Phase.Idle && (clock >= _phaseClockUntil || clock >= _episodeClockUntil))
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _phase = Phase.Idle;
        _target = -1;
        _weapon = null;
        _offhand = null;
        _current = Agent.MovementControlFlag.None;
        _planLength = 0;
        _planIndex = 0;
        _phaseUntil = 0d;
        _phaseClockUntil = 0;
        _episodeUntil = 0d;
        _episodeClockUntil = 0;
        _snapshotUntil = 0;
        _cancelSent = false;
        _retryCancel = false;
        _requestSent = false;
        _finalReadyObserved = false;
    }

    private static long AddMilliseconds(long clock, long milliseconds)
    {
        long duration = Stopwatch.Frequency / 1000 * milliseconds
            + Stopwatch.Frequency % 1000 * milliseconds / 1000;
        return clock > long.MaxValue - duration ? long.MaxValue : clock + duration;
    }

    private static bool IsSingleOrNone(Agent.MovementControlFlag attack)
    {
        uint bits = (uint)attack;
        return bits == 0 || (bits & (bits - 1u)) == 0;
    }

    private static Agent.MovementControlFlag Direction(Agent.UsageDirection direction) => direction switch
    {
        Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.AttackUp,
        Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.AttackDown,
        Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.AttackLeft,
        Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.AttackRight,
        _ => Agent.MovementControlFlag.None,
    };

    private static Agent.MovementControlFlag GuardFor(Agent.MovementControlFlag attack) => attack switch
    {
        Agent.MovementControlFlag.AttackUp => Agent.MovementControlFlag.DefendUp,
        Agent.MovementControlFlag.AttackDown => Agent.MovementControlFlag.DefendDown,
        Agent.MovementControlFlag.AttackLeft => Agent.MovementControlFlag.DefendLeft,
        Agent.MovementControlFlag.AttackRight => Agent.MovementControlFlag.DefendRight,
        _ => Agent.MovementControlFlag.None,
    };
}
