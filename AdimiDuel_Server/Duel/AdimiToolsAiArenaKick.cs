using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Native still owns the kick animation, collision and damage. The mission
// thread supplies all scene and animation observations, including KickClear;
// the parallel AI-input callback only consumes a short-lived snapshot.
internal sealed class AdimiToolsAiArenaKick : AgentComponent
{
    internal const float MinDistance = 0.25f;
    internal const float MaxDistance = 1.20f;
    internal const float MinFacingDot = 0.75f;
    internal const float MaxHeightDifference = 0.60f;
    internal const double OpportunityIntervalSeconds = 1.2d;
    internal const double KickCooldownSeconds = 3d;
    private const long SnapshotMilliseconds = 100;

    private const Agent.MovementControlFlag ConflictingMovement = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock | Agent.MovementControlFlag.Action;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;

    private readonly object _gate = new();
    private readonly Action<double, int>? _counterKickSent;
    private bool _armed;
    private bool _counterOpportunity;
    private int _target = -1;
    private long _snapshotExpiresAt;
    private long _duplicateSuppressedUntil;
    private double _lastObservedAt;
    private double _nextOpportunityAt;
    private double _cooldownUntil;
    private bool _hasObservation;

    public AdimiToolsAiArenaKick(Agent agent, Action<double, int>? counterKickSent = null) : base(agent)
    {
        _counterKickSent = counterKickSent;
    }

    // Called on the mission thread. eligible covers the living arena infantry,
    // its melee equipment and a valid hostile target. busy covers every state
    // except a confirmed Idle/Guard/Other upper action with stage None, as well
    // as hit reactions, weapon changes and other controlled tactics;
    // enemyAttacking covers nearby releases and kicks. A held ready alone does
    // not prevent an opportunistic kick. The caller
    // obtains kickClear from Agent.KickClear(), never from the AI callback.
    // roll is one independent random sample in [0, 1); it is only evaluated
    // at bounded opportunities, never once per frame as a kick probability.
    public void Observe(double now, int targetIndex, bool eligible, float distance, float facingDot,
        float verticalDistance, bool kickClear, bool busy, bool enemyAttacking, bool counterPending, double roll, double chance,
        bool counterOpportunity = false)
    {
        lock (_gate)
        {
            if (!double.IsFinite(now) || now < 0d)
            {
                Disarm();
                return;
            }

            if (_hasObservation && now < _lastObservedAt)
            {
                // A mission clock reset invalidates the old snapshot. Start a
                // fresh cooldown so recovery cannot turn the reset into spam.
                Disarm();
                _lastObservedAt = now;
                _nextOpportunityAt = now + KickCooldownSeconds;
                _cooldownUntil = _nextOpportunityAt;
                return;
            }

            _hasObservation = true;
            _lastObservedAt = now;
            bool valid = eligible && targetIndex >= 0 && kickClear && !busy && !enemyAttacking && !counterPending
                && float.IsFinite(distance) && distance >= MinDistance && distance <= MaxDistance
                && float.IsFinite(facingDot) && facingDot >= MinFacingDot && facingDot <= 1.001f
                && float.IsFinite(verticalDistance) && Math.Abs(verticalDistance) <= MaxHeightDifference
                && double.IsFinite(roll) && roll >= 0d && roll < 1d
                && double.IsFinite(chance) && chance > 0d && chance <= 1d;
            if (!valid)
            {
                Disarm();
                return;
            }

            long clock = Stopwatch.GetTimestamp();
            if (clock < _duplicateSuppressedUntil) { Disarm(); return; }
            if (_armed && (_target != targetIndex || clock >= _snapshotExpiresAt)) Disarm();
            if (_armed)
            {
                // An armed ordinary kick can serve the same target's counter
                // opportunity; clearing that opportunity restores normal use.
                _counterOpportunity = counterOpportunity;
                return;
            }
            // A missed ordinary random roll only throttles ordinary attempts.
            // A deliberate counter may bypass that throttle, but never the
            // cooldown from a kick that was actually emitted.
            if (now < _cooldownUntil || (!counterOpportunity && now < _nextOpportunityAt)) return;

            // A missed roll or input conflict cannot be retried every frame.
            _nextOpportunityAt = now + OpportunityIntervalSeconds;
            if (!counterOpportunity && roll >= chance) return;

            _target = targetIndex;
            _counterOpportunity = counterOpportunity;
            _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            _armed = true;
        }
    }

    // Invalidating a snapshot must not reset the opportunity/cooldown: an
    // actor briefly changing targets or equipment must not spam kicks.
    public void Clear()
    {
        lock (_gate)
        {
            Disarm();
            _duplicateSuppressedUntil = 0;
        }
    }

    // A hit or parry invalidates our pending kick, but must not remove the
    // short-lived multiplayer lock against duplicate Native kick inputs.
    public void Suspend()
    {
        lock (_gate) Disarm();
    }

    // Only the assigned bot may kick during one opponent's counter window.
    // This short-lived snapshot also suppresses a Native kick input for the
    // other bots while preserving every other input and all kick cooldowns.
    public void PublishDuplicateKickSuppression(bool suppress)
    {
        lock (_gate)
        {
            _duplicateSuppressedUntil = suppress ? AddMilliseconds(Stopwatch.GetTimestamp(), SnapshotMilliseconds) : 0;
            if (suppress) Disarm();
        }
    }

    // A delayed collision can disprove a suspected missed kick after the
    // counter input snapshot was published but before Native consumes it.
    public void CancelCounterOpportunity(int targetIndex)
    {
        lock (_gate)
            if (_armed && _counterOpportunity && _target == targetIndex) Disarm();
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
        ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
    {
        Action<double, int>? notify = null;
        double notifiedAt = 0d;
        int notifiedTarget = -1;
        lock (_gate)
        {
            if (Stopwatch.GetTimestamp() < _duplicateSuppressedUntil)
            {
                eventFlag &= ~Agent.EventControlFlag.Kick;
                Disarm();
                return;
            }
            if (!_armed) return;
            bool nativeCounterKick = _counterOpportunity && (eventFlag & Agent.EventControlFlag.Kick) != 0
                && (eventFlag & ~(CompatibleEvents | Agent.EventControlFlag.Kick)) == Agent.EventControlFlag.None
                && (movementFlag & ConflictingMovement) == 0;
            if (Stopwatch.GetTimestamp() >= _snapshotExpiresAt
                || (!nativeCounterKick && (eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None)
                || (movementFlag & ConflictingMovement) != 0)
            {
                Disarm();
                return;
            }

            Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
            uint attackBits = (uint)attack;
            if (!nativeCounterKick && attackBits != 0 && (attackBits & (attackBits - 1u)) != 0)
            {
                Disarm();
                return;
            }

            // AI inputs are published before this callback. A single fresh
            // Native attack request can yield to a kick from the sampled
            // neutral state; an ambiguous multi-direction request cannot.
            // Native remains responsible for action transitions and hits.
            if (!nativeCounterKick) movementFlag &= ~Agent.MovementControlFlag.AttackMask;
            eventFlag |= Agent.EventControlFlag.Kick;
            _nextOpportunityAt = _lastObservedAt + KickCooldownSeconds;
            _cooldownUntil = _nextOpportunityAt;
            if (_counterOpportunity && _counterKickSent != null)
            {
                notify = _counterKickSent;
                notifiedAt = _lastObservedAt;
                notifiedTarget = _target;
            }
            Disarm();
        }
        // Never call another component while holding this component's lock.
        try { notify?.Invoke(notifiedAt, notifiedTarget); }
        catch (Exception) { /* A failed combo notification must not disrupt Native input. */ }
    }

    private void Disarm()
    {
        _armed = false;
        _counterOpportunity = false;
        _target = -1;
        _snapshotExpiresAt = 0;
    }

    private static long AddMilliseconds(long clock, long milliseconds)
    {
        long duration = Stopwatch.Frequency / 1000 * milliseconds
            + Stopwatch.Frequency % 1000 * milliseconds / 1000;
        return clock > long.MaxValue - duration ? long.MaxValue : clock + duration;
    }
}
