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
    internal const double KickCooldownSeconds = 2.4d;
    // The response remains bounded across distinct missed-kick episodes.
    internal const double CounterKickCooldownSeconds = 1.4d;
    internal const double SignalWindowSeconds = 0.15d;
    internal const double StartTimeoutSeconds = 0.65d;
    internal const double ProtectionTimeoutSeconds = 1.2d;
    private const long SnapshotMilliseconds = 100;
    private const long GuardCancellationSnapshotMilliseconds = 30;
    private const long ScopeSnapshotMilliseconds = 200;
    private const long EarlyFilterSnapshotMilliseconds = 100;

    private const Agent.MovementControlFlag DefenseBits = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock;
    private const Agent.MovementControlFlag ConflictingMovement = DefenseBits | Agent.MovementControlFlag.Action;
    private const Agent.EventControlFlag CompatibleEvents = Agent.EventControlFlag.Walk | Agent.EventControlFlag.Run;
    private const Agent.MovementControlFlag TranslationMask = Agent.MovementControlFlag.Forward
        | Agent.MovementControlFlag.Backward | Agent.MovementControlFlag.StrafeLeft | Agent.MovementControlFlag.StrafeRight;

    // Native invokes the components for one AI input serially on its callback
    // thread. Thread-local data prevents different agents and concurrent
    // callbacks from sharing a native-kick or passive-guard decision.
    [ThreadStatic]
    private static AdimiToolsAiArenaKick? _earlyFilterOwner;
    [ThreadStatic]
    private static bool _earlyFilterSawKick;
    [ThreadStatic]
    private static Agent.MovementControlFlag _earlyFilterDefense;
    [ThreadStatic]
    private static long _earlyFilterExpiresAt;

    private readonly object _gate = new();
    private readonly Action<double, int>? _counterKickSent;
    private bool _earlyFilterBound;
    private long _scopeExpiresAt;
    private bool _armed;
    private bool _counterOpportunity;
    private int _target = -1;
    private long _snapshotExpiresAt;
    private long _guardCancellationExpiresAt;
    private long _duplicateSuppressedUntil;
    private double _lastObservedAt;
    private double _counterCooldownUntil;
    private bool _hasObservation;
    // Once sent, a kick needs a bounded startup/commit period. All engine
    // reads remain with the mission-thread caller; Observe and the native AI
    // callback consume only scalar snapshots under the same lock.
    private bool _protecting;
    private bool _activeCounter;
    private bool _observedKick;
    private int _activeTarget = -1;
    private double _issuedAt;
    private long _protectionExpiresAt;
    private long _signalExpiresAt;
    private long _protectionSnapshotExpiresAt;
    private long _sampleVersion;
    private long _lastSignalVersion;

    public AdimiToolsAiArenaKick(Agent agent, Action<double, int>? counterKickSent = null)
        : base(agent)
    {
        _counterKickSent = counterKickSent;
    }

    // The early component is attached before any guard/attack component and
    // is bound only after this component has been constructed. No engine state
    // is inspected from either method.
    public void BindEarlyFilter()
    {
        lock (_gate)
        {
            _earlyFilterBound = true;
        }
    }

    public void FilterNativeKick(ref Agent.EventControlFlag eventFlag, Agent.MovementControlFlag movementFlag)
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            if (!_earlyFilterBound || clock >= _scopeExpiresAt)
            {
                if (ReferenceEquals(_earlyFilterOwner, this))
                {
                    ClearEarlyFilterContext();
                }

                return;
            }

            _earlyFilterOwner = this;
            _earlyFilterSawKick = (eventFlag & Agent.EventControlFlag.Kick) != 0;
            _earlyFilterDefense = movementFlag & DefenseBits;
            _earlyFilterExpiresAt = AddMilliseconds(clock, EarlyFilterSnapshotMilliseconds);
            eventFlag &= ~Agent.EventControlFlag.Kick;
        }
    }

    // Called on the mission thread for this arena's living on-foot infantry.
    // A bounded managed snapshot keeps callback threads from reading Native
    // agent state and releases input if the mission stops publishing scope.
    public void SetCounterOnlyScope(bool enabled)
    {
        lock (_gate)
        {
            _scopeExpiresAt = enabled ? AddMilliseconds(Stopwatch.GetTimestamp(), ScopeSnapshotMilliseconds) : 0;
            if (!enabled)
            {
                Disarm();
                EndProtection();
                _duplicateSuppressedUntil = 0;
            }
        }
    }

    // The later footwork callback must yield to a fresh kick protection even
    // after the brief Kick event has ended. No engine reads occur here.
    public bool HasFreshProtection()
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            return clock < _scopeExpiresAt && _protecting && clock < _protectionSnapshotExpiresAt && clock < _protectionExpiresAt;
        }
    }

    // Initiative must also yield to an armed kick, before the native input
    // callback emits its event and starts the existing protection window.
    public bool HasFreshOpportunityOrProtection()
    {
        lock (_gate)
        {
            long clock = Stopwatch.GetTimestamp();
            return clock < _scopeExpiresAt && ((_armed && clock < _snapshotExpiresAt && clock >= _duplicateSuppressedUntil)
                || (_protecting && clock < _protectionSnapshotExpiresAt && clock < _protectionExpiresAt));
        }
    }

    // Called on the mission thread. eligible covers the living arena infantry,
    // its melee equipment and a valid hostile target. busy covers every state
    // except a confirmed Idle/Guard/Other upper action with stage None, as well
    // as hit reactions, weapon changes and other controlled tactics;
    // enemyAttacking covers nearby releases and kicks. The caller obtains
    // kickClear from Agent.KickClear(), never from the AI callback. The caller
    // authorizes counterOpportunity only for a tracked, eligible missed kick.
    public void Observe(double now, int targetIndex, bool eligible, float distance, float facingDot,
        float verticalDistance, bool kickClear, bool busy, bool enemyAttacking, bool counterPending, double roll, double chance,
        bool counterOpportunity = false, Agent.ActionCodeType? ownUpper = null, Agent.ActionCodeType? ownLower = null,
        bool? ownStruck = null, bool allowGuardCancellation = false)
    {
        lock (_gate)
        {
            if (!double.IsFinite(now) || now < 0d)
            {
                Disarm();
                EndProtection();
                return;
            }

            if (_hasObservation && now < _lastObservedAt)
            {
                // A mission clock reset invalidates the old snapshot. Start a
                // fresh cooldown so recovery cannot turn the reset into spam.
                Disarm();
                EndProtection();
                _lastObservedAt = now;
                _counterCooldownUntil = now + KickCooldownSeconds;
                return;
            }

            _hasObservation = true;
            _lastObservedAt = now;
            long clock = Stopwatch.GetTimestamp();
            if (_protecting)
            {
                // A committed kick is naturally busy and KickClear is no
                // longer meaningful; do not mistake this for invalidation.
                bool validActive = eligible && targetIndex == _activeTarget && !enemyAttacking
                    && float.IsFinite(distance) && float.IsFinite(facingDot) && float.IsFinite(verticalDistance)
                    && double.IsFinite(chance) && chance > 0d && chance <= 1d
                    && (_activeCounter || !counterPending) && clock >= _duplicateSuppressedUntil
                    && clock < _protectionExpiresAt && now - _issuedAt <= ProtectionTimeoutSeconds;
                if (!validActive)
                {
                    EndProtection();
                }
                else
                {
                    // Caller supplies one main-thread snapshot. Missing or
                    // invalid samples fail open to Native, never a stale hold.
                    if (!ownUpper.HasValue || !ownLower.HasValue || !ownStruck.HasValue
                        || !Enum.IsDefined(typeof(Agent.ActionCodeType), ownUpper.Value)
                        || !Enum.IsDefined(typeof(Agent.ActionCodeType), ownLower.Value))
                    {
                        EndProtection();
                    }
                    else
                    {
                        UpdateOwnAction(now, ownUpper.Value, ownLower.Value, ownStruck.Value);
                    }

                    if (_protecting)
                    {
                        if (!_observedKick && now - _issuedAt > StartTimeoutSeconds)
                        {
                            EndProtection();
                        }
                        else
                        {
                            _protectionSnapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
                            _sampleVersion++;
                            return;
                        }
                    }
                }
            }

            bool valid = counterOpportunity && eligible && targetIndex >= 0 && kickClear && !busy && !enemyAttacking && !counterPending
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

            if (clock < _duplicateSuppressedUntil)
            {
                Disarm();
                return;
            }

            if (_armed && _target != targetIndex)
            {
                Disarm();
                return;
            }

            if (_armed && clock >= _snapshotExpiresAt)
            {
                Disarm();
            }

            if (_armed)
            {
                _guardCancellationExpiresAt = allowGuardCancellation
                    ? AddMilliseconds(clock, GuardCancellationSnapshotMilliseconds) : 0;
                return;
            }

            if (now < _counterCooldownUntil)
            {
                return;
            }

            _target = targetIndex;
            _counterOpportunity = true;
            _snapshotExpiresAt = AddMilliseconds(clock, SnapshotMilliseconds);
            _guardCancellationExpiresAt = allowGuardCancellation
                ? AddMilliseconds(clock, GuardCancellationSnapshotMilliseconds) : 0;
            _sampleVersion++;
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
            EndProtection();
            _duplicateSuppressedUntil = 0;
        }
    }

    // A hit or parry invalidates our pending kick, but must not remove the
    // short-lived multiplayer lock against duplicate Native kick inputs.
    public void Suspend()
    {
        lock (_gate)
        {
            Disarm();
            EndProtection();
        }
    }

    // Only the assigned bot may kick during one opponent's counter window.
    // This short-lived snapshot also suppresses a Native kick input for the
    // other bots while preserving every other input and all kick cooldowns.
    public void PublishDuplicateKickSuppression(bool suppress)
    {
        lock (_gate)
        {
            _duplicateSuppressedUntil = suppress ? AddMilliseconds(Stopwatch.GetTimestamp(), SnapshotMilliseconds) : 0;
            if (suppress)
            {
                Disarm();
                EndProtection();
            }
        }
    }

    // A delayed collision can disprove a suspected missed kick after the
    // counter input snapshot was published but before Native consumes it.
    public void CancelCounterOpportunity(int targetIndex)
    {
        lock (_gate)
        {
            if (_armed && _counterOpportunity && _target == targetIndex)
            {
                Disarm();
            }

            if (_protecting && _activeCounter && _activeTarget == targetIndex)
            {
                EndProtection();
            }
        }
    }

    // Called only after the separate counter-punish component has confirmed
    // this bot's kick against its current target and contact window. That
    // contact itself confirms the kick even before the next animation sample.
    public void NotifyCounterKickContact(double now, int targetIndex)
    {
        lock (_gate)
        {
            if (!_protecting || !_activeCounter || targetIndex != _activeTarget || !double.IsFinite(now)
                || now < _issuedAt || now > _issuedAt + ProtectionTimeoutSeconds
                || Stopwatch.GetTimestamp() >= _protectionExpiresAt)
            {
                return;
            }

            EndProtection();
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
        ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
    {
        Action<double, int>? notify = null;
        double notifiedAt = 0d;
        int notifiedTarget = -1;
        lock (_gate)
        {
            long inputTimestamp = Stopwatch.GetTimestamp();
            bool matchingFilter = ReferenceEquals(_earlyFilterOwner, this);
            bool freshFilter = matchingFilter && inputTimestamp < _earlyFilterExpiresAt;
            bool filteredKick = freshFilter && _earlyFilterSawKick;
            Agent.MovementControlFlag filteredDefense = freshFilter ? _earlyFilterDefense : Agent.MovementControlFlag.None;
            if (matchingFilter)
            {
                ClearEarlyFilterContext();
            }

            if (inputTimestamp >= _scopeExpiresAt)
            {
                Disarm();
                EndProtection();
                return;
            }

            // Scope is published only for eligible arena infantry. Native's
            // unrelated kick input is never authorized; the valid counter
            // path below may add its own signal.
            bool nativeKickRequested = (eventFlag & Agent.EventControlFlag.Kick) != 0 || filteredKick
                || (_earlyFilterBound && !freshFilter);
            eventFlag &= ~Agent.EventControlFlag.Kick;
            if (Stopwatch.GetTimestamp() < _duplicateSuppressedUntil)
            {
                Disarm();
                EndProtection();
                return;
            }

            if (_protecting)
            {
                long clock = Stopwatch.GetTimestamp();
                if (clock >= _protectionSnapshotExpiresAt || clock >= _protectionExpiresAt)
                {
                    EndProtection();
                    return;
                }

                if ((eventFlag & ~(CompatibleEvents | Agent.EventControlFlag.Kick)) != Agent.EventControlFlag.None
                    || (movementFlag & ConflictingMovement) != 0)
                {
                    EndProtection();
                    return;
                }

                movementFlag &= ~(Agent.MovementControlFlag.AttackMask | TranslationMask);
                inputVector = new Vec2(0f, 0f);
                if (_sampleVersion != _lastSignalVersion && _lastObservedAt - _issuedAt <= SignalWindowSeconds && clock < _signalExpiresAt)
                {
                    eventFlag |= Agent.EventControlFlag.Kick;
                    _lastSignalVersion = _sampleVersion;
                }

                return;
            }

            if (!_armed)
            {
                return;
            }

            long inputClock = Stopwatch.GetTimestamp();
            Agent.MovementControlFlag defense = movementFlag & DefenseBits;
            uint defenseDirection = (uint)(defense & Agent.MovementControlFlag.DefendMask);
            // The caller authorizes this only for a sampled, ordinary guard
            // in a safe kick opportunity with no active threat.
            // Ambiguous defense/attack inputs and special events always win.
            bool defenseChangedAfterFilter = _earlyFilterBound && (!freshFilter || filteredDefense != defense);
            bool cancelPassiveGuard = !nativeKickRequested && !defenseChangedAfterFilter && inputClock < _guardCancellationExpiresAt
                && defense != Agent.MovementControlFlag.None
                && (defenseDirection == 0 || (defenseDirection & (defenseDirection - 1u)) == 0)
                && (movementFlag & (Agent.MovementControlFlag.Action | Agent.MovementControlFlag.AttackMask)) == 0
                && (eventFlag & ~CompatibleEvents) == Agent.EventControlFlag.None;
            if (inputClock >= _snapshotExpiresAt
                || (eventFlag & ~CompatibleEvents) != Agent.EventControlFlag.None
                || (movementFlag & Agent.MovementControlFlag.Action) != 0
                || (defense != Agent.MovementControlFlag.None && !cancelPassiveGuard))
            {
                Disarm();
                return;
            }

            Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
            uint attackBits = (uint)attack;
            if (attackBits != 0 && (attackBits & (attackBits - 1u)) != 0)
            {
                Disarm();
                return;
            }

            // AI inputs are published before this callback. A single fresh
            // Native attack request can yield to a kick from the sampled
            // neutral state; an ambiguous multi-direction request cannot.
            // Native remains responsible for action transitions and hits.
            if (cancelPassiveGuard)
            {
                movementFlag &= ~DefenseBits;
            }

            movementFlag &= ~Agent.MovementControlFlag.AttackMask;

            eventFlag |= Agent.EventControlFlag.Kick;
            _counterCooldownUntil = _lastObservedAt + CounterKickCooldownSeconds;
            long issuedClock = Stopwatch.GetTimestamp();
            _protecting = true;
            _activeCounter = _counterOpportunity;
            _activeTarget = _target;
            _issuedAt = _lastObservedAt;
            _observedKick = false;
            _signalExpiresAt = AddMilliseconds(issuedClock, (long)(SignalWindowSeconds * 1000d));
            _protectionExpiresAt = AddMilliseconds(issuedClock, (long)(ProtectionTimeoutSeconds * 1000d));
            _protectionSnapshotExpiresAt = AddMilliseconds(issuedClock, SnapshotMilliseconds);
            _lastSignalVersion = _sampleVersion;
            if (_counterOpportunity && _counterKickSent != null)
            {
                notify = _counterKickSent;
                notifiedAt = _lastObservedAt;
                notifiedTarget = _target;
            }

            Disarm();
        }

        // Never call another component while holding this component's lock.
        try
        {
            notify?.Invoke(notifiedAt, notifiedTarget);
        }
        catch (Exception)
        { /* A failed combo notification must not disrupt Native input. */
        }
    }

    private void Disarm()
    {
        _armed = false;
        _counterOpportunity = false;
        _target = -1;
        _snapshotExpiresAt = 0;
        _guardCancellationExpiresAt = 0;
    }

    private static void ClearEarlyFilterContext()
    {
        _earlyFilterOwner = null;
        _earlyFilterSawKick = false;
        _earlyFilterDefense = Agent.MovementControlFlag.None;
        _earlyFilterExpiresAt = 0;
    }

    private void EndProtection()
    {
        _protecting = false;
        _activeCounter = false;
        _observedKick = false;
        _activeTarget = -1;
        _protectionExpiresAt = 0;
        _signalExpiresAt = 0;
        _protectionSnapshotExpiresAt = 0;
    }

    private void UpdateOwnAction(double now, Agent.ActionCodeType upper, Agent.ActionCodeType lower, bool struck)
    {
        if (!_protecting)
        {
            return;
        }

        if (!double.IsFinite(now) || now < _issuedAt || now - _issuedAt > ProtectionTimeoutSeconds || struck
            || IsInterrupted(upper) || IsInterrupted(lower))
        {
            EndProtection();
            return;
        }

        bool kicking = IsKick(upper) || IsKick(lower);
        if (kicking)
        {
            _observedKick = true;
        }
        else if (_observedKick)
        {
            EndProtection();
        }
    }

    private static bool IsKick(Agent.ActionCodeType action) =>
        action is Agent.ActionCodeType.Kick or Agent.ActionCodeType.KickContinue or Agent.ActionCodeType.KickHit;

    private static bool IsInterrupted(Agent.ActionCodeType action)
    {
        int value = (int)action;
        return !IsKick(action) && (action is Agent.ActionCodeType.ParriedMelee or Agent.ActionCodeType.BlockedMelee
            or Agent.ActionCodeType.Fall or Agent.ActionCodeType.WeaponBash or Agent.ActionCodeType.EquipUnequip
            or Agent.ActionCodeType.SwitchAlternative or Agent.ActionCodeType.Mount or Agent.ActionCodeType.Dismount
            or Agent.ActionCodeType.HitObject or Agent.ActionCodeType.LadderRaise or Agent.ActionCodeType.LadderRaiseEnd
            || (value >= (int)Agent.ActionCodeType.JumpStart && value <= (int)Agent.ActionCodeType.KickHit)
            || (value >= (int)Agent.ActionCodeType.StrikeBegin && value <= (int)Agent.ActionCodeType.StrikeEnd));
    }

    private static long AddMilliseconds(long clock, long milliseconds)
    {
        long duration = Stopwatch.Frequency / 1000 * milliseconds
            + Stopwatch.Frequency % 1000 * milliseconds / 1000;
        return clock > long.MaxValue - duration ? long.MaxValue : clock + duration;
    }
}
