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
    internal const double ConfirmationSeconds = 0.08d;
    internal const double EarlyContactToleranceSeconds = 0.20d;
    internal const double CounterOpportunitySeconds = 1.6d;
    internal const double KickContactSeconds = 0.8d;
    internal const double FollowOpportunitySeconds = 0.6d;
    private const long SnapshotMilliseconds = 100;
    private const long AttackMilliseconds = 200;
    private const int MaximumRecentContacts = 64;
    private const Agent.MovementControlFlag DefenseOrAction = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock | Agent.MovementControlFlag.Action;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;

    private enum Phase { None, ConfirmMiss, CounterKick, AwaitKickContact, FollowAttack }

    private readonly object _gate = new();
    private readonly Dictionary<int, double> _recentEnemyContacts = new();
    private Phase _phase;
    private int _targetIndex = -1;
    private bool _enemyKickObserved;
    private bool _enemyKickContacted;
    private double _enemyKickStartedAt;
    private double _lastObservedAt;
    private bool _hasObservation;
    private double _phaseStartedAt;
    private double _phaseExpiresAt;
    private long _phaseClockExpiresAt;
    private long _snapshotExpiresAt;
    private long _attackClockExpiresAt;
    private bool _armed;
    private bool _attackStarted;
    private Agent.MovementControlFlag _attack;

    public AdimiToolsAiArenaKickPunish(Agent agent) : base(agent) { }

    // eligible must include living hostile participants in the same running
    // arena, on-foot melee equipment and valid target/distance. enemyKicking
    // combines both action channels. ownKicking is intentionally not proof of
    // a hit: only NotifyOwnKickContact can open the follow-up attack window.
    public void Observe(double now, int targetIndex, bool eligible, bool enemyKicking, bool ownKicking,
        bool ownAttack, bool hardStun, bool otherThreat, int difficulty, double roll,
        Agent.MovementControlFlag allowed, Agent.MovementControlFlag preferred)
    {
        lock (_gate)
        {
            if (!double.IsFinite(now) || now < 0d || targetIndex < 0 || !eligible
                || (_hasObservation && now < _lastObservedAt))
            {
                ClearLocked();
                return;
            }

            long clock = Stopwatch.GetTimestamp();
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
                    // A new opposing kick preempts any previous opportunity.
                    ClearPhaseLocked();
                    _enemyKickObserved = true;
                    _enemyKickStartedAt = now;
                    _enemyKickContacted = _recentEnemyContacts.TryGetValue(targetIndex, out double contact)
                        && contact >= now - EarlyContactToleranceSeconds && contact <= now;
                }
            }
            else if (_enemyKickObserved)
            {
                _enemyKickObserved = false;
                if (!_enemyKickContacted)
                {
                    _phase = Phase.ConfirmMiss;
                    _phaseStartedAt = now;
                    _phaseExpiresAt = now + ConfirmationSeconds + CounterOpportunitySeconds;
                    _phaseClockExpiresAt = AddMilliseconds(clock, 1680);
                }
                else ClearPhaseLocked();
            }

            if (_phase == Phase.ConfirmMiss && now >= _phaseStartedAt + ConfirmationSeconds)
            {
                // Take exactly one sample per observed missed kick. Invalid
                // samples and any immediate danger fail closed.
                double chance = difficulty switch { 2 => 0.7d, 3 => 1d, _ => 0.4d };
                if (!double.IsFinite(roll) || roll < 0d || roll >= 1d || roll >= chance
                    || hardStun || otherThreat || ownAttack || ownKicking)
                    ClearPhaseLocked();
                else
                {
                    _phase = Phase.CounterKick;
                    _phaseStartedAt = now;
                    _phaseExpiresAt = now + CounterOpportunitySeconds;
                    _phaseClockExpiresAt = AddMilliseconds(clock, 1600);
                }
            }

            if (_phase is Phase.CounterKick or Phase.AwaitKickContact or Phase.FollowAttack)
            {
                if (hardStun || otherThreat)
                {
                    ClearPhaseLocked();
                    return;
                }

                if (ownAttack)
                {
                    // If Native is already attacking there is no need to
                    // interrupt it with another kick or attack selection.
                    ClearPhaseLocked();
                    return;
                }
            }

            _armed = false;
            if (_phase == Phase.CounterKick)
            {
                _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            }
            else if (_phase == Phase.FollowAttack)
            {
                Agent.MovementControlFlag available = allowed & Agent.MovementControlFlag.AttackMask;
                if (available == Agent.MovementControlFlag.None) return;
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
        if (!double.IsFinite(now) || now < 0d || attackerIndex < 0) return;
        lock (_gate)
        {
            if (!_recentEnemyContacts.TryGetValue(attackerIndex, out double previous) || now > previous)
                _recentEnemyContacts[attackerIndex] = now;
            TrimRecentContactsLocked();

            if (attackerIndex != _targetIndex) return;
            if (_enemyKickObserved && now >= _enemyKickStartedAt - EarlyContactToleranceSeconds)
                _enemyKickContacted = true;
            if (_phase != Phase.None && now >= _enemyKickStartedAt - EarlyContactToleranceSeconds)
                ClearPhaseLocked();
        }
    }

    public bool WantsCounterKick(int targetIndex)
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            if (_phase != Phase.CounterKick || _targetIndex != targetIndex) return false;
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
            long clock = Stopwatch.GetTimestamp();
            if (!double.IsFinite(now) || _phase != Phase.CounterKick || targetIndex != _targetIndex
                || now < _phaseStartedAt || now >= _phaseExpiresAt
                || clock >= _phaseClockExpiresAt || clock >= _snapshotExpiresAt) return;
            _phase = Phase.AwaitKickContact;
            _phaseStartedAt = now;
            _phaseExpiresAt = now + KickContactSeconds;
            _phaseClockExpiresAt = AddMilliseconds(clock, 800);
            _snapshotExpiresAt = 0;
            _armed = false;
        }
    }

    // The caller must confirm this bot's own non-blocked Kick actually hit its
    // current target. An observed KickHit animation alone is not sufficient.
    public void NotifyOwnKickContact(double now, int targetIndex)
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            if (!double.IsFinite(now) || _phase != Phase.AwaitKickContact || targetIndex != _targetIndex
                || now < _phaseStartedAt || now >= _phaseExpiresAt || clock >= _phaseClockExpiresAt) return;
            _phase = Phase.FollowAttack;
            _phaseStartedAt = now;
            _phaseExpiresAt = now + FollowOpportunitySeconds;
            _phaseClockExpiresAt = AddMilliseconds(clock, 600);
            _snapshotExpiresAt = 0;
            _attackClockExpiresAt = 0;
            _armed = false;
            _attackStarted = false;
            _attack = Agent.MovementControlFlag.None;
        }
    }

    public bool HasPendingOrControl
    {
        get
        {
            lock (_gate)
            {
                long clock = Stopwatch.GetTimestamp();
                if ((_phaseClockExpiresAt > 0 && clock >= _phaseClockExpiresAt)
                    || (_attackStarted && clock >= _attackClockExpiresAt))
                    ClearPhaseLocked();
                return _phase is Phase.CounterKick or Phase.AwaitKickContact or Phase.FollowAttack;
            }
        }
    }

    public void Clear()
    {
        lock (_gate) ClearLocked();
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
        ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            if (_phase != Phase.FollowAttack) return;
            if (clock >= _phaseClockExpiresAt || (_attackStarted && clock >= _attackClockExpiresAt))
            {
                ClearPhaseLocked();
                return;
            }
            if (!_armed || clock >= _snapshotExpiresAt) { _armed = false; return; }
            if ((eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None || (movementFlag & DefenseOrAction) != 0)
            {
                // No event or defense is overridden. A fresh safe sample is
                // required before this brief opportunity can be tried again.
                _armed = false;
                return;
            }
            if ((movementFlag & Agent.MovementControlFlag.AttackMask) != 0)
            {
                ClearPhaseLocked();
                return;
            }
            if (!_attackStarted)
            {
                _attackStarted = true;
                _attackClockExpiresAt = AddMilliseconds(clock, AttackMilliseconds);
            }
            movementFlag |= _attack;
        }
    }

    private void ExpirePhaseLocked(double now, long clock)
    {
        if (_phase != Phase.None && (now >= _phaseExpiresAt || clock >= _phaseClockExpiresAt
            || (_attackStarted && clock >= _attackClockExpiresAt))) ClearPhaseLocked();
    }

    private void PruneRecentContactsLocked(double now)
    {
        if (_recentEnemyContacts.Count == 0) return;
        List<int>? expired = null;
        foreach (var pair in _recentEnemyContacts)
            if (pair.Value < now - 2.5d || pair.Value > now + EarlyContactToleranceSeconds)
                (expired ??= new List<int>()).Add(pair.Key);
        if (expired == null) return;
        foreach (int index in expired) _recentEnemyContacts.Remove(index);
    }

    private void TrimRecentContactsLocked()
    {
        if (_recentEnemyContacts.Count <= MaximumRecentContacts) return;
        int oldestIndex = -1;
        double oldest = double.PositiveInfinity;
        foreach (var pair in _recentEnemyContacts)
            if (pair.Value < oldest) { oldest = pair.Value; oldestIndex = pair.Key; }
        if (oldestIndex >= 0) _recentEnemyContacts.Remove(oldestIndex);
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
    }

    private void ClearPhaseLocked()
    {
        _phase = Phase.None;
        _phaseStartedAt = 0d;
        _phaseExpiresAt = 0d;
        _phaseClockExpiresAt = 0;
        _snapshotExpiresAt = 0;
        _attackClockExpiresAt = 0;
        _armed = false;
        _attackStarted = false;
        _attack = Agent.MovementControlFlag.None;
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
