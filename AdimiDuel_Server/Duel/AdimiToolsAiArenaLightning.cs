using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// An experimental input sequence for one observed side swing. Mission-thread
// observations own all Native reads; the input callback consumes only a fresh
// snapshot. Native retains authority over attack transitions and collisions.
internal sealed class AdimiToolsAiArenaLightning : AgentComponent
{
    internal const float ReleaseWindowStart = 0.55f;
    internal const float ReleaseWindowEnd = 0.90f;
    internal const long SnapshotMilliseconds = 100;
    internal const long PrimeBeforeGuardMilliseconds = 120;
    internal const long GuardMilliseconds = 150;
    internal const long ReleaseAfterGuardMilliseconds = 190;
    internal const long AttemptMilliseconds = 1400;
    internal const long HitDebounceMilliseconds = 300;
    private const double AttemptSeconds = 1.4d;
    private const double ReleaseTransitionSeconds = 0.35d;
    private const long PrimeTransitionMilliseconds = 250;
    private const Agent.MovementControlFlag DefenseBits = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock;
    private const Agent.MovementControlFlag Swings = Agent.MovementControlFlag.AttackUp
        | Agent.MovementControlFlag.AttackLeft | Agent.MovementControlFlag.AttackRight;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;

    private enum Phase { None, Prime, AwaitQuickReady, Pulse, WaitRelease }

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private Phase _phase;
    private int _targetIndex = -1;
    private object? _weapon;
    private object? _offhand;
    private Agent.MovementControlFlag _first;
    private Agent.MovementControlFlag _next;
    private bool _fromConfirmedHit;
    private bool _releaseLocked;
    private bool _primeIssued;
    private bool _quickReadyObserved;
    private double _startedAt;
    private double _lastObservationAt;
    private bool _hasObservation;
    private long _attemptExpiresAt;
    private long _snapshotExpiresAt;
    private long _primeAt;
    private long _guardAt;
    private long _ignoreHitUntil;

    public AdimiToolsAiArenaLightning(Agent agent) : this(agent, Stopwatch.GetTimestamp) { }

    internal AdimiToolsAiArenaLightning(Agent agent, Func<long> clock) : base(agent)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // The caller must verify a real unblocked hostile melee hit that deducted
    // health, and pass the same stable Item reference later supplied to Observe.
    // This path can prepare a sequence before the next release sample; a miss
    // is supported independently by Observe. A hit never creates damage here.
    public bool NotifyFirstHit(double now, int targetIndex, object? weaponKey, int difficulty,
        Agent.UsageDirection firstDirection, bool confirmedMeleeDamage, object? offhandKey = null)
    {
        lock (_gate)
        {
            long clock = _clock();
            Agent.MovementControlFlag first = Direction(firstDirection);
            if (!confirmedMeleeDamage || difficulty != 3 || !double.IsFinite(now) || now < 0d
                || targetIndex < 0 || weaponKey == null || !IsSide(first) || _phase != Phase.None
                || _releaseLocked || clock < _ignoreHitUntil || (_hasObservation && now < _lastObservationAt))
            {
                return false;
            }

            BeginLocked(now, clock, targetIndex, weaponKey, offhandKey, first, fromConfirmedHit: true);
            return true;
        }
    }

    // eligible must already cover a live hostile target in the same active
    // arena, weapon/height/range and competing components. hardStun and
    // otherThreat include every new incoming attack or real defense reaction.
    // swingAttacks must contain only attacks supported as swings by this weapon.
    public void Observe(double now, int targetIndex, object? weaponKey, bool eligible, int difficulty,
        bool hardStun, bool otherThreat, Agent.ActionCodeType ownAction, Agent.ActionStage ownStage,
        Agent.UsageDirection ownDirection, float progress, Agent.MovementControlFlag swingAttacks, object? offhandKey = null)
    {
        lock (_gate)
        {
            long clock = _clock();
            if (!double.IsFinite(now) || now < 0d || (_hasObservation && now < _lastObservationAt))
            {
                ClearLocked();
                return;
            }

            _hasObservation = true;
            _lastObservationAt = now;
            Agent.MovementControlFlag current = Direction(ownDirection);
            bool release = ownAction == Agent.ActionCodeType.ReleaseMelee && ownStage == Agent.ActionStage.AttackRelease;
            bool quickReady = ownAction == Agent.ActionCodeType.ReadyMelee && ownStage == Agent.ActionStage.AttackQuickReady;
            bool neutral = ownStage == Agent.ActionStage.None
                && ownAction is Agent.ActionCodeType.Idle or Agent.ActionCodeType.Guard or Agent.ActionCodeType.Other;

            if (_phase == Phase.None && !release)
            {
                _releaseLocked = false;
            }

            if (!eligible || difficulty != 3 || targetIndex < 0 || weaponKey == null || hardStun || otherThreat)
            {
                if (release) _releaseLocked = true;
                StopAttemptLocked(clock);
                return;
            }

            swingAttacks &= Swings;
            if (_phase != Phase.None && (targetIndex != _targetIndex || !ReferenceEquals(weaponKey, _weapon)
                || !ReferenceEquals(offhandKey, _offhand)
                || clock >= _attemptExpiresAt || now - _startedAt >= AttemptSeconds))
            {
                StopAttemptLocked(clock);
                return;
            }

            if (_phase == Phase.None && !_releaseLocked && release && IsSide(current))
            {
                if (!float.IsFinite(progress) || progress < 0f || progress > 1f || (swingAttacks & current) == 0)
                {
                    _releaseLocked = true;
                    return;
                }

                if (progress > ReleaseWindowEnd)
                {
                    _releaseLocked = true;
                    return;
                }

                if (progress >= ReleaseWindowStart)
                {
                    BeginLocked(now, clock, targetIndex, weaponKey, offhandKey, current, fromConfirmedHit: false);
                }
            }

            if (_phase == Phase.None)
            {
                return;
            }

            if ((swingAttacks & _first) == 0)
            {
                StopAttemptLocked(clock);
                return;
            }

            if (_next == Agent.MovementControlFlag.None)
            {
                // Native quick-ready does not reliably accept immediately
                // opposite left/right cuts. Prefer an available overhead swing;
                // otherwise only try the same supported horizontal direction.
                _next = (swingAttacks & Agent.MovementControlFlag.AttackUp) != 0
                    ? Agent.MovementControlFlag.AttackUp : _first;
            }

            if ((swingAttacks & _next) == 0)
            {
                StopAttemptLocked(clock);
                return;
            }

            bool firstRelease = release && current == _first && !_quickReadyObserved;
            bool acceptedQuickReady = quickReady && current == _next;
            bool nextRelease = release && current == _next && (_quickReadyObserved || _next != _first);
            if (nextRelease)
            {
                _releaseLocked = true;
                StopAttemptLocked(clock);
                return;
            }

            if (firstRelease && !_primeIssued && !_fromConfirmedHit
                && (!float.IsFinite(progress) || progress > ReleaseWindowEnd))
            {
                // Opening the intent in the window is insufficient if the
                // callback cannot actually request the next swing in time.
                StopAttemptLocked(clock);
                return;
            }

            if (acceptedQuickReady)
            {
                _quickReadyObserved = true;
            }
            else if (!firstRelease && !(neutral && (_phase is Phase.Prime or Phase.AwaitQuickReady)
                && (_primeIssued ? clock < AddMilliseconds(_primeAt, PrimeTransitionMilliseconds)
                    : now - _startedAt <= ReleaseTransitionSeconds)))
            {
                // Unexpected ready, block, another attack or incompatible
                // transition always wins over this optional experiment.
                StopAttemptLocked(clock);
                return;
            }

            if ((_phase is Phase.Pulse or Phase.WaitRelease) && !acceptedQuickReady)
            {
                StopAttemptLocked(clock);
                return;
            }

            _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
        }
    }

    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                long clock = _clock();
                if (_phase != Phase.None && clock >= _attemptExpiresAt) StopAttemptLocked(clock);
                return _phase != Phase.None;
            }
        }
    }

    // A block, a new hit, threat or competing tactic revokes all injected
    // input without forgetting that this same release was already attempted.
    public void Suspend()
    {
        lock (_gate)
        {
            _releaseLocked = true;
            StopAttemptLocked(_clock());
        }
    }

    public void NotifyAttackBlocked() => Suspend();

    public void Clear()
    {
        lock (_gate) ClearLocked();
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        lock (_gate)
        {
            if (_phase == Phase.None) return;
            long clock = _clock();
            if (clock >= _attemptExpiresAt)
            {
                StopAttemptLocked(clock);
                return;
            }

            if (clock >= _snapshotExpiresAt) return;
            if ((eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None
                || (movementFlag & (DefenseBits | Agent.MovementControlFlag.Action)) != 0)
            {
                StopAttemptLocked(clock);
                return;
            }

            Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
            if (attack != Agent.MovementControlFlag.None && attack != _next)
            {
                // The first release may still retain its own attack bit. Wait
                // for a fresh callback instead of overriding another attack.
                if ((_phase is Phase.Prime or Phase.AwaitQuickReady) && attack == _first && !_quickReadyObserved) return;
                StopAttemptLocked(clock);
                return;
            }

            if (_phase == Phase.Prime)
            {
                _primeIssued = true;
                _primeAt = clock;
                _phase = Phase.AwaitQuickReady;
            }

            if (_phase == Phase.AwaitQuickReady && _quickReadyObserved
                && clock >= AddMilliseconds(_primeAt, PrimeBeforeGuardMilliseconds))
            {
                _phase = Phase.Pulse;
                _guardAt = clock;
            }

            if (_phase == Phase.WaitRelease) return;
            if (_phase == Phase.Pulse && clock >= AddMilliseconds(_guardAt, ReleaseAfterGuardMilliseconds))
            {
                // Stop injecting our own left input. Never erase Native input;
                // wait for a real observed release or the bounded timeout.
                _phase = Phase.WaitRelease;
                return;
            }

            movementFlag |= _next;
            if (_phase == Phase.Pulse && clock < AddMilliseconds(_guardAt, GuardMilliseconds))
            {
                movementFlag |= GuardFor(_next);
            }
        }
    }

    private void BeginLocked(double now, long clock, int targetIndex, object weapon, object? offhand,
        Agent.MovementControlFlag first, bool fromConfirmedHit)
    {
        _phase = Phase.Prime;
        _targetIndex = targetIndex;
        _weapon = weapon;
        _offhand = offhand;
        _first = first;
        _next = Agent.MovementControlFlag.None;
        _fromConfirmedHit = fromConfirmedHit;
        _releaseLocked = true;
        _primeIssued = false;
        _quickReadyObserved = false;
        _startedAt = now;
        _attemptExpiresAt = AddMilliseconds(clock, AttemptMilliseconds);
        _snapshotExpiresAt = 0; // No input until the mission thread validates this opportunity.
        _primeAt = 0;
        _guardAt = 0;
    }

    private void StopAttemptLocked(long clock)
    {
        if (_phase != Phase.None) _ignoreHitUntil = AddMilliseconds(clock, HitDebounceMilliseconds);
        _phase = Phase.None;
        _targetIndex = -1;
        _weapon = null;
        _offhand = null;
        _first = Agent.MovementControlFlag.None;
        _next = Agent.MovementControlFlag.None;
        _fromConfirmedHit = false;
        _primeIssued = false;
        _quickReadyObserved = false;
        _startedAt = 0d;
        _attemptExpiresAt = 0;
        _snapshotExpiresAt = 0;
        _primeAt = 0;
        _guardAt = 0;
    }

    private void ClearLocked()
    {
        StopAttemptLocked(_clock());
        _releaseLocked = false;
        _ignoreHitUntil = 0;
        _lastObservationAt = 0d;
        _hasObservation = false;
    }

    private static bool IsSide(Agent.MovementControlFlag direction) => direction is
        Agent.MovementControlFlag.AttackLeft or Agent.MovementControlFlag.AttackRight;

    private static Agent.MovementControlFlag Direction(Agent.UsageDirection direction) => direction switch
    {
        Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.AttackUp,
        Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.AttackLeft,
        Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.AttackRight,
        _ => Agent.MovementControlFlag.None,
    };

    private static Agent.MovementControlFlag GuardFor(Agent.MovementControlFlag attack) => attack switch
    {
        Agent.MovementControlFlag.AttackLeft => Agent.MovementControlFlag.DefendLeft,
        Agent.MovementControlFlag.AttackRight => Agent.MovementControlFlag.DefendRight,
        _ => Agent.MovementControlFlag.DefendUp,
    };

    private static long AddMilliseconds(long clock, long milliseconds)
    {
        long duration = Stopwatch.Frequency / 1000 * milliseconds
            + Stopwatch.Frequency % 1000 * milliseconds / 1000;
        return clock > long.MaxValue - duration ? long.MaxValue : clock + duration;
    }
}
