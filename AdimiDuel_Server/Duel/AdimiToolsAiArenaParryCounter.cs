using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A confirmed hostile melee block opens one bounded counter opportunity. The
// mission thread owns all engine reads; OnAIInputSet consumes only snapshots.
internal sealed class AdimiToolsAiArenaParryCounter : AgentComponent
{
    internal const double MinimumDelaySeconds = 0.12d;
    internal const double OpportunitySeconds = 1.8d;
    internal const double RecoveryMarginSeconds = 0.03d;
    // Cap only at the entire opportunity lifetime, never below it: a valid
    // longer Native stun must not become permission to attack prematurely.
    private const float MaximumStunSeconds = (float)OpportunitySeconds;
    private const float UnknownStunSeconds = 0.35f;
    private const long SnapshotMilliseconds = 100;
    private const long AttackMilliseconds = 200;
    private const long OpportunityMilliseconds = 1800;
    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick
        | Agent.EventControlFlag.Mount | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear
        | Agent.EventControlFlag.Jump | Agent.EventControlFlag.ToggleAlternativeWeapon
        | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1 | Agent.EventControlFlag.Wield2
        | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;
    private const Agent.MovementControlFlag DefenseBits = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock;

    private readonly object _gate = new();
    private bool _pending;
    private bool _armed;
    private bool _attackStarted;
    private int _attackerIndex = -1;
    private double _parryAt;
    private double _lastObservedAt;
    private double _earliestAttackAt;
    private long _opportunityExpiresAt;
    private long _snapshotExpiresAt;
    private long _attackExpiresAt;
    private Agent.MovementControlFlag _attack;

    // Retain this separately after the counter is consumed so the same already
    // blocked release cannot reassert custom guard during the next callback.
    private bool _suppressBlockedSwing;
    private int _blockedAttackerIndex = -1;
    private double _blockedAt;
    private long _blockedSwingExpiresAt;

    public AdimiToolsAiArenaParryCounter(Agent agent)
        : base(agent)
    {
    }

    // The caller must filter a real hostile melee Blocked/Parried collision;
    // chamber attacks, kicks, missiles and unrelated agents are not parries.
    public void RecordParry(double now, int attackerIndex, float defenderStunSeconds)
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            PruneLocked(clock);
            if (!double.IsFinite(now) || now < 0d || attackerIndex < 0
                || (_pending && now < _lastObservedAt))
            {
                ClearLocked();
                return;
            }

            if (_suppressBlockedSwing && now - _blockedAt >= OpportunitySeconds)
            {
                ClearSuppressionLocked();
            }

            // Multiple collision callbacks for the same still-releasing swing
            // are not additional opportunities and cannot extend deadlines.
            if (_suppressBlockedSwing && attackerIndex == _blockedAttackerIndex)
            {
                return;
            }

            float boundedStun = float.IsFinite(defenderStunSeconds)
                ? Math.Clamp(defenderStunSeconds, 0f, MaximumStunSeconds)
                : UnknownStunSeconds;
            _pending = true;
            _armed = false;
            _attackStarted = false;
            _attackerIndex = attackerIndex;
            _parryAt = now;
            _lastObservedAt = now;
            _earliestAttackAt = now + Math.Max(MinimumDelaySeconds, boundedStun + RecoveryMarginSeconds);
            _opportunityExpiresAt = AddMilliseconds(clock, OpportunityMilliseconds);
            _snapshotExpiresAt = 0;
            _attackExpiresAt = 0;
            _attack = Agent.MovementControlFlag.None;

            _suppressBlockedSwing = true;
            _blockedAttackerIndex = attackerIndex;
            _blockedAt = now;
            _blockedSwingExpiresAt = _opportunityExpiresAt;
        }
    }

    // eligible includes live, same-arena hostile actor and target, on-foot
    // melee equipment and attack distance. hardStun includes both channels'
    // hit/fall/interruption and DefendParry, but not a normal Defend pose.
    // otherThreat means any new Release, including from the blocked attacker
    // after that swing ended, or a release from another nearby opponent.
    public void Observe(double now, int targetIndex, bool eligible, bool hardStun, bool nativeAttacking,
        bool otherThreat, Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag preferredAttack)
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            PruneLocked(clock);
            if (!_pending)
            {
                return;
            }

            if (!double.IsFinite(now) || now < _lastObservedAt || now - _parryAt >= OpportunitySeconds
                || !eligible || targetIndex != _attackerIndex)
            {
                ClearLocked();
                return;
            }

            _lastObservedAt = now;
            if (nativeAttacking && !hardStun)
            {
                // Native already began an attack. Keep only the old-swing
                // guard suppression until that release ends or expires.
                ClearCounterLocked();
                return;
            }

            Agent.MovementControlFlag allowed = allowedAttacks & Agent.MovementControlFlag.AttackMask;
            if (allowed == Agent.MovementControlFlag.None
                || (_attackStarted && (allowed & _attack) == Agent.MovementControlFlag.None))
            {
                ClearLocked();
                return;
            }

            if (hardStun || otherThreat || now < _earliestAttackAt)
            {
                _armed = false;
                return;
            }

            if (!_attackStarted)
            {
                Agent.MovementControlFlag preferred = preferredAttack & allowed;
                _attack = ChooseSingle(preferred != Agent.MovementControlFlag.None ? preferred : allowed);
            }

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
                PruneLocked(Stopwatch.GetTimestamp());
                return _pending;
            }
        }
    }

    public int PendingAttackerIndex
    {
        get
        {
            lock (_gate)
            {
                PruneLocked(Stopwatch.GetTimestamp());
                return _pending ? _attackerIndex : -1;
            }
        }
    }

    // Call for the blocked attacker on every sample, including false once its
    // release ends. A different attacker is never suppressed. Only suppress a
    // release identified as the same one that produced the block.
    public bool ShouldYieldGuardFor(int attackerIndex, bool sameSwingReleasing, double now)
    {
        lock (_gate)
        {
            PruneLocked(Stopwatch.GetTimestamp());
            if (!_suppressBlockedSwing || attackerIndex != _blockedAttackerIndex)
            {
                return false;
            }

            if (!double.IsFinite(now) || now < _blockedAt || now - _blockedAt >= OpportunitySeconds
                || !sameSwingReleasing)
            {
                ClearSuppressionLocked();
                return false;
            }

            return true;
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
            long clock = Stopwatch.GetTimestamp();
            PruneLocked(clock);
            if (!_pending)
            {
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
                // Respect a Native or higher-priority attack, including an
                // already accepted request. Never add a second direction.
                ClearCounterLocked();
                return;
            }

            if (!_attackStarted)
            {
                _attackStarted = true;
                _attackExpiresAt = AddMilliseconds(clock, AttackMilliseconds);
            }

            // A fresh safe sample permits the stale ordinary guard to be
            // released for a single bounded attack request. Other movement,
            // event and analog inputs are preserved; Native enforces timing.
            movementFlag = (movementFlag & ~DefenseBits) | _attack;
        }
    }

    private void PruneLocked(long clock)
    {
        if (_pending && (clock >= _opportunityExpiresAt || (_attackStarted && clock >= _attackExpiresAt)))
        {
            ClearCounterLocked();
        }

        if (_suppressBlockedSwing && clock >= _blockedSwingExpiresAt)
        {
            ClearSuppressionLocked();
        }
    }

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

    private void ClearCounterLocked()
    {
        _pending = false;
        _armed = false;
        _attackStarted = false;
        _attackerIndex = -1;
        _parryAt = 0d;
        _lastObservedAt = 0d;
        _earliestAttackAt = 0d;
        _opportunityExpiresAt = 0;
        _snapshotExpiresAt = 0;
        _attackExpiresAt = 0;
        _attack = Agent.MovementControlFlag.None;
    }

    private void ClearSuppressionLocked()
    {
        _suppressBlockedSwing = false;
        _blockedAttackerIndex = -1;
        _blockedAt = 0d;
        _blockedSwingExpiresAt = 0;
    }

    private void ClearLocked()
    {
        ClearCounterLocked();
        ClearSuppressionLocked();
    }
}
