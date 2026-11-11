using System.Collections.Generic;
using System.Diagnostics;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Input assistance for training bots only. All engine reads happen during the
// mission's pre-tick, after the previous agent tick has finished. The native AI
// callback can run on a different thread and reads only the published flag.
internal sealed class AdimiToolsAiArenaBackSwingGuard : AgentComponent
{
    private const float RearFacingThreshold = 0.15f;
    private const float RearContinuityFacingThreshold = 0.35f;
    private const double RearContinuitySeconds = 0.15d;
    private const float ReleaseProgressBacktrackTolerance = 0.02f;
    private const float ReachAllowance = 0.65f;
    private const double GuardWindowSeconds = 0.65d;
    private const double NativeDecisionWindowSeconds = 0.30d;
    private const long SnapshotMilliseconds = 100;
    private const double ReadyDirectionCacheSeconds = 0.20d;
    private const double VisibilityCacheSeconds = 0.10d;
    private const float VisibilityPositionToleranceSquared = 0.01f;
    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick | Agent.EventControlFlag.Mount
        | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear | Agent.EventControlFlag.Jump
        | Agent.EventControlFlag.ToggleAlternativeWeapon | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1
        | Agent.EventControlFlag.Wield2 | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;

    private sealed class InputSnapshot
    {
        public InputSnapshot(Agent.MovementControlFlag guard, long expiresAt, bool isRelease)
        {
            Guard = guard;
            ExpiresAt = expiresAt;
            IsRelease = isRelease;
        }

        public Agent.MovementControlFlag Guard { get; }
        public long ExpiresAt { get; }
        public bool IsRelease { get; }
    }

    // Mission-thread only. One continuous observation is kept per attacker;
    // a feint, direction change or new attack starts a fresh reaction timer.
    private sealed class AttackObservation
    {
        public Agent.UsageDirection Direction { get; set; }
        public double SeenAt { get; set; }
        public double LastReadyAt { get; set; }
        public bool WasRelease { get; set; }
        public bool PendingReleaseEdge { get; set; }
        public double LastConfirmedRearAt { get; set; } = double.NegativeInfinity;
        public bool HasReleaseProgress { get; set; }
        public float LastReleaseProgress { get; set; }
    }

    private readonly struct VisibilitySample
    {
        public VisibilitySample(Scene scene, Vec3 defenderEye, Vec3 attackerEye, double time, bool clear)
        {
            Scene = scene;
            DefenderEye = defenderEye;
            AttackerEye = attackerEye;
            Time = time;
            Clear = clear;
        }

        public Scene Scene { get; }
        public Vec3 DefenderEye { get; }
        public Vec3 AttackerEye { get; }
        public double Time { get; }
        public bool Clear { get; }
    }

    private readonly object _publicationGate = new();
    private readonly Dictionary<Agent, AttackObservation> _observations = new();
    private readonly List<Agent> _obsoleteObservations = new();
    private readonly Dictionary<Agent, VisibilitySample> _visibility = new();
    private readonly List<Agent> _obsoleteVisibility = new();
    private volatile InputSnapshot? _snapshot;
    private int _publicationVersion;
    private int _sampledPublicationVersion;
    private Agent.MovementControlFlag _sampledGuard;
    private float _sampledDistanceSquared;
    private double _sampledTime;
    private Agent? _sampledAttacker;
    private bool _sampledReleaseEdge;
    private bool _sampledRelease;
    private bool _pendingRelease;
    private double _reactionDelaySeconds;
    private Dictionary<Agent, bool> _previousReleases = new();
    private Dictionary<Agent, bool> _currentReleases = new();
    private double _lastTime;
    private bool _hasLastTime;
    private double _guardUntil;
    private double _yieldUntil;

    public AdimiToolsAiArenaBackSwingGuard(Agent agent)
        : base(agent)
    {
    }

    public void BeginSampling(double now)
    {
        lock (_publicationGate)
        {
            _snapshot = null;
            _sampledPublicationVersion = _publicationVersion;
        }

        if (!double.IsFinite(now) || (_hasLastTime && now < _lastTime))
        {
            _previousReleases.Clear();
            _observations.Clear();
            _visibility.Clear();
            _guardUntil = 0d;
            _yieldUntil = 0d;
        }

        _currentReleases.Clear();
        _sampledGuard = Agent.MovementControlFlag.None;
        _sampledDistanceSquared = float.PositiveInfinity;
        _sampledTime = now;
        _sampledAttacker = null;
        _sampledReleaseEdge = false;
        _sampledRelease = false;
        _pendingRelease = false;
        _reactionDelaySeconds = ReactionDelayForSkill(GetEffectiveMeleeSkill(Agent));
    }

    public void ConsiderAttacker(Agent attacker)
    {
        Agent.ActionCodeType action = attacker.GetCurrentActionType(1);
        Agent.ActionStage stage = attacker.GetCurrentActionStage(1);
        bool isRelease = action == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease;
        bool isReady = action == Agent.ActionCodeType.ReadyMelee
            && stage is Agent.ActionStage.AttackReady or Agent.ActionStage.AttackQuickReady;
        _currentReleases[attacker] = isRelease;
        bool releaseEdge = isRelease && (!_previousReleases.TryGetValue(attacker, out bool wasRelease) || !wasRelease);
        if ((!isRelease && !isReady) || !double.IsFinite(_sampledTime))
        {
            _observations.Remove(attacker);
            return;
        }

        WeaponComponentData? usage = GetUsage(attacker.WieldedWeapon);
        if (usage == null || !usage.IsMeleeWeapon)
        {
            _observations.Remove(attacker);
            return;
        }

        // A bow in hand cannot be relied on to parry. If the bot has a shield
        // or a melee weapon, use ordinary guard input; do not switch equipment.
        WeaponComponentData? defenseWeapon = GetUsage(Agent.WieldedWeapon);
        WeaponComponentData? offhand = GetUsage(Agent.WieldedOffhandWeapon);
        bool canGuard = defenseWeapon?.IsMeleeWeapon == true || offhand?.IsShield == true;
        if (!canGuard)
        {
            _observations.Remove(attacker);
            return;
        }

        // Once a blow has been released the engine may no longer allow the AI
        // to cancel it. Leave that committed attack to Native.
        Agent.ActionCodeType defenderAction = Agent.GetCurrentActionType(1);
        Agent.ActionCodeType defenderLowerAction = Agent.GetCurrentActionType(0);
        if (defenderAction == Agent.ActionCodeType.ReleaseMelee
            || defenderAction == Agent.ActionCodeType.ReleaseRanged
            || defenderAction == Agent.ActionCodeType.ReleaseThrowing
            || IsInterrupted(defenderAction) || IsInterrupted(defenderLowerAction))
        {
            _observations.Remove(attacker);
            return;
        }

        Agent.MovementControlFlag input = attacker.MovementFlags;
        Agent.MovementControlFlag attackBits = input & Agent.MovementControlFlag.AttackMask;
        Agent.UsageDirection inputDirection = DirectionForInput(attackBits);
        Agent.UsageDirection direction = attacker.GetCurrentActionDirection(1);
        bool readyObservationOnly = false;
        if (isReady)
        {
            // Multiple attacks or a special action are ambiguous. A brief
            // single-attack + defend overlap occurs during feints; in that
            // case Native's current action direction may continue the reaction
            // observation, but it is not yet a reason to publish a guard.
            if ((input & Agent.MovementControlFlag.Action) != 0
                || (attackBits != Agent.MovementControlFlag.None && inputDirection == Agent.UsageDirection.None))
            {
                _observations.Remove(attacker);
                return;
            }

            bool defending = (input & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0;
            if (defending)
            {
                if (inputDirection == Agent.UsageDirection.None || GuardForDirection(direction) == Agent.MovementControlFlag.None)
                {
                    _observations.Remove(attacker);
                    return;
                }

                readyObservationOnly = true;
            }
            else if (inputDirection != Agent.UsageDirection.None)
            {
                direction = inputDirection;
            }
        }
        else if (direction == Agent.UsageDirection.None)
        {
            // Some release samples expose no direction. A recent, unambiguous
            // ready is usable only if the current input does not contradict it.
            if (!_observations.TryGetValue(attacker, out AttackObservation? cached)
                || _sampledTime < cached.LastReadyAt || _sampledTime - cached.LastReadyAt > ReadyDirectionCacheSeconds
                || (input & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock
                    | Agent.MovementControlFlag.Action)) != 0
                || (attackBits != Agent.MovementControlFlag.None && inputDirection != cached.Direction))
            {
                _observations.Remove(attacker);
                return;
            }

            direction = cached.Direction;
        }

        Vec3 relativePosition = Agent.Position - attacker.Position;
        Vec2 relative = relativePosition.AsVec2;
        Vec2 attackerFacing = attacker.LookDirection.AsVec2;
        Vec2 defenderFacing = Agent.LookDirection.AsVec2;
        float distanceSquared = relative.LengthSquared + relativePosition.z * relativePosition.z;
        float weaponLength = usage.GetRealWeaponLength();
        float releaseProgress = isRelease ? attacker.GetCurrentActionProgress(1) : 0f;
        bool validReleaseProgress = !isRelease || (float.IsFinite(releaseProgress) && releaseProgress >= 0f && releaseProgress < 1f);
        _observations.TryGetValue(attacker, out AttackObservation? previous);
        bool restartedRelease = isRelease && previous?.WasRelease == true && previous.HasReleaseProgress
            && validReleaseProgress && releaseProgress + ReleaseProgressBacktrackTolerance < previous.LastReleaseProgress;
        bool sameObservedAttack = previous != null && previous.Direction == direction
            && !(previous.WasRelease && isReady) && !restartedRelease;
        Agent.MovementControlFlag guard = ChooseGuard(relative.x, relative.y, relativePosition.z,
            attackerFacing.x, attackerFacing.y, defenderFacing.x, defenderFacing.y,
            weaponLength, direction);
        bool confirmedRear = guard != Agent.MovementControlFlag.None;
        bool continuingRear = false;
        if (!confirmedRear && sameObservedAttack && previous != null
            && double.IsFinite(previous.LastConfirmedRearAt) && _sampledTime >= previous.LastConfirmedRearAt
            && _sampledTime - previous.LastConfirmedRearAt <= RearContinuitySeconds
            && validReleaseProgress && (!isRelease || !previous.WasRelease || previous.HasReleaseProgress))
        {
            // Keep only the same already-seen swing across a small, brief
            // angular boundary change; do not start ordinary frontal reads.
            guard = ChooseGuard(relative.x, relative.y, relativePosition.z,
                attackerFacing.x, attackerFacing.y, defenderFacing.x, defenderFacing.y,
                weaponLength, direction, RearContinuityFacingThreshold);
            continuingRear = guard != Agent.MovementControlFlag.None;
        }

        if (guard == Agent.MovementControlFlag.None)
        {
            // Geometry is checked every tick, including after turning.
            _observations.Remove(attacker);
            return;
        }

        // Observe visibility on the mission thread, before publishing a
        // snapshot. A missing scene, invalid eye position or failed native
        // raycast leaves defense to Native instead of interrupting combat.
        if (!HasClearSight(attacker, forceFresh: continuingRear))
        {
            _observations.Remove(attacker);
            return;
        }

        if (!sameObservedAttack || previous == null)
        {
            previous = new AttackObservation { Direction = direction, SeenAt = _sampledTime, LastReadyAt = double.NegativeInfinity };
            _observations[attacker] = previous;
        }

        AttackObservation observation = previous;
        if (confirmedRear)
        {
            observation.LastConfirmedRearAt = _sampledTime;
        }

        if (isReady)
        {
            // A conflicting input cannot be used later as the cached answer
            // for a release whose real Native action direction is missing.
            observation.LastReadyAt = readyObservationOnly ? double.NegativeInfinity : _sampledTime;
        }

        observation.WasRelease = isRelease;
        observation.HasReleaseProgress = isRelease && validReleaseProgress;
        observation.LastReleaseProgress = releaseProgress;
        observation.PendingReleaseEdge |= releaseEdge;
        if (readyObservationOnly)
        {
            return;
        }

        if (_sampledTime < observation.SeenAt || _sampledTime - observation.SeenAt < _reactionDelaySeconds)
        {
            _pendingRelease |= isRelease;
            return;
        }

        bool observedReleaseEdge = observation.PendingReleaseEdge;
        // An already reacted-to release is not a new release just because a
        // different ongoing attacker later becomes the closest one.
        observation.PendingReleaseEdge = false;

        // An actual release is more urgent than a closer held ready. Within
        // the same phase choose the nearest valid attacker, not iteration order.
        if (_sampledAttacker != null && ((_sampledRelease && !isRelease)
            || (_sampledRelease == isRelease && distanceSquared >= _sampledDistanceSquared)))
        {
            return;
        }

        _sampledDistanceSquared = distanceSquared;
        _sampledGuard = guard;
        _sampledAttacker = attacker;
        _sampledRelease = isRelease;
        _sampledReleaseEdge = observedReleaseEdge;
    }

    public void EndSampling()
    {
        if (!double.IsFinite(_sampledTime))
        {
            Clear();
            return;
        }

        _obsoleteObservations.Clear();
        foreach (Agent observed in _observations.Keys)
        {
            if (!_currentReleases.ContainsKey(observed))
            {
                _obsoleteObservations.Add(observed);
            }
        }

        foreach (Agent obsolete in _obsoleteObservations)
        {
            _observations.Remove(obsolete);
        }

        _obsoleteVisibility.Clear();
        foreach (Agent cached in _visibility.Keys)
        {
            if (!_currentReleases.ContainsKey(cached))
            {
                _obsoleteVisibility.Add(cached);
            }
        }

        foreach (Agent obsolete in _obsoleteVisibility)
        {
            _visibility.Remove(obsolete);
        }

        (_previousReleases, _currentReleases) = (_currentReleases, _previousReleases);
        _lastTime = _sampledTime;
        _hasLastTime = true;
        if (_sampledGuard == Agent.MovementControlFlag.None || _sampledAttacker == null
            || (_pendingRelease && !_sampledRelease))
        {
            _guardUntil = 0d;
            _yieldUntil = 0d;
            return;
        }

        if (_sampledReleaseEdge)
        {
            _guardUntil = _sampledTime + GuardWindowSeconds;
            _yieldUntil = 0d;
        }

        if (_yieldUntil > 0d)
        {
            if (_sampledTime < _yieldUntil)
            {
                return;
            }

            _yieldUntil = 0d;
            _guardUntil = _sampledTime + GuardWindowSeconds;
        }

        if (_guardUntil <= 0d)
        {
            _guardUntil = _sampledTime + GuardWindowSeconds;
        }

        if (_sampledTime >= _guardUntil)
        {
            // An unusually long release periodically yields back to Native.
            _yieldUntil = _sampledTime + NativeDecisionWindowSeconds;
            _guardUntil = 0d;
            return;
        }

        long timestamp = Stopwatch.GetTimestamp();
        long duration = Stopwatch.Frequency / 1000 * SnapshotMilliseconds
            + Stopwatch.Frequency % 1000 * SnapshotMilliseconds / 1000;
        long expiresAt = timestamp > long.MaxValue - duration ? long.MaxValue : timestamp + duration;
        lock (_publicationGate)
        {
            if (_sampledPublicationVersion == _publicationVersion)
            {
                _snapshot = new InputSnapshot(_sampledGuard, expiresAt, _sampledRelease);
            }
        }
    }

    public void Clear()
    {
        lock (_publicationGate)
        {
            _snapshot = null;
            _publicationVersion++;
        }

        _previousReleases.Clear();
        _currentReleases.Clear();
        _observations.Clear();
        _visibility.Clear();
        _obsoleteVisibility.Clear();
        _hasLastTime = false;
        _guardUntil = 0d;
        _yieldUntil = 0d;
    }

    // Safe to call from a hit/removal callback: only the published input is
    // withdrawn; mission-thread tracking is left for the regular sample.
    public void Suspend()
    {
        lock (_publicationGate)
        {
            _snapshot = null;
            _publicationVersion++;
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        lock (_publicationGate)
        {
            InputSnapshot? snapshot = _snapshot;
            if (snapshot == null || Stopwatch.GetTimestamp() >= snapshot.ExpiresAt
                || (eventFlag & InterruptingEvents) != 0
                || (movementFlag & (Agent.MovementControlFlag.DefendAuto | Agent.MovementControlFlag.Action)) != 0)
            {
                return;
            }

            Agent.MovementControlFlag direction = movementFlag & Agent.MovementControlFlag.DefendDirMask;
            if ((movementFlag & Agent.MovementControlFlag.DefendBlock) != 0
                && (direction == Agent.MovementControlFlag.None
                    || (movementFlag & Agent.MovementControlFlag.AttackMask) != 0))
            {
                // A standalone block has no direction to correct. A simultaneous
                // attack and block is ambiguous; retain Native's complete input.
                return;
            }

            if (direction == snapshot.Guard)
            {
                // A freshly sampled released blow must retain defensive
                // priority even when Native already selected the right guard.
                // Do not reinterpret an ambiguous multi-direction attack or
                // suppress a held-ready tell, which may be a player feint.
                Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
                uint attackBits = (uint)attack;
                if (snapshot.IsRelease && attackBits != 0 && (attackBits & (attackBits - 1u)) == 0)
                {
                    movementFlag &= ~Agent.MovementControlFlag.AttackMask;
                }

                return;
            }

            if (direction != Agent.MovementControlFlag.None && direction is not (Agent.MovementControlFlag.DefendLeft
                or Agent.MovementControlFlag.DefendRight or Agent.MovementControlFlag.DefendUp or Agent.MovementControlFlag.DefendDown))
            {
                // Multiple simultaneous direction bits are ambiguous Native input.
                return;
            }

            // The gate serializes this managed write with Suspend/Clear. Native
            // still owns the action transition and collision.
            movementFlag = (movementFlag & ~(Agent.MovementControlFlag.AttackMask | Agent.MovementControlFlag.DefendDirMask))
                | snapshot.Guard;
        }
    }

    // These are deliberately bounded input-reaction timings, not block
    // probabilities. The engine still owns animation and collision resolution.
    internal static double ReactionDelayForSkill(int skill)
    {
        double normalized = System.Math.Clamp(skill, 0, 330) / 330d;
        return 0.150d - 0.115d * normalized;
    }

    internal static int GetEffectiveMeleeSkill(Agent agent)
    {
        try
        {
            WeaponComponentData? usage = GetUsage(agent.WieldedWeapon);
            if (usage?.IsMeleeWeapon != true)
            {
                // Shield-only defense has no currently held melee skill to query.
                return 0;
            }

            return MissionGameModels.Current?.AgentStatCalculateModel?.GetEffectiveSkillForWeapon(agent, usage) ?? 0;
        }
        catch (System.Exception)
        {
            // A missing model or invalid weapon usage must not abort sampling.
            return 0;
        }
    }

    internal static Agent.MovementControlFlag ChooseGuard(float dx, float dy, float dz, float attackLookX, float attackLookY,
        float defendLookX, float defendLookY, float weaponLength, Agent.UsageDirection attackDirection,
        float maximumAttackerDot = RearFacingThreshold)
    {
        float horizontalDistanceSquared = dx * dx + dy * dy;
        float distanceSquared = horizontalDistanceSquared + dz * dz;
        float attackFacingSquared = attackLookX * attackLookX + attackLookY * attackLookY;
        float defendFacingSquared = defendLookX * defendLookX + defendLookY * defendLookY;
        if (!float.IsFinite(distanceSquared) || !float.IsFinite(attackFacingSquared)
            || !float.IsFinite(defendFacingSquared) || !float.IsFinite(weaponLength)
            || horizontalDistanceSquared < 0.0001f || attackFacingSquared < 0.0001f || defendFacingSquared < 0.0001f)
        {
            return Agent.MovementControlFlag.None;
        }

        float reach = System.Math.Clamp(weaponLength, 0.4f, 2.5f) + ReachAllowance;
        if (distanceSquared > reach * reach)
        {
            return Agent.MovementControlFlag.None;
        }

        float attackerDot = (dx * attackLookX + dy * attackLookY)
            / System.MathF.Sqrt(horizontalDistanceSquared * attackFacingSquared);
        if (attackerDot > maximumAttackerDot)
        {
            return Agent.MovementControlFlag.None;
        }

        // The defender must have the attacker on its front side; the engine is
        // still responsible for physically executing and resolving the guard.
        float defenderDot = -(dx * defendLookX + dy * defendLookY)
            / System.MathF.Sqrt(horizontalDistanceSquared * defendFacingSquared);
        if (defenderDot <= 0f)
        {
            return Agent.MovementControlFlag.None;
        }

        // Start with Native's ordinary auto-block direction mapping. Whether it
        // also catches this unusual back-facing sweep must be verified in game.
        return GuardForDirection(attackDirection);
    }

    internal static Agent.MovementControlFlag GuardForDirection(Agent.UsageDirection attackDirection) => attackDirection switch
        {
            Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.DefendRight,
            Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.DefendLeft,
            Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.DefendUp,
            Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.DefendDown,
            _ => Agent.MovementControlFlag.None,
        };

    private static bool IsInterrupted(Agent.ActionCodeType action)
    {
        int value = (int)action;
        return action is Agent.ActionCodeType.ParriedMelee or Agent.ActionCodeType.BlockedMelee
            or Agent.ActionCodeType.Fall or Agent.ActionCodeType.WeaponBash or Agent.ActionCodeType.PassiveUsage
            or Agent.ActionCodeType.EquipUnequip or Agent.ActionCodeType.SwitchAlternative or Agent.ActionCodeType.Mount
            or Agent.ActionCodeType.Dismount or Agent.ActionCodeType.HitObject or Agent.ActionCodeType.LadderRaise
            or Agent.ActionCodeType.LadderRaiseEnd
            || (value >= (int)Agent.ActionCodeType.JumpStart && value <= (int)Agent.ActionCodeType.KickHit)
            || (value >= (int)Agent.ActionCodeType.StrikeBegin && value <= (int)Agent.ActionCodeType.StrikeEnd);
    }

    private static Agent.UsageDirection DirectionForInput(Agent.MovementControlFlag attackBits) => attackBits switch
    {
        Agent.MovementControlFlag.AttackLeft => Agent.UsageDirection.AttackLeft,
        Agent.MovementControlFlag.AttackRight => Agent.UsageDirection.AttackRight,
        Agent.MovementControlFlag.AttackUp => Agent.UsageDirection.AttackUp,
        Agent.MovementControlFlag.AttackDown => Agent.UsageDirection.AttackDown,
        _ => Agent.UsageDirection.None,
    };

    private static WeaponComponentData? GetUsage(MissionWeapon weapon)
    {
        try
        {
            if (weapon.Item == null || weapon.CurrentUsageIndex < 0 || weapon.CurrentUsageIndex >= weapon.WeaponsCount)
            {
                return null;
            }

            return weapon.CurrentUsageItem;
        }
        catch (System.Exception)
        {
            // A transient invalid usage must not abort other arena tactics.
            return null;
        }
    }

    private static bool IsFinite(Vec3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static float DistanceSquared(Vec3 a, Vec3 b)
    {
        Vec3 delta = a - b;
        return delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
    }

    private bool HasClearSight(Agent attacker, bool forceFresh = false)
    {
        try
        {
            Mission? mission = Agent.Mission;
            if (mission == null || !System.Object.ReferenceEquals(mission, attacker.Mission))
            {
                _visibility.Remove(attacker);
                return false;
            }

            Scene? scene = mission.Scene;
            Vec3 defenderEye = Agent.GetEyeGlobalPosition();
            Vec3 attackerEye = attacker.GetEyeGlobalPosition();
            if (scene == null || !IsFinite(defenderEye) || !IsFinite(attackerEye))
            {
                _visibility.Remove(attacker);
                return false;
            }

            if (!forceFresh && _visibility.TryGetValue(attacker, out VisibilitySample sample)
                && System.Object.ReferenceEquals(sample.Scene, scene) && _sampledTime >= sample.Time
                && _sampledTime - sample.Time < VisibilityCacheSeconds
                && DistanceSquared(defenderEye, sample.DefenderEye) <= VisibilityPositionToleranceSquared
                && DistanceSquared(attackerEye, sample.AttackerEye) <= VisibilityPositionToleranceSquared)
            {
                return sample.Clear;
            }

            bool clear = !scene.RayCastForClosestEntityOrTerrain(defenderEye, attackerEye, out _, 0.01f,
                BodyFlags.CommonFocusRayCastExcludeFlags);
            _visibility[attacker] = new VisibilitySample(scene, defenderEye, attackerEye, _sampledTime, clear);
            return clear;
        }
        catch (System.Exception)
        {
            _visibility.Remove(attacker);
            return false;
        }
    }
}
