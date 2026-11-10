using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A hit only opens a short opportunity. The mission thread decides whether the
// actor, target, weapon and animation are eligible; the input callback never
// reads an Agent or scene object and cannot bypass Native action handling.
internal sealed class AdimiToolsAiArenaHitCounter : AgentComponent
{
    internal const double MinimumDelaySeconds = 0.12d;
    internal const double OpportunitySeconds = 1.8d;
    internal const double RecoveryMarginSeconds = 0.03d;
    private const float MaximumStunSeconds = (float)OpportunitySeconds;
    private const float UnknownStunSeconds = 0.35f;
    private const long SnapshotMilliseconds = 100;
    private const long AttackMilliseconds = 200;
    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick
        | Agent.EventControlFlag.Mount | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear
        | Agent.EventControlFlag.Jump | Agent.EventControlFlag.ToggleAlternativeWeapon
        | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1 | Agent.EventControlFlag.Wield2
        | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;

    private readonly object _gate = new();
    private bool _pending;
    private bool _armed;
    private bool _attackStarted;
    private int _attackerIndex = -1;
    private double _hitAt;
    private double _earliestAttackAt;
    private double _lastObservedAt;
    private long _opportunityExpiresAt;
    private long _snapshotExpiresAt;
    private long _attackExpiresAt;
    private Agent.MovementControlFlag _attack;

    public AdimiToolsAiArenaHitCounter(Agent agent)
        : base(agent)
    {
    }

    // The caller filters actual hostile melee damage before recording a hit.
    // Repeated damage replaces the previous opportunity and stops its input.
    public void RecordHit(double now, int attackerIndex, float defenderStunSeconds = 0f)
    {
        lock (_gate)
        {
            if (!double.IsFinite(now) || now < 0d || attackerIndex < 0 || (_pending && now < _lastObservedAt))
            {
                ClearLocked();
                return;
            }

            long clock = Stopwatch.GetTimestamp();
            _pending = true;
            _armed = false;
            _attackStarted = false;
            _attackerIndex = attackerIndex;
            _hitAt = now;
            // The hit callback can precede Native's visible hit animation.
            // Respect its reported stun even if the sampled action temporarily
            // looks idle. Unknown values use a conservative bounded fallback;
            // a longer-than-opportunity stun naturally expires without attack.
            float boundedStun = float.IsFinite(defenderStunSeconds)
                ? Math.Clamp(defenderStunSeconds, 0f, MaximumStunSeconds)
                : UnknownStunSeconds;
            _earliestAttackAt = now + Math.Max(MinimumDelaySeconds, boundedStun + RecoveryMarginSeconds);
            _lastObservedAt = now;
            _opportunityExpiresAt = AddMilliseconds(clock, 1800);
            _snapshotExpiresAt = 0;
            _attackExpiresAt = 0;
            _attack = Agent.MovementControlFlag.None;
        }
    }

    // Called after the previous native agent tick. eligible includes the arena,
    // living actor/target, weapon and distance checks. recovering includes hit,
    // fall and other animations in which an attack must not be requested.
    // nativeAttacking means an observed Native ReadyMelee or ReleaseMelee.
    public void Observe(double now, int targetIndex, bool eligible, bool recovering, bool nativeAttacking,
        Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag preferredAttack)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return;
            }

            long clock = Stopwatch.GetTimestamp();
            if (ExpiredLocked(clock) || !double.IsFinite(now) || now < _lastObservedAt
                || now - _hitAt >= OpportunitySeconds || !eligible || targetIndex != _attackerIndex
                || (nativeAttacking && !recovering))
            {
                ClearLocked();
                return;
            }

            _lastObservedAt = now;
            Agent.MovementControlFlag allowed = allowedAttacks & Agent.MovementControlFlag.AttackMask;
            if (allowed == Agent.MovementControlFlag.None)
            {
                ClearLocked();
                return;
            }

            if (_attackStarted && (allowed & _attack) == Agent.MovementControlFlag.None)
            {
                ClearLocked();
                return;
            }

            if (recovering || now < _earliestAttackAt)
            {
                _armed = false;
                return;
            }

            if (!_attackStarted)
            {
                Agent.MovementControlFlag preferred = preferredAttack & allowed;
                _attack = ChooseSingle(preferred != Agent.MovementControlFlag.None ? preferred : allowed);
            }

            // This snapshot expires unless a fresh mission-thread observation
            // confirms that it is still safe. A started attack has its own
            // separate deadline, which repeated observations cannot extend.
            _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            _armed = true;
        }
    }

    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                if (_pending && ExpiredLocked(Stopwatch.GetTimestamp()))
                {
                    ClearLocked();
                }

                return _pending;
            }
        }
    }

    // Readable safely from other managed components; no native Agent access.
    // The callback may run on a worker, so wall-clock expiry is checked here.
    public int PendingAttackerIndex
    {
        get
        {
            lock (_gate)
            {
                if (_pending && ExpiredLocked(Stopwatch.GetTimestamp()))
                {
                    ClearLocked();
                }

                return _pending ? _attackerIndex : -1;
            }
        }
    }

    // The mission thread can additionally validate its authoritative clock
    // before retargeting. This prevents a one-frame chase after a time jump
    // or logical opportunity expiry even if little wall time has elapsed.
    public int GetPendingAttackerIndex(double now)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return -1;
            }

            if (ExpiredLocked(Stopwatch.GetTimestamp()) || !double.IsFinite(now) || now < _lastObservedAt
                || now - _hitAt >= OpportunitySeconds)
            {
                ClearLocked();
                return -1;
            }

            return _attackerIndex;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearLocked();
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return;
            }

            long clock = Stopwatch.GetTimestamp();
            if (ExpiredLocked(clock))
            {
                ClearLocked();
                return;
            }

            if ((eventFlag & InterruptingEvents) != 0 || (movementFlag & Agent.MovementControlFlag.Action) != 0)
            {
                ClearLocked();
                return;
            }

            if (!_armed || clock >= _snapshotExpiresAt)
            {
                _armed = false;
                return;
            }

            if ((movementFlag & Agent.MovementControlFlag.AttackMask) != 0)
            {
                // Native has already requested an attack; do not start another.
                ClearLocked();
                return;
            }

            if ((movementFlag & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0)
            {
                // Preserve the bounded opportunity, but require another safe
                // observation before attempting an attack after this defense.
                _armed = false;
                return;
            }

            if (!_attackStarted)
            {
                _attackStarted = true;
                _attackExpiresAt = AddMilliseconds(clock, AttackMilliseconds);
            }

            // Add a single attack direction only. All movement, turn, event and
            // analogue input remains untouched; in particular no kick is added.
            movementFlag |= _attack;
        }
    }

    private bool ExpiredLocked(long clock) => clock >= _opportunityExpiresAt
        || (_attackStarted && clock >= _attackExpiresAt);

    private static long AddMilliseconds(long clock, long milliseconds)
    {
        long duration = Stopwatch.Frequency / 1000 * milliseconds
            + Stopwatch.Frequency % 1000 * milliseconds / 1000;
        return clock > long.MaxValue - duration ? long.MaxValue : clock + duration;
    }

    private static Agent.MovementControlFlag ChooseSingle(Agent.MovementControlFlag flags)
    {
        uint bits = (uint)flags;
        return (Agent.MovementControlFlag)(bits & (~bits + 1u));
    }

    private void ClearLocked()
    {
        _pending = false;
        _armed = false;
        _attackStarted = false;
        _attackerIndex = -1;
        _hitAt = 0d;
        _earliestAttackAt = 0d;
        _lastObservedAt = 0d;
        _opportunityExpiresAt = 0;
        _snapshotExpiresAt = 0;
        _attackExpiresAt = 0;
        _attack = Agent.MovementControlFlag.None;
    }
}
