using System;
using System.Collections.Generic;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// The mission thread observes the current opponent and owns all native reads.
// Hit and input callbacks only exchange managed scalar snapshots under a lock.
// A lack of a contact callback is an approximation of a miss, not a native
// assertion that an attack could not have touched terrain or another object.
internal sealed class AdimiToolsAiArenaKickPunish : AgentComponent
{
    internal enum OwnKickContactResult
    {
        None,
        ContactOnly,
        FollowAttack,
    }

    internal const double ConfirmationSeconds = 0.12d;
    internal const double EarlyContactToleranceSeconds = 0.20d;
    internal const double CounterOpportunitySeconds = 1.6d;
    internal const double KickContactSeconds = 1.6d;
    internal const double OwnMissConfirmationSeconds = 0.08d;
    internal const double GuardAfterMissSeconds = 0.8d;
    // Contact can precede the end of the kick animation. Keep the opportunity
    // long enough to request the attack once Native can transition out of it.
    internal const double FollowOpportunitySeconds = 1.4d;
    private const long SnapshotMilliseconds = 100;
    internal const long AttackRequestMilliseconds = 80;
    internal const long AttackGapMilliseconds = 30;
    private const int MaximumRecentContacts = 64;
    private const Agent.MovementControlFlag DefenseOrAction = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock | Agent.MovementControlFlag.Action;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;

    private enum Phase
    {
        None,
        ConfirmMiss,
        CounterKick,
        AwaitKickContact,
        GuardAfterMiss,
        FollowAttack,
    }

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Dictionary<int, double> _recentEnemyContacts = new();
    private Phase _phase;
    private int _targetIndex = -1;
    private bool _enemyKickObserved;
    private bool _enemyKickContacted;
    private double _enemyKickStartedAt;
    private bool _enemyKickEnded;
    private double _enemyKickEndedAt;
    private double _lastObservedAt;
    private bool _hasObservation;
    private double _phaseStartedAt;
    private double _phaseExpiresAt;
    private long _phaseClockExpiresAt;
    private long _snapshotExpiresAt;
    private long _trackingSnapshotExpiresAt;
    private long _attackPulseStartedAt;
    private double _ownKickSentAt;
    private double _ownKickExpiresAt;
    private long _ownKickClockExpiresAt;
    private bool _ownKickObserved;
    private double _ownKickEndedAt;
    private bool _ownKickEnded;
    private int _counterSourceIndex = -1;
    private bool _armed;
    private bool _attackStarted;
    private Agent.MovementControlFlag _attack;

    public AdimiToolsAiArenaKickPunish(Agent agent)
        : this(agent, Stopwatch.GetTimestamp)
    {
    }

    internal AdimiToolsAiArenaKickPunish(Agent agent, Func<long> clock)
        : base(agent)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // eligible must include living hostile participants in the same running
    // arena, on-foot melee equipment and valid target/distance. enemyKicking
    // combines both action channels. ownKicking is intentionally not proof of
    // a hit: only NotifyOwnKickContact can open the follow-up attack window.
    public void Observe(double now, int targetIndex, bool eligible, bool enemyKicking, bool ownKicking,
        bool ownAttack, bool hardStun, bool otherThreat, int difficulty, double roll,
        Agent.MovementControlFlag allowed, Agent.MovementControlFlag preferred, bool cancelableOwnReady = false,
        bool confirmedMissRecovery = false)
    {
        lock (_gate)
        {
            if (!double.IsFinite(now) || now < 0d || targetIndex < 0 || !eligible
                || (_hasObservation && now < _lastObservedAt))
            {
                ClearLocked();
                return;
            }

            long clock = _clock();
            if (_targetIndex != targetIndex)
            {
                ResetKickTrackingLocked();
                ClearPhaseLocked();
                _targetIndex = targetIndex;
            }

            _lastObservedAt = now;
            _hasObservation = true;
            PruneRecentContactsLocked(now);
            ExpirePhaseLocked(now, clock);

            if (enemyKicking)
            {
                if (!_enemyKickObserved)
                {
                    // Action channels can briefly leave and resume the same
                    // kick. Keep that kick's contact history across the gap.
                    bool resumed = _enemyKickEnded && now <= _enemyKickEndedAt + ConfirmationSeconds;
                    ClearPhaseLocked();
                    _enemyKickObserved = true;
                    _enemyKickEnded = false;
                    if (!resumed)
                    {
                        _enemyKickStartedAt = now;
                        _enemyKickContacted = _recentEnemyContacts.TryGetValue(targetIndex, out double contact)
                            && contact >= now - EarlyContactToleranceSeconds && contact <= now;
                    }
                }
            }
            else if (_enemyKickObserved)
            {
                _enemyKickObserved = false;
                _enemyKickEnded = true;
                _enemyKickEndedAt = now;
                if (!_enemyKickContacted)
                {
                    _phase = Phase.ConfirmMiss;
                    _phaseStartedAt = now;
                    _phaseExpiresAt = now + ConfirmationSeconds + CounterOpportunitySeconds;
                    _phaseClockExpiresAt = AddMilliseconds(clock, 1720);
                }
                else
                {
                    ClearPhaseLocked();
                }
            }

            // While an observed kick is still in progress, a separate short
            // snapshot may permit positioning. This never confirms a miss or
            // grants a kick; the mission thread must still check geometry,
            // obstacles and all other threats before publishing movement.
            _trackingSnapshotExpiresAt = enemyKicking && _enemyKickObserved && !_enemyKickContacted
                && !hardStun && !ownKicking && (!ownAttack || cancelableOwnReady)
                ? AddMilliseconds(clock, SnapshotMilliseconds) : 0;

            if (_phase == Phase.ConfirmMiss && !enemyKicking && !_enemyKickContacted
                && (confirmedMissRecovery || now >= _phaseStartedAt + ConfirmationSeconds)
                && !hardStun && !otherThreat)
            {
                // Take exactly one sample per observed missed kick. Invalid
                // samples fail closed; a threat temporarily holds the window.
                double chance = difficulty switch { 2 => 0.95d, 3 => 1d, _ => 0.8d };
                if (!double.IsFinite(roll) || roll < 0d || roll >= 1d || roll >= chance)
                {
                    ClearPhaseLocked();
                }
                else
                {
                    _phase = Phase.CounterKick;
                    _phaseStartedAt = now;
                }
            }

            if (_phase is Phase.CounterKick or Phase.AwaitKickContact or Phase.GuardAfterMiss or Phase.FollowAttack)
            {
                // An incoming attack must still cancel our offensive input,
                // but not the observation needed to defend after a missed kick.
                if (hardStun || (otherThreat && (_phase is Phase.CounterKick or Phase.FollowAttack)))
                {
                    ClearPhaseLocked();
                    return;
                }

                if (_phase == Phase.CounterKick && ((ownAttack && !cancelableOwnReady) || ownKicking))
                {
                    // The player has already missed. A Native attack/kick
                    // that is still finishing should not erase the entire
                    // bounded opportunity, nor should we interrupt it. Do
                    // not publish an approach/kick until a fresh safe sample
                    // observes that action end; the original deadline remains.
                    _snapshotExpiresAt = 0;
                    _armed = false;
                    return;
                }

                if (_phase == Phase.FollowAttack && ownAttack)
                {
                    // If Native is already attacking there is no need to
                    // interrupt it with another kick or attack selection.
                    ClearPhaseLocked();
                    return;
                }
            }

            _armed = false;
            if (_phase == Phase.ConfirmMiss)
            {
                // This only permits a tentative approach; it never permits a
                // kick. Unsafe or stale samples revoke it immediately.
                _snapshotExpiresAt = !hardStun && !otherThreat && !ownKicking
                    && (!ownAttack || cancelableOwnReady)
                    ? AddMilliseconds(clock, SnapshotMilliseconds) : 0;
            }
            if (_phase == Phase.AwaitKickContact)
            {
                if (ownKicking)
                {
                    _ownKickObserved = true;
                    _ownKickEnded = false;
                }
                else if (_ownKickObserved)
                {
                    if (!_ownKickEnded)
                    {
                        _ownKickEnded = true;
                        _ownKickEndedAt = now;
                    }
                    else if (now >= _ownKickEndedAt + OwnMissConfirmationSeconds)
                    {
                        _phase = Phase.GuardAfterMiss;
                        _phaseStartedAt = now;
                        _phaseExpiresAt = now + GuardAfterMissSeconds;
                        _phaseClockExpiresAt = AddMilliseconds(clock, (long)(GuardAfterMissSeconds * 1000d));
                    }
                }

                // Lets the caller cache an incoming attack while the kick is
                // underway without publishing a defense before it ends.
                _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            }
            else if (_phase == Phase.GuardAfterMiss)
            {
                if (ownKicking)
                {
                    // A short gap between kick animation phases is not an end.
                    _phase = Phase.AwaitKickContact;
                    _phaseStartedAt = _ownKickSentAt;
                    _phaseExpiresAt = _ownKickExpiresAt;
                    _phaseClockExpiresAt = _ownKickClockExpiresAt;
                    _ownKickEnded = false;
                }

                _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            }

            if (_phase == Phase.CounterKick)
            {
                _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            }
            else if (_phase == Phase.FollowAttack)
            {
                Agent.MovementControlFlag available = allowed & Agent.MovementControlFlag.AttackMask;
                if (available == Agent.MovementControlFlag.None)
                {
                    return;
                }

                if (_attackStarted && (available & _attack) == Agent.MovementControlFlag.None)
                {
                    ClearPhaseLocked();
                    return;
                }

                if (!_attackStarted)
                {
                    Agent.MovementControlFlag wanted = preferred & available;
                    _attack = ChooseSingle(wanted != Agent.MovementControlFlag.None ? wanted : available);
                }

                _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
                _armed = true;
            }
        }
    }

    // The caller must filter same-arena player kick contacts, including blocked
    // or zero-damage contacts against any participant, before calling this.
    public void NotifyEnemyKickContact(double now, int attackerIndex)
    {
        if (!double.IsFinite(now) || now < 0d || attackerIndex < 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_recentEnemyContacts.TryGetValue(attackerIndex, out double previous) || now > previous)
            {
                _recentEnemyContacts[attackerIndex] = now;
            }

            TrimRecentContactsLocked();

            bool originalCounterSource = attackerIndex == _counterSourceIndex
                && _phase is Phase.AwaitKickContact or Phase.GuardAfterMiss or Phase.FollowAttack;
            if (attackerIndex != _targetIndex && !originalCounterSource)
            {
                return;
            }

            if (attackerIndex == _targetIndex)
            {
                _trackingSnapshotExpiresAt = 0;
            }

            if ((_enemyKickObserved || _enemyKickEnded && now <= _enemyKickEndedAt + ConfirmationSeconds)
                && now >= _enemyKickStartedAt - EarlyContactToleranceSeconds)
            {
                _enemyKickContacted = true;
            }

            if (_phase != Phase.None && now >= _enemyKickStartedAt - EarlyContactToleranceSeconds)
            {
                ClearPhaseLocked();
            }
        }
    }

    // An unconfirmed miss is only an opportunity to close distance. This is
    // never permission to kick; a fresh safe observation is always required.
    public bool WantsCounterApproach(int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            return _phase == Phase.ConfirmMiss && _targetIndex == targetIndex
                && _enemyKickEnded && !_enemyKickContacted
                && clock < _phaseClockExpiresAt && clock < _snapshotExpiresAt;
        }
    }

    // Fresh authorization to prepare position while the assigned opponent's
    // kick is still underway. Caller must check KickHit, threats, geometry and
    // path safety; this is deliberately separate from counter-kick permission.
    public bool WantsTrackActiveKick(int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            return _targetIndex == targetIndex && _enemyKickObserved && !_enemyKickContacted
                && _trackingSnapshotExpiresAt != 0 && clock < _trackingSnapshotExpiresAt;
        }
    }

    // Only for withdrawing a previously published movement snapshot on a
    // collision. The positioning snapshot can expire just before a newer
    // footwork snapshot, so this observation deliberately has no TTL and
    // must never be used to authorize movement or attacks.
    public bool HasObservedActiveKick(int targetIndex)
    {
        lock (_gate)
        {
            return _targetIndex == targetIndex && _enemyKickObserved && !_enemyKickContacted;
        }
    }

    public bool WantsCounterKick(int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            if (_phase != Phase.CounterKick || _targetIndex != targetIndex)
            {
                return false;
            }

            if (clock >= _phaseClockExpiresAt)
            {
                ClearPhaseLocked();
                return false;
            }

            return clock < _snapshotExpiresAt;
        }
    }

    // Called only after the existing kick controller actually emits its event.
    public void NotifyCounterKickSent(double now, int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            if (!double.IsFinite(now) || _phase != Phase.CounterKick || targetIndex != _targetIndex
                || now < _phaseStartedAt || now >= _phaseExpiresAt
                || clock >= _phaseClockExpiresAt || clock >= _snapshotExpiresAt)
            {
                return;
            }

            _phase = Phase.AwaitKickContact;
            _phaseStartedAt = now;
            _phaseExpiresAt = now + KickContactSeconds;
            _phaseClockExpiresAt = AddMilliseconds(clock, (long)(KickContactSeconds * 1000d));
            _ownKickSentAt = now;
            _counterSourceIndex = _targetIndex;
            _ownKickExpiresAt = _phaseExpiresAt;
            _ownKickClockExpiresAt = _phaseClockExpiresAt;
            _ownKickObserved = false;
            _ownKickEnded = false;
            _ownKickEndedAt = 0d;
            _snapshotExpiresAt = 0;
            _armed = false;
        }
    }

    // The caller must confirm this bot's own non-blocked Kick actually hit its
    // current target. An observed KickHit animation alone is not sufficient.
    public bool NotifyOwnKickContact(double now, int targetIndex)
    {
        lock (_gate)
        {
            // Preserve the existing contract for callers that only confirm
            // contact with the originally selected target.
            if (targetIndex != _targetIndex)
            {
                return false;
            }

            return NotifyOwnKickCollisionLocked(now, targetIndex, canFollow: true, out _) == OwnKickContactResult.FollowAttack;
        }
    }

    // The caller must establish that this is a real collision with a person.
    // It alone decides whether the victim is an unblocked, living hostile
    // player in the same arena; callbacks here never read Native agent state.
    // Even an ineligible or blocked person is contact, not a missed kick.
    public OwnKickContactResult NotifyOwnKickCollision(double now, int victimIndex, bool canFollow, out int originalTargetIndex)
    {
        lock (_gate)
        {
            return NotifyOwnKickCollisionLocked(now, victimIndex, canFollow, out originalTargetIndex);
        }
    }

    // Used on the mission thread before ordinary targeting can clear a new
    // follow-up aimed at a different, confirmed victim.
    public int GetPendingFollowTargetIndex(double now)
    {
        lock (_gate)
        {
            if (_phase != Phase.FollowAttack)
            {
                return -1;
            }

            long clock = _clock();
            if (!double.IsFinite(now) || now < _phaseStartedAt || now >= _phaseExpiresAt
                || clock >= _phaseClockExpiresAt)
            {
                ClearPhaseLocked();
                return -1;
            }

            return _targetIndex;
        }
    }

    // A sent counter-kick remains tied to its original opponent until its
    // hit/miss is resolved. This lets the mission thread keep ordinary target
    // redistribution from erasing a valid in-flight kick observation.
    public int GetPendingKickTargetIndex(double now)
    {
        lock (_gate)
        {
            if (_phase != Phase.AwaitKickContact)
            {
                return -1;
            }

            long clock = _clock();
            if (!double.IsFinite(now) || now < _ownKickSentAt || now >= _phaseExpiresAt || clock >= _phaseClockExpiresAt)
            {
                ClearPhaseLocked();
                return -1;
            }

            return _counterSourceIndex;
        }
    }

    // Read-only opportunity for main-thread threat observation. A separate
    // WantsGuardAfterMiss check is required before publishing defense input.
    public bool CanObserveGuardAfterKick(int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            return _targetIndex == targetIndex && _ownKickObserved
                && (_phase is Phase.AwaitKickContact or Phase.GuardAfterMiss)
                && clock < _phaseClockExpiresAt && clock < _snapshotExpiresAt;
        }
    }

    // This is deliberately not part of HasPendingOrControl: a guard window must
    // not extend the multiplayer kick assignment or authorize another kick.
    public bool WantsGuardAfterMiss(int targetIndex)
    {
        lock (_gate)
        {
            long clock = _clock();
            return _phase == Phase.GuardAfterMiss && _targetIndex == targetIndex
                && clock < _phaseClockExpiresAt && clock < _snapshotExpiresAt;
        }
    }

    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                long clock = _clock();
                if (_phaseClockExpiresAt > 0 && clock >= _phaseClockExpiresAt)
                {
                    ClearPhaseLocked();
                }

                return _phase is Phase.ConfirmMiss or Phase.CounterKick or Phase.AwaitKickContact or Phase.FollowAttack;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearLocked();
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
        ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
    {
        lock (_gate)
        {
            long clock = _clock();
            if (_phase != Phase.FollowAttack)
            {
                return;
            }

            if (clock >= _phaseClockExpiresAt)
            {
                ClearPhaseLocked();
                return;
            }

            if (!_armed || clock >= _snapshotExpiresAt)
            {
                _armed = false;
                return;
            }

            if ((eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None || (movementFlag & DefenseOrAction) != 0)
            {
                // No event or defense is overridden. A fresh safe sample is
                // required before this brief opportunity can be tried again.
                _armed = false;
                return;
            }

            if ((movementFlag & Agent.MovementControlFlag.AttackMask) != 0)
            {
                // Preserve Native's one existing direction. Its input flag is
                // not proof that Native accepted or began the attack.
                return;
            }

            if (!_attackStarted)
            {
                _attackStarted = true;
                _attackPulseStartedAt = clock;
            }

            long period = AddMilliseconds(0, AttackRequestMilliseconds + AttackGapMilliseconds);
            long onDuration = AddMilliseconds(0, AttackRequestMilliseconds);
            if (clock >= _attackPulseStartedAt && (clock - _attackPulseStartedAt) % period < onDuration)
            {
                movementFlag |= _attack;
            }
        }
    }

    private OwnKickContactResult NotifyOwnKickCollisionLocked(double now, int victimIndex, bool canFollow, out int originalTargetIndex)
    {
        originalTargetIndex = -1;
        long clock = _clock();
        if (!double.IsFinite(now) || victimIndex < 0 || _phase is not (Phase.AwaitKickContact or Phase.GuardAfterMiss)
            || now < _ownKickSentAt || now >= _phaseExpiresAt || clock >= _phaseClockExpiresAt)
        {
            return OwnKickContactResult.None;
        }

        originalTargetIndex = _counterSourceIndex;
        if (!canFollow)
        {
            ClearPhaseLocked();
            return OwnKickContactResult.ContactOnly;
        }

        _targetIndex = victimIndex;
        _phase = Phase.FollowAttack;
        _phaseStartedAt = now;
        _phaseExpiresAt = now + FollowOpportunitySeconds;
        _phaseClockExpiresAt = AddMilliseconds(clock, (long)(FollowOpportunitySeconds * 1000d));
        _snapshotExpiresAt = 0;
        _attackPulseStartedAt = 0;
        _armed = false;
        _attackStarted = false;
        _attack = Agent.MovementControlFlag.None;
        return OwnKickContactResult.FollowAttack;
    }

    private void ExpirePhaseLocked(double now, long clock)
    {
        if (_phase != Phase.None && (now >= _phaseExpiresAt || clock >= _phaseClockExpiresAt))
        {
            ClearPhaseLocked();
        }
    }

    private void PruneRecentContactsLocked(double now)
    {
        if (_recentEnemyContacts.Count == 0)
        {
            return;
        }

        List<int>? expired = null;
        foreach (var pair in _recentEnemyContacts)
        {
            if (pair.Value < now - 2.5d || pair.Value > now + EarlyContactToleranceSeconds)
            {
                (expired ??= new List<int>()).Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (int index in expired)
        {
            _recentEnemyContacts.Remove(index);
        }
    }

    private void TrimRecentContactsLocked()
    {
        if (_recentEnemyContacts.Count <= MaximumRecentContacts)
        {
            return;
        }

        int oldestIndex = -1;
        double oldest = double.PositiveInfinity;
        foreach (var pair in _recentEnemyContacts)
        {
            if (pair.Value < oldest)
            {
                oldest = pair.Value;
                oldestIndex = pair.Key;
            }
        }

        if (oldestIndex >= 0)
        {
            _recentEnemyContacts.Remove(oldestIndex);
        }
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

    private void ResetKickTrackingLocked()
    {
        _enemyKickObserved = false;
        _enemyKickContacted = false;
        _enemyKickStartedAt = 0d;
        _enemyKickEnded = false;
        _enemyKickEndedAt = 0d;
        _trackingSnapshotExpiresAt = 0;
    }

    private void ClearPhaseLocked()
    {
        _phase = Phase.None;
        _phaseStartedAt = 0d;
        _phaseExpiresAt = 0d;
        _phaseClockExpiresAt = 0;
        _snapshotExpiresAt = 0;
        _attackPulseStartedAt = 0;
        _armed = false;
        _attackStarted = false;
        _attack = Agent.MovementControlFlag.None;
        _ownKickSentAt = 0d;
        _ownKickExpiresAt = 0d;
        _ownKickClockExpiresAt = 0;
        _ownKickObserved = false;
        _ownKickEndedAt = 0d;
        _ownKickEnded = false;
        _counterSourceIndex = -1;
    }

    private void ClearLocked()
    {
        ClearPhaseLocked();
        ResetKickTrackingLocked();
        _recentEnemyContacts.Clear();
        _targetIndex = -1;
        _lastObservedAt = 0d;
        _hasObservation = false;
    }
}
