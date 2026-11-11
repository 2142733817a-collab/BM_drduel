using System;
using System.Collections.Generic;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A confirmed hostile melee block opens one bounded counter opportunity. The
// mission thread owns all engine reads; OnAIInputSet consumes only snapshots.
internal sealed class AdimiToolsAiArenaParryCounter : AgentComponent
{
    internal const double MinimumDelaySeconds = 0.12d;
    internal const double FeintMinimumDelaySeconds = 0.04d;
    internal const double OpportunitySeconds = 1.8d;
    internal const double RecoveryMarginSeconds = 0.03d;
    // Cap only at the entire opportunity lifetime, never below it: a valid
    // longer Native stun must not become permission to attack prematurely.
    private const float MaximumStunSeconds = (float)OpportunitySeconds;
    private const float UnknownStunSeconds = 0.35f;
    private const long SnapshotMilliseconds = 100;
    private const long GuardSnapshotMilliseconds = 40;
    private const long AttackMilliseconds = 1200;
    private const long OpportunityMilliseconds = 1800;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;
    private const Agent.MovementControlFlag DefenseBits = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock;

    // Native processes one agent's input components serially on its callback
    // thread. A short thread-local record lets the final component restore a
    // removed passive guard if authorization changes before its own callback.
    [ThreadStatic]
    private static AdimiToolsAiArenaParryCounter? _earlyGuardOwner;
    [ThreadStatic]
    private static Agent.MovementControlFlag _earlyRemovedGuard;

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Dictionary<(int Attacker, ulong Signature), (byte Source, double At, long Expires)> _recentCollisions = new();
    private readonly List<(int Attacker, ulong Signature)> _expiredCollisions = new();
    private bool _pending;
    private bool _armed;
    private bool _attackStarted;
    private bool _attackChosen;
    private bool _allowFeint;
    private bool _feintPlanned;
    private bool _feintReady;
    private int _attackerIndex = -1;
    private double _parryAt;
    private double _lastObservedAt;
    private double _earliestAttackAt;
    private long _opportunityExpiresAt;
    private long _snapshotExpiresAt;
    private long _attackExpiresAt;
    private long _guardSnapshotExpiresAt;
    private Agent.MovementControlFlag _attack;
    private Agent.MovementControlFlag _sampledGuard;

    // Retain this separately after the counter is consumed so the same already
    // blocked release cannot reassert custom guard during the next callback.
    private bool _suppressBlockedSwing;
    private int _blockedAttackerIndex = -1;
    private double _blockedAt;
    private long _blockedSwingExpiresAt;

    public AdimiToolsAiArenaParryCounter(Agent agent)
        : this(agent, Stopwatch.GetTimestamp)
    {
    }

    internal AdimiToolsAiArenaParryCounter(Agent agent, Func<long> clock)
        : base(agent)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // The caller must filter a real hostile melee Blocked/Parried collision;
    // chamber attacks, kicks, missiles and unrelated agents are not parries.
    public bool RecordParry(double now, int attackerIndex, float defenderStunSeconds, bool allowFeint = false,
        ulong collisionSignature = 0, byte collisionSource = 0)
    {
        lock (_gate)
        {
            long clock = _clock();
            PruneLocked(clock);
            if (!double.IsFinite(now) || now < 0d || attackerIndex < 0
                || (_pending && now < _lastObservedAt))
            {
                ClearLocked();
                return false;
            }

            PruneRecentCollisionsLocked(now, clock);
            var collisionKey = (attackerIndex, collisionSignature);
            bool identifiableCollision = collisionSignature != 0 && (collisionSource is 1 or 2);
            if (identifiableCollision && _recentCollisions.TryGetValue(collisionKey, out var prior)
                && prior.Source != collisionSource && now >= prior.At && now - prior.At <= 0.25d && clock < prior.Expires)
            {
                // The same collision was delivered by the other engine
                // callback, potentially after the old release ended. Never
                // restart its counter or interrupt a newly started feint.
                return false;
            }

            if (_suppressBlockedSwing && now - _blockedAt >= OpportunitySeconds)
            {
                ClearSuppressionLocked();
            }

            // Multiple collision callbacks for the same still-releasing swing
            // are not additional opportunities and cannot extend deadlines.
            if (_suppressBlockedSwing && attackerIndex == _blockedAttackerIndex)
            {
                return false;
            }

            float boundedStun = float.IsFinite(defenderStunSeconds)
                ? Math.Clamp(defenderStunSeconds, 0f, MaximumStunSeconds)
                : UnknownStunSeconds;
            _pending = true;
            _armed = false;
            _attackStarted = false;
            _attackChosen = false;
            _allowFeint = allowFeint;
            _feintPlanned = false;
            _feintReady = false;
            _attackerIndex = attackerIndex;
            _parryAt = now;
            _lastObservedAt = now;
            double minimumDelay = allowFeint ? FeintMinimumDelaySeconds : MinimumDelaySeconds;
            _earliestAttackAt = now + Math.Max(minimumDelay, boundedStun + RecoveryMarginSeconds);
            _opportunityExpiresAt = AddMilliseconds(clock, OpportunityMilliseconds);
            _snapshotExpiresAt = 0;
            _attackExpiresAt = 0;
            _guardSnapshotExpiresAt = 0;
            _attack = Agent.MovementControlFlag.None;
            _sampledGuard = Agent.MovementControlFlag.None;

            _suppressBlockedSwing = true;
            _blockedAttackerIndex = attackerIndex;
            _blockedAt = now;
            _blockedSwingExpiresAt = _opportunityExpiresAt;
            if (identifiableCollision)
            {
                _recentCollisions[collisionKey] = (collisionSource, now, AddMilliseconds(clock, 250));
                TrimRecentCollisionsLocked();
            }

            return true;
        }
    }

    // eligible includes live, same-arena hostile actor and target, on-foot
    // melee equipment and attack distance. hardStun includes both channels'
    // hit/fall/interruption and DefendParry, but not a normal Defend pose.
    // otherThreat means any new Release, including from the blocked attacker
    // after that swing ended, or a release from another nearby opponent.
    public void Observe(double now, int targetIndex, bool eligible, bool hardStun, bool nativeAttacking,
        bool otherThreat, Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag preferredAttack,
        Agent.MovementControlFlag requestedFeintFirst = Agent.MovementControlFlag.None,
        Agent.UsageDirection observedDirection = Agent.UsageDirection.None, bool observedReady = false,
        bool observedRelease = false, Agent.MovementControlFlag sampledGuard = Agent.MovementControlFlag.None)
    {
        lock (_gate)
        {
            long clock = _clock();
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
            Agent.MovementControlFlag allowed = allowedAttacks & Agent.MovementControlFlag.AttackMask;
            if (allowed == Agent.MovementControlFlag.None
                || (_attackChosen && (allowed & _attack) == Agent.MovementControlFlag.None))
            {
                ClearLocked();
                return;
            }

            if (nativeAttacking && !hardStun)
            {
                // Only a real sampled matching ready after a request may hand
                // off a feint. An input bit alone is never proof of a ready.
                if (!otherThreat && now >= _earliestAttackAt && !observedRelease && observedReady
                    && _feintPlanned && _attackStarted && Direction(observedDirection) == _attack)
                {
                    _armed = false;
                    _feintReady = true;
                    _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
                    _guardSnapshotExpiresAt = 0;
                    return;
                }

                // Respect a different or already released Native attack.
                ClearCounterLocked();
                return;
            }

            if (hardStun || otherThreat || now < _earliestAttackAt)
            {
                _armed = false;
                _feintReady = false;
                _guardSnapshotExpiresAt = 0;
                return;
            }

            if (!_attackChosen)
            {
                bool validFeint = _allowFeint && (requestedFeintFirst is Agent.MovementControlFlag.AttackUp
                    or Agent.MovementControlFlag.AttackDown) && (requestedFeintFirst & allowed) != 0;
                if (validFeint)
                {
                    _attack = requestedFeintFirst;
                    _feintPlanned = true;
                }
                else
                {
                    Agent.MovementControlFlag preferred = preferredAttack & allowed;
                    _attack = ChooseSingle(preferred != Agent.MovementControlFlag.None ? preferred : allowed);
                    _feintPlanned = false;
                }

                _attackChosen = true;
            }

            _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            _sampledGuard = sampledGuard & DefenseBits;
            _guardSnapshotExpiresAt = IsUnambiguousGuard(_sampledGuard)
                ? AddMilliseconds(clock, GuardSnapshotMilliseconds) : 0;
            _armed = true;
        }
    }

    // The mission thread consumes this only after it observed the requested
    // initial Ready, and transfers control to the separate real feint logic.
    public bool TryTakeFeintStart(int targetIndex, out Agent.MovementControlFlag first)
    {
        lock (_gate)
        {
            long clock = _clock();
            PruneLocked(clock);
            first = Agent.MovementControlFlag.None;
            if (!_pending || !_feintReady || targetIndex != _attackerIndex || clock >= _snapshotExpiresAt)
            {
                return false;
            }

            first = _attack;
            ClearCounterLocked();
            return true;
        }
    }

    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                PruneLocked(_clock());
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
                PruneLocked(_clock());
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
            PruneLocked(_clock());
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

    // Invoked before guard-assist components on the native callback thread.
    // It may remove only the exact single ordinary held guard seen in a very
    // recent safe mission-thread sample. Later guard components run normally.
    public void FilterPassiveGuard(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag)
    {
        if (ReferenceEquals(_earlyGuardOwner, this))
        {
            _earlyGuardOwner = null;
            _earlyRemovedGuard = Agent.MovementControlFlag.None;
        }

        lock (_gate)
        {
            long clock = _clock();
            PruneLocked(clock);
            Agent.MovementControlFlag guard = movementFlag & DefenseBits;
            if (!_pending || !_armed || clock >= _snapshotExpiresAt || clock >= _guardSnapshotExpiresAt
                || guard != _sampledGuard || !IsUnambiguousGuard(guard)
                || (eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None
                || (movementFlag & (Agent.MovementControlFlag.Action | Agent.MovementControlFlag.AttackMask)) != 0)
            {
                return;
            }

            _earlyGuardOwner = this;
            _earlyRemovedGuard = guard;
            movementFlag &= ~guard;
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        Agent.MovementControlFlag removedGuard = Agent.MovementControlFlag.None;
        if (ReferenceEquals(_earlyGuardOwner, this))
        {
            removedGuard = _earlyRemovedGuard;
            _earlyGuardOwner = null;
            _earlyRemovedGuard = Agent.MovementControlFlag.None;
        }

        lock (_gate)
        {
            long clock = _clock();
            PruneLocked(clock);
            if (!_pending)
            {
                RestoreGuardWhenUnopposed(ref movementFlag, removedGuard);
                return;
            }

            if ((eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None
                || (movementFlag & Agent.MovementControlFlag.Action) != 0)
            {
                ClearLocked();
                RestoreGuardWhenUnopposed(ref movementFlag, removedGuard);
                return;
            }

            if (!_armed || clock >= _snapshotExpiresAt)
            {
                _armed = false;
                RestoreGuardWhenUnopposed(ref movementFlag, removedGuard);
                return;
            }

            // A guard added by a later reader is a new defense. Never remove
            // or overwrite it in this callback, even if it matches old bits.
            if ((movementFlag & DefenseBits) != Agent.MovementControlFlag.None)
            {
                _armed = false;
                return;
            }

            Agent.MovementControlFlag existingAttack = movementFlag & Agent.MovementControlFlag.AttackMask;
            if (existingAttack != Agent.MovementControlFlag.None)
            {
                // A single existing input is kept as-is, never considered a
                // confirmed action. A different/ambiguous attack waits for a
                // new mission sample instead of changing its direction.
                if (existingAttack == _attack)
                {
                    StartRequestClockLocked(clock);
                }
                else
                {
                    _armed = false;
                }

                return;
            }

            StartRequestClockLocked(clock);
            movementFlag |= _attack;
        }
    }

    private void StartRequestClockLocked(long clock)
    {
        if (!_attackStarted)
        {
            _attackStarted = true;
            _attackExpiresAt = AddMilliseconds(clock, AttackMilliseconds);
        }
    }

    private static void RestoreGuardWhenUnopposed(ref Agent.MovementControlFlag movementFlag,
        Agent.MovementControlFlag removedGuard)
    {
        if ((movementFlag & (DefenseBits | Agent.MovementControlFlag.AttackMask)) == 0)
        {
            movementFlag |= removedGuard;
        }
    }

    private static bool IsUnambiguousGuard(Agent.MovementControlFlag guard)
    {
        if (guard == Agent.MovementControlFlag.None)
        {
            return false;
        }

        uint directions = (uint)(guard & Agent.MovementControlFlag.DefendMask);
        return directions == 0 || (directions & (directions - 1u)) == 0;
    }

    private static Agent.MovementControlFlag Direction(Agent.UsageDirection direction) => direction switch
    {
        Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.AttackUp,
        Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.AttackDown,
        Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.AttackLeft,
        Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.AttackRight,
        _ => Agent.MovementControlFlag.None,
    };

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

    // Kept briefly even across a counter Clear: a hit or target switch must
    // not allow the second callback for the same collision to reopen it.
    private void PruneRecentCollisionsLocked(double now, long clock)
    {
        _expiredCollisions.Clear();
        foreach (var entry in _recentCollisions)
        {
            if (clock >= entry.Value.Expires || now < entry.Value.At || now - entry.Value.At > 0.25d)
            {
                _expiredCollisions.Add(entry.Key);
            }
        }

        foreach (var key in _expiredCollisions)
        {
            _recentCollisions.Remove(key);
        }
    }

    private void TrimRecentCollisionsLocked()
    {
        const int maximumRecentCollisions = 32;
        while (_recentCollisions.Count > maximumRecentCollisions)
        {
            (int Attacker, ulong Signature) oldest = default;
            long oldestExpiry = long.MaxValue;
            foreach (var entry in _recentCollisions)
            {
                if (entry.Value.Expires < oldestExpiry)
                {
                    oldest = entry.Key;
                    oldestExpiry = entry.Value.Expires;
                }
            }

            _recentCollisions.Remove(oldest);
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
        _attackChosen = false;
        _allowFeint = false;
        _feintPlanned = false;
        _feintReady = false;
        _attackerIndex = -1;
        _parryAt = 0d;
        _lastObservedAt = 0d;
        _earliestAttackAt = 0d;
        _opportunityExpiresAt = 0;
        _snapshotExpiresAt = 0;
        _attackExpiresAt = 0;
        _guardSnapshotExpiresAt = 0;
        _attack = Agent.MovementControlFlag.None;
        _sampledGuard = Agent.MovementControlFlag.None;
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
