using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A bounded, experimental request to attack a visibly held melee ready. The
// mission thread owns eligibility, visibility and threat detection. The Native
// input callback consumes only the latest short-lived managed snapshot.
internal sealed class AdimiToolsAiArenaAttackInitiative : AgentComponent
{
    private enum Phase
    {
        Idle,
        Reacting,
        Starting,
        Committing,
    }

    internal const double SimpleReactionSeconds = 0.45d;
    internal const double NormalReactionSeconds = 0.25d;
    internal const double HardReactionSeconds = 0.12d;
    private const double CooldownSeconds = 1.4d;
    private const double VisibilityCacheSeconds = 0.10d;
    private const float VisibilityToleranceSquared = 0.01f;
    // Input may lose permission between low-rate mission samples. A separate
    // continuity deadline lets reaction timing resume at the next safe sample
    // without making an expired input snapshot usable.
    private const long SnapshotMilliseconds = 30;
    private const long ContinuityMilliseconds = 100;
    private const long StartMilliseconds = 240;
    private const long CommitMilliseconds = 180;
    private const long MaximumAttemptMilliseconds = 450;

    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick
        | Agent.EventControlFlag.Mount | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear
        | Agent.EventControlFlag.Jump | Agent.EventControlFlag.ToggleAlternativeWeapon
        | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1 | Agent.EventControlFlag.Wield2
        | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;
    private const Agent.MovementControlFlag DefenseBits = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock;

    private readonly object _gate = new();
    private readonly Func<long> _timestamp;
    private Phase _phase;
    private int _target = -1;
    private int _difficulty;
    private int _spentTarget = -1;
    private bool _waitingForNewReady;
    private bool _hasObservedTime;
    private double _lastObservedTime;
    private double _reactionStartedAt;
    private double _cooldownUntil;
    private long _snapshotUntil;
    private long _continuityUntil;
    private long _phaseUntil;
    private long _attemptUntil;
    private bool _issuedInput;
    private bool _reactingReady;
    private Agent.MovementControlFlag _attack;
    private Agent.MovementControlFlag _allowed;

    // This cache is mission-thread-only. It contains no scene or Agent
    // references, and is never touched by OnAIInputSet.
    private bool _hasSight;
    private int _sightTarget = -1;
    private Vec3 _observerEye;
    private Vec3 _targetEye;
    private double _sightTime;
    private bool _sightClear;

    public AdimiToolsAiArenaAttackInitiative(Agent agent)
        : this(agent, Stopwatch.GetTimestamp)
    {
    }

    // An injected monotonic clock permits deterministic expiration/race tests.
    internal AdimiToolsAiArenaAttackInitiative(Agent agent, Func<long> timestamp)
        : base(agent)
    {
        _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
    }

    // eligible: same arena, both on foot, live hostile target, visible, and
    // safely within this weapon's reach and facing. targetReady is only the
    // target's visible ReadyMelee animation, not their keyboard/mouse input.
    // danger covers every relevant hostile released swing or kick. busy covers
    // hit/parry, counter/kick/feint ownership and other interrupted actions.
    // Invoke on every sample, including targetReady=false, to rearm after a
    // held attack ends. All parameters are immutable value snapshots.
    public void Observe(double now, int targetIndex, bool eligible, bool targetReady, bool danger, bool busy,
        int difficulty, Agent.ActionCodeType ownAction, Agent.ActionStage ownStage, Agent.UsageDirection ownDirection,
        Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag preferredAttack)
    {
        lock (_gate)
        {
            long clock = _timestamp();
            if (!double.IsFinite(now) || now < 0d || (_hasObservedTime && now < _lastObservedTime))
            {
                DisarmLocked();
                _hasObservedTime = false;
                return;
            }

            if (_phase != Phase.Idle && clock >= _continuityUntil)
            {
                DisarmLocked();
            }

            _hasObservedTime = true;
            _lastObservedTime = now;

            if (_waitingForNewReady && (targetIndex != _spentTarget || !targetReady))
            {
                _waitingForNewReady = false;
                _spentTarget = -1;
            }

            Agent.MovementControlFlag allowed = allowedAttacks & Agent.MovementControlFlag.AttackMask;
            if (!eligible || !targetReady || danger || busy || targetIndex < 0 || difficulty is < 1 or > 3
                || allowed == Agent.MovementControlFlag.None || allowed != allowedAttacks
                || (preferredAttack & ~Agent.MovementControlFlag.AttackMask) != Agent.MovementControlFlag.None)
            {
                DisarmLocked();
                return;
            }

            if (_phase != Phase.Idle && (_target != targetIndex || _difficulty != difficulty))
            {
                DisarmLocked();
            }

            if (_waitingForNewReady && _spentTarget == targetIndex)
            {
                // An in-flight attempt may continue while observing this same
                // ready. Once it finishes, never loop while it remains held.
                if (_phase is not (Phase.Starting or Phase.Committing))
                {
                    return;
                }
            }
            else if (now < _cooldownUntil)
            {
                DisarmLocked();
                return;
            }

            bool ready = ownAction == Agent.ActionCodeType.ReadyMelee
                && ownStage is Agent.ActionStage.AttackReady or Agent.ActionStage.AttackQuickReady;
            bool release = ownAction == Agent.ActionCodeType.ReleaseMelee
                && ownStage == Agent.ActionStage.AttackRelease;
            int actionValue = (int)ownAction;
            bool defense = actionValue >= (int)Agent.ActionCodeType.DefendAllBegin
                && actionValue < (int)Agent.ActionCodeType.DefendAllEnd
                && ownStage is Agent.ActionStage.None or Agent.ActionStage.Defend;
            bool neutral = ownStage == Agent.ActionStage.None
                && ownAction is Agent.ActionCodeType.Idle or Agent.ActionCodeType.Guard or Agent.ActionCodeType.Other;

            if (release)
            {
                // A Native release or an observed requested release is enough;
                // do not issue another attack against the same held ready.
                SpendLocked(targetIndex, now);
                DisarmLocked();
                return;
            }

            if (!ready && !neutral && !defense)
            {
                DisarmLocked();
                return;
            }

            Agent.MovementControlFlag readyDirection = ready ? Direction(ownDirection) : Agent.MovementControlFlag.None;
            if (ready && (readyDirection == Agent.MovementControlFlag.None || (readyDirection & allowed) == 0))
            {
                DisarmLocked();
                return;
            }

            if (_phase is Phase.Starting or Phase.Committing)
            {
                if (clock >= _phaseUntil || clock >= _attemptUntil)
                {
                    DisarmLocked();
                    return;
                }

                if ((_attack & allowed) == 0)
                {
                    DisarmLocked();
                    return;
                }

                if (ready && _phase == Phase.Starting)
                {
                    // Only commit the same attack that was requested. Never
                    // turn a different Native attack into a forced release.
                    if (_issuedInput && readyDirection != _attack)
                    {
                        DisarmLocked();
                        return;
                    }

                    _attack = readyDirection;
                    _phase = Phase.Committing;
                    _phaseUntil = Math.Min(_attemptUntil, AddMilliseconds(clock, CommitMilliseconds));
                }
                else if (!ready && _phase == Phase.Committing)
                {
                    DisarmLocked();
                    return;
                }
            }
            else
            {
                if (_phase == Phase.Idle)
                {
                    _phase = Phase.Reacting;
                    _target = targetIndex;
                    _difficulty = difficulty;
                    _reactionStartedAt = now;
                }

                if (now - _reactionStartedAt >= ReactionSeconds(difficulty))
                {
                    _attack = ready ? readyDirection : ChooseSingle(preferredAttack & allowed);
                    if (_attack == Agent.MovementControlFlag.None)
                    {
                        _attack = ChooseSingle(allowed);
                    }

                    _phase = ready ? Phase.Committing : Phase.Starting;
                    _attemptUntil = AddMilliseconds(clock, MaximumAttemptMilliseconds);
                    _phaseUntil = AddMilliseconds(clock, ready ? CommitMilliseconds : StartMilliseconds);
                    // Count the arm itself as the attempt. If Native never
                    // calls back, this opportunity still cannot loop forever.
                    SpendLocked(targetIndex, now);
                }
            }

            _reactingReady = _phase == Phase.Reacting && ready;
            if (_reactingReady)
            {
                _attack = readyDirection;
            }

            _allowed = allowed;
            _snapshotUntil = AddMilliseconds(clock, SnapshotMilliseconds);
            _continuityUntil = AddMilliseconds(clock, ContinuityMilliseconds);
        }
    }

    // During reaction, ownership exists only while protecting an observed
    // ready against a needless guard. A neutral reaction remains passive.
    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                long clock = _timestamp();
                if (_phase is Phase.Starting or Phase.Committing
                    && (clock >= _phaseUntil || clock >= _attemptUntil))
                {
                    DisarmLocked();
                }

                return clock < _snapshotUntil && (_phase is Phase.Starting or Phase.Committing
                    || (_phase == Phase.Reacting && _reactingReady));
            }
        }
    }

    // Keep cooldown and spent-ready state when eligibility changes; otherwise
    // rapidly toggling visibility or range could repeatedly trigger attacks.
    public void Clear()
    {
        lock (_gate)
        {
            DisarmLocked();
        }
    }

    // Safe in native hit/removal callbacks: no main-thread sight cache access.
    public void Suspend()
    {
        lock (_gate)
        {
            DisarmLocked();
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        lock (_gate)
        {
            if (_phase is not (Phase.Reacting or Phase.Starting or Phase.Committing))
            {
                return;
            }

            long clock = _timestamp();
            if (clock >= _snapshotUntil)
            {
                return;
            }

            if ((_phase is Phase.Starting or Phase.Committing && (clock >= _phaseUntil || clock >= _attemptUntil))
                || (eventFlag & InterruptingEvents) != 0 || (movementFlag & Agent.MovementControlFlag.Action) != 0)
            {
                DisarmLocked();
                return;
            }

            Agent.MovementControlFlag nativeAttack = movementFlag & Agent.MovementControlFlag.AttackMask;
            if (nativeAttack != Agent.MovementControlFlag.None && !IsSingle(nativeAttack))
            {
                DisarmLocked();
                return;
            }

            if (_phase == Phase.Reacting)
            {
                // Never manufacture a hold when Native is naturally releasing.
                // Only suppress a sampled unthreatened defensive cancellation.
                if (!_reactingReady || (movementFlag & DefenseBits) == 0)
                {
                    return;
                }

                if (nativeAttack != Agent.MovementControlFlag.None && nativeAttack != _attack)
                {
                    DisarmLocked();
                    return;
                }

                movementFlag = (movementFlag & ~(DefenseBits | Agent.MovementControlFlag.AttackMask)) | _attack;
                return;
            }

            if (_phase == Phase.Starting && nativeAttack != Agent.MovementControlFlag.None)
            {
                if ((nativeAttack & _allowed) == 0 || (_issuedInput && nativeAttack != _attack))
                {
                    DisarmLocked();
                    return;
                }

                _attack = nativeAttack;
            }

            if (!_issuedInput)
            {
                _issuedInput = true;
            }

            // Remove only the unthreatened ordinary guard. For a neutral bot,
            // hold one valid attack direction until its Ready is observed; for
            // an already readied attack, release the attack bit. Native is
            // solely responsible for accepting the actual action transition.
            movementFlag &= ~DefenseBits;
            if (_phase == Phase.Starting)
            {
                movementFlag = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | _attack;
            }
            else
            {
                movementFlag &= ~Agent.MovementControlFlag.AttackMask;
            }
        }
    }

    // Main-thread sight caching avoids repeated scene raycasts; these methods
    // do not affect the concurrent input snapshot or access any Native object.
    public bool TryGetCachedSight(double now, int targetIndex, Vec3 observerEye, Vec3 targetEye, out bool clear)
    {
        clear = false;
        if (!_hasSight || !ValidSight(now, targetIndex, observerEye, targetEye) || targetIndex != _sightTarget
            || now < _sightTime || now - _sightTime >= VisibilityCacheSeconds
            || DistanceSquared(observerEye, _observerEye) > VisibilityToleranceSquared
            || DistanceSquared(targetEye, _targetEye) > VisibilityToleranceSquared)
        {
            return false;
        }

        clear = _sightClear;
        return true;
    }

    public void RememberSight(double now, int targetIndex, Vec3 observerEye, Vec3 targetEye, bool clear)
    {
        if (!ValidSight(now, targetIndex, observerEye, targetEye))
        {
            _hasSight = false;
            return;
        }

        _sightTarget = targetIndex;
        _observerEye = observerEye;
        _targetEye = targetEye;
        _sightTime = now;
        _sightClear = clear;
        _hasSight = true;
    }

    private void SpendLocked(int targetIndex, double now)
    {
        _waitingForNewReady = true;
        _spentTarget = targetIndex;
        _cooldownUntil = Math.Max(_cooldownUntil, now + CooldownSeconds);
    }

    private void DisarmLocked()
    {
        _phase = Phase.Idle;
        _target = -1;
        _difficulty = 0;
        _reactionStartedAt = 0d;
        _snapshotUntil = 0;
        _continuityUntil = 0;
        _phaseUntil = 0;
        _attemptUntil = 0;
        _issuedInput = false;
        _reactingReady = false;
        _attack = Agent.MovementControlFlag.None;
        _allowed = Agent.MovementControlFlag.None;
    }

    private static double ReactionSeconds(int difficulty) => difficulty switch
    {
        1 => SimpleReactionSeconds,
        2 => NormalReactionSeconds,
        _ => HardReactionSeconds,
    };

    private static Agent.MovementControlFlag Direction(Agent.UsageDirection direction) => direction switch
    {
        Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.AttackLeft,
        Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.AttackRight,
        Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.AttackUp,
        Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.AttackDown,
        _ => Agent.MovementControlFlag.None,
    };

    private static bool IsSingle(Agent.MovementControlFlag flag)
    {
        uint bits = (uint)flag;
        return bits != 0 && (bits & (bits - 1u)) == 0;
    }

    private static Agent.MovementControlFlag ChooseSingle(Agent.MovementControlFlag flags)
    {
        uint bits = (uint)flags;
        return (Agent.MovementControlFlag)(bits & (~bits + 1u));
    }

    private static long AddMilliseconds(long clock, long milliseconds)
    {
        long duration = Stopwatch.Frequency / 1000 * milliseconds
            + Stopwatch.Frequency % 1000 * milliseconds / 1000;
        return clock > long.MaxValue - duration ? long.MaxValue : clock + duration;
    }

    private static bool ValidSight(double now, int targetIndex, Vec3 a, Vec3 b) =>
        double.IsFinite(now) && now >= 0d && targetIndex >= 0
        && float.IsFinite(a.x) && float.IsFinite(a.y) && float.IsFinite(a.z)
        && float.IsFinite(b.x) && float.IsFinite(b.y) && float.IsFinite(b.z);

    private static float DistanceSquared(Vec3 a, Vec3 b)
    {
        float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
        return x * x + y * y + z * z;
    }
}
