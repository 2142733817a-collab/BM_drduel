using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// The caller supplies only living, opposing players in the same active arena.
// Ordinary reading is restricted to Hard; a confirmed missed counter-kick can
// enable the separately timed read at any difficulty. All agent and scene
// reads happen on the mission thread; the AI callback reads only a snapshot.
internal sealed class AdimiToolsAiArenaReadGuard : AgentComponent
{
    // One independent choice per observed release and defender. This controls
    // the assistance opportunity, not the eventual native block success rate.
    internal const double ReadAssistChance = 0.10d;
    private const double GuardWindowSeconds = 0.65d;
    private const double NativeWindowSeconds = 0.30d;
    private const double VisibilityCacheSeconds = 0.10d;
    private const double ReadyDirectionCacheSeconds = 0.20d;
    private const double AfterMissWindowSeconds = 0.80d;
    private const float VisibilityPositionToleranceSquared = 0.01f;
    private const long SnapshotMilliseconds = 100;
    private const float ReachAllowance = 0.65f;
    private const float AttackerFacingThreshold = 0.15f;
    private const Agent.MovementControlFlag ExistingDefense = Agent.MovementControlFlag.DefendMask
        | Agent.MovementControlFlag.DefendBlock;
    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick | Agent.EventControlFlag.Mount
        | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear | Agent.EventControlFlag.Jump
        | Agent.EventControlFlag.ToggleAlternativeWeapon | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1
        | Agent.EventControlFlag.Wield2 | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;

    private sealed class InputSnapshot
    {
        public InputSnapshot(Agent.MovementControlFlag guard, long expiresAt, bool mayCorrectDirection = false)
        {
            Guard = guard;
            ExpiresAt = expiresAt;
            MayCorrectDirection = mayCorrectDirection;
        }

        public Agent.MovementControlFlag Guard { get; }
        public long ExpiresAt { get; }
        public bool MayCorrectDirection { get; }
    }

    // An observation is made while the bot is still kicking, but cannot
    // publish a special guard until the caller confirms the kick has missed.
    private sealed class AfterKickObservation
    {
        public Agent.MovementControlFlag Guard { get; set; }
        public double SeenAt { get; set; }
        public bool WasRelease { get; set; }
    }

    private sealed class ReleaseDecision
    {
        public ReleaseDecision(bool selected)
        {
            Selected = selected;
        }

        public bool Selected { get; }
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

    private readonly struct ReadyDirectionSample
    {
        public ReadyDirectionSample(Agent.MovementControlFlag guard, double time)
        {
            Guard = guard;
            Time = time;
        }

        public Agent.MovementControlFlag Guard { get; }
        public double Time { get; }
    }

    private volatile InputSnapshot? _snapshot;
    private readonly Func<double> _nextRoll;
    // Weak keys keep a decision through a brief omission or Clear without
    // retaining departed players. An observed non-release removes it early.
    private readonly ConditionalWeakTable<Agent, ReleaseDecision> _releaseDecisions = new();
    private readonly object _publicationGate = new();
    private int _publicationVersion;
    private int _sampledPublicationVersion;
    private Dictionary<Agent, bool> _previousReleases = new();
    private Dictionary<Agent, bool> _currentReleases = new();
    private readonly Dictionary<Agent, VisibilitySample> _visibility = new();
    private readonly Dictionary<Agent, ReadyDirectionSample> _readyDirections = new();
    private readonly Dictionary<Agent, AfterKickObservation> _afterKickObservations = new();
    private readonly List<Agent> _obsoleteVisibility = new();
    private Agent.MovementControlFlag _sampledGuard;
    private Agent? _sampledAttacker;
    private float _sampledDistanceSquared;
    private bool _sampledRelease;
    private bool _sampledReleaseEdge;
    private bool _defenderEligible;
    private double _sampledTime;
    private double _lastTime;
    private bool _hasLastTime;
    private double _guardUntil;
    private double _yieldUntil;
    private bool _normalReadEnabled;
    private bool _observeAfterKick;
    private bool _afterMissRequested;
    private bool _afterMissWasRequested;
    private double _afterMissUntil;
    private double _reactionDelaySeconds;
    private Agent.MovementControlFlag _afterMissGuard;
    private Agent? _afterMissAttacker;
    private float _afterMissDistanceSquared;
    private bool _afterMissRelease;
    private bool _afterMissPendingRelease;
    private int _observedPublicationVersion;

    public AdimiToolsAiArenaReadGuard(Agent agent)
        : this(agent, Random.Shared.NextDouble)
    {
    }

    internal AdimiToolsAiArenaReadGuard(Agent agent, Func<double> nextRoll)
        : base(agent)
    {
        _nextRoll = nextRoll ?? throw new ArgumentNullException(nameof(nextRoll));
    }

    public void BeginSampling(double now, bool afterMiss = false, bool observeAfterKick = false, bool normalReadEnabled = true)
    {
        // Do not leave yesterday's published input active if sampling throws.
        bool invalidated;
        lock (_publicationGate)
        {
            _snapshot = null;
            _sampledPublicationVersion = _publicationVersion;
            invalidated = _observedPublicationVersion != _publicationVersion;
            _observedPublicationVersion = _publicationVersion;
        }

        if (invalidated)
        {
            _afterKickObservations.Clear();
        }

        _sampledTime = now;
        _sampledGuard = Agent.MovementControlFlag.None;
        _sampledAttacker = null;
        _sampledDistanceSquared = float.PositiveInfinity;
        _sampledRelease = false;
        _sampledReleaseEdge = false;
        _afterMissGuard = Agent.MovementControlFlag.None;
        _afterMissAttacker = null;
        _afterMissDistanceSquared = float.PositiveInfinity;
        _afterMissRelease = false;
        _afterMissPendingRelease = false;
        _normalReadEnabled = normalReadEnabled;
        _observeAfterKick = observeAfterKick || afterMiss;
        _afterMissRequested = afterMiss;
        _currentReleases.Clear();
        if (!double.IsFinite(now) || (_hasLastTime && now < _lastTime))
        {
            ResetWindows();
            _previousReleases.Clear();
            _releaseDecisions.Clear();
            _visibility.Clear();
            _readyDirections.Clear();
            _afterKickObservations.Clear();
            _afterMissWasRequested = false;
            _afterMissUntil = 0d;
        }

        if (!_observeAfterKick)
        {
            _afterKickObservations.Clear();
        }

        if (afterMiss && !_afterMissWasRequested && double.IsFinite(now))
        {
            _afterMissUntil = now + AfterMissWindowSeconds;
        }

        _afterMissWasRequested = afterMiss;
        _reactionDelaySeconds = _observeAfterKick
            ? AdimiToolsAiArenaBackSwingGuard.ReactionDelayForSkill(AdimiToolsAiArenaBackSwingGuard.GetEffectiveMeleeSkill(Agent))
            : 0d;

        _defenderEligible = double.IsFinite(now) && CanDefend(Agent);
    }

    public void ConsiderAttacker(Agent attacker)
    {
        if (!double.IsFinite(_sampledTime))
        {
            return;
        }

        Agent.ActionCodeType action = attacker.GetCurrentActionType(1);
        Agent.ActionStage stage = attacker.GetCurrentActionStage(1);
        bool release = action == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease;
        bool ready = action == Agent.ActionCodeType.ReadyMelee
            && stage is Agent.ActionStage.AttackReady or Agent.ActionStage.AttackQuickReady;
        _currentReleases[attacker] = release;
        bool releaseEdge = release && !_releaseDecisions.TryGetValue(attacker, out _);
        if (!release)
        {
            _releaseDecisions.Remove(attacker);
        }
        else if (releaseEdge && _normalReadEnabled)
        {
            _releaseDecisions.Add(attacker, new ReleaseDecision(SelectReadAssist()));
        }

        if (!ReferenceEquals(attacker.Mission, Agent.Mission))
        {
            _readyDirections.Remove(attacker);
            _afterKickObservations.Remove(attacker);
            return;
        }

        if (ready && (_defenderEligible || _observeAfterKick))
        {
            // Record only a single server-received attack bit. A held ready
            // never publishes a guard; a cancel/ambiguous bit drops the cache.
            Agent.MovementControlFlag readyGuard = GuardForInput(attacker.MovementFlags);
            if (readyGuard == Agent.MovementControlFlag.None)
            {
                _readyDirections.Remove(attacker);
            }
            else
            {
                _readyDirections[attacker] = new ReadyDirectionSample(readyGuard, _sampledTime);
            }
        }
        else if (!release || (!_defenderEligible && !_observeAfterKick))
        {
            _readyDirections.Remove(attacker);
        }

        if (_observeAfterKick)
        {
            ObserveAfterKick(attacker, ready, release);
        }

        if (!_defenderEligible || !_normalReadEnabled || !release)
        {
            return;
        }

        if (!_releaseDecisions.TryGetValue(attacker, out ReleaseDecision? decision) || !decision.Selected)
        {
            return;
        }

        Agent.MovementControlFlag guard = GuardForDirection(attacker.GetCurrentActionDirection(1));
        if (guard == Agent.MovementControlFlag.None)
        {
            Agent.MovementControlFlag flags = attacker.MovementFlags;
            Agent.MovementControlFlag attackBits = flags & Agent.MovementControlFlag.AttackMask;
            Agent.MovementControlFlag inputGuard = GuardForInput(flags);
            if (!_readyDirections.TryGetValue(attacker, out ReadyDirectionSample cached)
                || _sampledTime < cached.Time || _sampledTime - cached.Time > ReadyDirectionCacheSeconds
                || (flags & (ExistingDefense | Agent.MovementControlFlag.Action)) != 0
                || (attackBits != Agent.MovementControlFlag.None && inputGuard != cached.Guard))
            {
                _readyDirections.Remove(attacker);
                return;
            }

            guard = cached.Guard;
        }

        if (guard == Agent.MovementControlFlag.None)
        {
            return;
        }

        WeaponComponentData? usage = GetUsage(attacker.WieldedWeapon);
        if (usage?.IsMeleeWeapon != true)
        {
            return;
        }

        Vec3 delta = attacker.Position - Agent.Position;
        Vec3 attackLook = attacker.LookDirection;
        Vec3 defendLook = Agent.LookDirection;
        float distanceSquared = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
        if (!IsInFrontAndReach(delta, attackLook, defendLook, usage.GetRealWeaponLength()))
        {
            return;
        }

        if (_sampledAttacker != null && ((_sampledRelease && !release)
            || (_sampledRelease == release && distanceSquared >= _sampledDistanceSquared)))
        {
            return;
        }

        Vec3 defenderEye = Agent.GetEyeGlobalPosition();
        Vec3 attackerEye = attacker.GetEyeGlobalPosition();
        Scene? scene = Agent.Mission?.Scene;
        if (scene == null || !IsFinite(defenderEye) || !IsFinite(attackerEye)
            || !HasClearSight(attacker, scene, defenderEye, attackerEye))
        {
            return;
        }

        _sampledAttacker = attacker;
        _sampledGuard = guard;
        _sampledDistanceSquared = distanceSquared;
        _sampledRelease = release;
        _sampledReleaseEdge = releaseEdge;
    }

    public void EndSampling()
    {
        if (!double.IsFinite(_sampledTime))
        {
            Clear();
            return;
        }

        // Record every considered player's phase, not just the chosen player.
        // Changing between two ongoing releases must not create false edges.
        (_previousReleases, _currentReleases) = (_currentReleases, _previousReleases);
        _obsoleteVisibility.Clear();
        foreach (Agent cached in _visibility.Keys)
        {
            if (!_previousReleases.ContainsKey(cached))
            {
                _obsoleteVisibility.Add(cached);
            }
        }

        foreach (Agent obsolete in _obsoleteVisibility)
        {
            _visibility.Remove(obsolete);
        }

        _obsoleteVisibility.Clear();
        foreach (Agent cached in _readyDirections.Keys)
        {
            if (!_previousReleases.ContainsKey(cached))
            {
                _obsoleteVisibility.Add(cached);
            }
        }

        foreach (Agent obsolete in _obsoleteVisibility)
        {
            _readyDirections.Remove(obsolete);
        }

        _obsoleteVisibility.Clear();
        foreach (Agent cached in _afterKickObservations.Keys)
        {
            if (!_previousReleases.ContainsKey(cached))
            {
                _obsoleteVisibility.Add(cached);
            }
        }

        foreach (Agent obsolete in _obsoleteVisibility)
        {
            _afterKickObservations.Remove(obsolete);
        }

        _lastTime = _sampledTime;
        _hasLastTime = true;
        if (_afterMissRequested && _sampledTime <= _afterMissUntil && _defenderEligible
            && (!_afterMissPendingRelease || _afterMissRelease)
            && _afterMissGuard != Agent.MovementControlFlag.None && _afterMissAttacker != null)
        {
            PublishGuard(_afterMissGuard, mayCorrectDirection: true);
            return;
        }

        if (_sampledGuard == Agent.MovementControlFlag.None || _sampledAttacker == null)
        {
            ResetWindows();
            return;
        }

        if (_sampledReleaseEdge)
        {
            // An actual new release can interrupt the native decision window.
            _guardUntil = _sampledTime + GuardWindowSeconds;
            _yieldUntil = 0d;
        }
        else if (_yieldUntil > 0d)
        {
            if (_sampledTime < _yieldUntil)
            {
                return;
            }

            _yieldUntil = 0d;
            _guardUntil = _sampledTime + GuardWindowSeconds;
        }
        else if (_guardUntil <= 0d)
        {
            _guardUntil = _sampledTime + GuardWindowSeconds;
        }

        if (_sampledTime >= _guardUntil)
        {
            // Even an unusually long release periodically yields to Native.
            _guardUntil = 0d;
            _yieldUntil = _sampledTime + NativeWindowSeconds;
            return;
        }

        PublishGuard(_sampledGuard, mayCorrectDirection: false);
    }

    public void Clear()
    {
        lock (_publicationGate)
        {
            _snapshot = null;
            _publicationVersion++;
        }

        _sampledGuard = Agent.MovementControlFlag.None;
        _sampledAttacker = null;
        _defenderEligible = false;
        _previousReleases.Clear();
        _currentReleases.Clear();
        _visibility.Clear();
        _readyDirections.Clear();
        _afterKickObservations.Clear();
        _obsoleteVisibility.Clear();
        _afterMissGuard = Agent.MovementControlFlag.None;
        _afterMissAttacker = null;
        _afterMissRequested = false;
        _afterMissWasRequested = false;
        _afterMissUntil = 0d;
        // Preserve the last release choices across a temporary Clear. The
        // same still-ongoing attack must not get another roll on re-entry.
        ResetWindows();
    }

    // Safe during native removal callbacks: dictionaries are mission-thread
    // state and are cleared later by the ordinary arena lifecycle.
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
                || (movementFlag & Agent.MovementControlFlag.Action) != 0)
            {
                return;
            }

            Agent.MovementControlFlag defense = movementFlag & ExistingDefense;
            if (!snapshot.MayCorrectDirection)
            {
                if (defense != Agent.MovementControlFlag.None)
                {
                    return;
                }
            }
            else
            {
                // Preserve a simultaneous block bit while correcting its single
                // direction. Auto defense, undirected block and ambiguous direction
                // combinations remain under Native control.
                if ((defense & Agent.MovementControlFlag.DefendAuto) != 0
                    || ((defense & Agent.MovementControlFlag.DefendBlock) != 0
                        && (movementFlag & Agent.MovementControlFlag.AttackMask) != 0))
                {
                    return;
                }

                uint directions = (uint)(defense & Agent.MovementControlFlag.DefendDirMask);
                if ((directions == 0 && (defense & Agent.MovementControlFlag.DefendBlock) != 0)
                    || (directions != 0 && (directions & (directions - 1)) != 0))
                {
                    return;
                }
            }

            // This managed write is serialized with Suspend/Clear. Native
            // remains responsible for the action transition and collision.
            movementFlag = (movementFlag & ~(Agent.MovementControlFlag.AttackMask | Agent.MovementControlFlag.DefendDirMask)) | snapshot.Guard;
        }
    }

    internal static bool IsInFrontAndReach(Vec3 delta, Vec3 attackerLook, Vec3 defenderLook, float weaponLength)
    {
        if (!IsFinite(delta) || !IsFinite(attackerLook) || !IsFinite(defenderLook) || !float.IsFinite(weaponLength))
        {
            return false;
        }

        float horizontalSquared = delta.x * delta.x + delta.y * delta.y;
        float distanceSquared = horizontalSquared + delta.z * delta.z;
        float attackerFacingSquared = attackerLook.x * attackerLook.x + attackerLook.y * attackerLook.y;
        float defenderFacingSquared = defenderLook.x * defenderLook.x + defenderLook.y * defenderLook.y;
        if (!float.IsFinite(distanceSquared) || !float.IsFinite(attackerFacingSquared) || !float.IsFinite(defenderFacingSquared)
            || horizontalSquared < 0.0001f || attackerFacingSquared < 0.0001f || defenderFacingSquared < 0.0001f)
        {
            return false;
        }

        float reach = System.Math.Clamp(weaponLength, 0.4f, 2.5f) + ReachAllowance;
        if (distanceSquared > reach * reach)
        {
            return false;
        }

        float attackerDot = -(delta.x * attackerLook.x + delta.y * attackerLook.y)
            / System.MathF.Sqrt(horizontalSquared * attackerFacingSquared);
        float defenderDot = (delta.x * defenderLook.x + delta.y * defenderLook.y)
            / System.MathF.Sqrt(horizontalSquared * defenderFacingSquared);
        return attackerDot > AttackerFacingThreshold && defenderDot > 0f;
    }

    private static bool CanDefend(Agent defender)
    {
        Agent.ActionCodeType upper = defender.GetCurrentActionType(1);
        Agent.ActionCodeType lower = defender.GetCurrentActionType(0);
        Agent.ActionStage stage = defender.GetCurrentActionStage(1);
        int upperType = (int)upper;
        bool defenseAnimation = upperType >= (int)Agent.ActionCodeType.DefendAllBegin
            && upperType < (int)Agent.ActionCodeType.DefendAllEnd;
        if (IsInterrupted(upper) || IsInterrupted(lower)
            || (!defenseAnimation && upper is not (Agent.ActionCodeType.Idle or Agent.ActionCodeType.Guard
                or Agent.ActionCodeType.Other or Agent.ActionCodeType.ReadyMelee))
            || stage is Agent.ActionStage.AttackRelease or Agent.ActionStage.DefendParry
            || (upper == Agent.ActionCodeType.ReadyMelee && stage != Agent.ActionStage.AttackReady))
        {
            return false;
        }

        WeaponComponentData? main = GetUsage(defender.WieldedWeapon);
        WeaponComponentData? offhand = GetUsage(defender.WieldedOffhandWeapon);
        return main?.IsMeleeWeapon == true || offhand?.IsShield == true;
    }

    private static bool IsInterrupted(Agent.ActionCodeType action)
    {
        int value = (int)action;
        return action is Agent.ActionCodeType.ReleaseMelee or Agent.ActionCodeType.ReleaseRanged
            or Agent.ActionCodeType.ReleaseThrowing or Agent.ActionCodeType.ParriedMelee
            or Agent.ActionCodeType.BlockedMelee or Agent.ActionCodeType.Fall or Agent.ActionCodeType.WeaponBash
            or Agent.ActionCodeType.EquipUnequip or Agent.ActionCodeType.SwitchAlternative
            or Agent.ActionCodeType.Mount or Agent.ActionCodeType.Dismount or Agent.ActionCodeType.HitObject
            or Agent.ActionCodeType.LadderRaise or Agent.ActionCodeType.LadderRaiseEnd
            || (value >= (int)Agent.ActionCodeType.JumpStart && value <= (int)Agent.ActionCodeType.KickHit)
            || (value >= (int)Agent.ActionCodeType.StrikeBegin && value <= (int)Agent.ActionCodeType.StrikeEnd);
    }

    private static WeaponComponentData? GetUsage(MissionWeapon weapon)
    {
        // CurrentUsageItem indexes the internal list without a bounds check.
        if (weapon.Item == null || weapon.CurrentUsageIndex < 0 || weapon.CurrentUsageIndex >= weapon.WeaponsCount)
        {
            return null;
        }

        return weapon.CurrentUsageItem;
    }

    private static Agent.MovementControlFlag GuardForInput(Agent.MovementControlFlag flags)
    {
        // A simultaneous guard or action may be cancelled/blocked intent.
        if ((flags & (ExistingDefense | Agent.MovementControlFlag.Action)) != 0)
        {
            return Agent.MovementControlFlag.None;
        }

        return (flags & Agent.MovementControlFlag.AttackMask) switch
        {
            Agent.MovementControlFlag.AttackLeft => Agent.MovementControlFlag.DefendRight,
            Agent.MovementControlFlag.AttackRight => Agent.MovementControlFlag.DefendLeft,
            Agent.MovementControlFlag.AttackUp => Agent.MovementControlFlag.DefendUp,
            Agent.MovementControlFlag.AttackDown => Agent.MovementControlFlag.DefendDown,
            _ => Agent.MovementControlFlag.None,
        };
    }

    private static Agent.MovementControlFlag GuardForDirection(Agent.UsageDirection direction) => direction switch
    {
        Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.DefendRight,
        Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.DefendLeft,
        Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.DefendUp,
        Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.DefendDown,
        _ => Agent.MovementControlFlag.None,
    };

    private static bool IsWithinReachAndInFront(Vec3 delta, Vec3 defenderLook, float weaponLength)
    {
        if (!IsFinite(delta) || !IsFinite(defenderLook) || !float.IsFinite(weaponLength))
        {
            return false;
        }

        float horizontalSquared = delta.x * delta.x + delta.y * delta.y;
        float distanceSquared = horizontalSquared + delta.z * delta.z;
        float defenderFacingSquared = defenderLook.x * defenderLook.x + defenderLook.y * defenderLook.y;
        if (!float.IsFinite(distanceSquared) || !float.IsFinite(defenderFacingSquared)
            || horizontalSquared < 0.0001f || defenderFacingSquared < 0.0001f)
        {
            return false;
        }

        float reach = System.Math.Clamp(weaponLength, 0.4f, 2.5f) + ReachAllowance;
        return distanceSquared <= reach * reach && delta.x * defenderLook.x + delta.y * defenderLook.y > 0f;
    }

    private static bool IsFinite(Vec3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static float DistanceSquared(Vec3 a, Vec3 b)
    {
        Vec3 delta = a - b;
        return delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
    }

    private void PublishGuard(Agent.MovementControlFlag guard, bool mayCorrectDirection)
    {
        long timestamp = Stopwatch.GetTimestamp();
        long duration = Stopwatch.Frequency * SnapshotMilliseconds / 1000;
        long expiresAt = timestamp > long.MaxValue - duration ? long.MaxValue : timestamp + duration;
        lock (_publicationGate)
        {
            if (_sampledPublicationVersion == _publicationVersion)
            {
                _snapshot = new InputSnapshot(guard, expiresAt, mayCorrectDirection);
            }
        }
    }

    private void ResetWindows()
    {
        _snapshot = null;
        _guardUntil = 0d;
        _yieldUntil = 0d;
    }

    private bool SelectReadAssist()
    {
        try
        {
            double roll = _nextRoll();
            return double.IsFinite(roll) && roll >= 0d && roll < ReadAssistChance;
        }
        catch (Exception)
        {
            // An unavailable random sample must leave this whole release to Native.
            return false;
        }
    }

    private void ObserveAfterKick(Agent attacker, bool ready, bool release)
    {
        if (!ready && !release)
        {
            _afterKickObservations.Remove(attacker);
            return;
        }

        Agent.MovementControlFlag guard;
        if (ready)
        {
            Agent.MovementControlFlag flags = attacker.MovementFlags;
            Agent.MovementControlFlag attackBits = flags & Agent.MovementControlFlag.AttackMask;
            if ((flags & (ExistingDefense | Agent.MovementControlFlag.Action)) != 0)
            {
                _afterKickObservations.Remove(attacker);
                return;
            }

            // A single current input follows a feint more reliably than the
            // possibly previous animation direction. Contradictory inputs are
            // not a direction and must never reuse the old observation.
            guard = attackBits != Agent.MovementControlFlag.None
                ? GuardForInput(flags)
                : AdimiToolsAiArenaBackSwingGuard.GuardForDirection(attacker.GetCurrentActionDirection(1));
        }
        else
        {
            guard = AdimiToolsAiArenaBackSwingGuard.GuardForDirection(attacker.GetCurrentActionDirection(1));
            Agent.MovementControlFlag flags = attacker.MovementFlags;
            Agent.MovementControlFlag attackBits = flags & Agent.MovementControlFlag.AttackMask;
            Agent.MovementControlFlag inputGuard = GuardForInput(flags);
            if (guard == Agent.MovementControlFlag.None
                && _readyDirections.TryGetValue(attacker, out ReadyDirectionSample cached)
                && _sampledTime >= cached.Time && _sampledTime - cached.Time <= ReadyDirectionCacheSeconds
                && (flags & (ExistingDefense | Agent.MovementControlFlag.Action)) == 0
                && (attackBits == Agent.MovementControlFlag.None || inputGuard == cached.Guard))
            {
                guard = cached.Guard;
            }
        }

        WeaponComponentData? usage = GetUsage(attacker.WieldedWeapon);
        Vec3 delta = attacker.Position - Agent.Position;
        if (guard == Agent.MovementControlFlag.None || usage?.IsMeleeWeapon != true
            || !IsWithinReachAndInFront(delta, Agent.LookDirection, usage.GetRealWeaponLength()))
        {
            _afterKickObservations.Remove(attacker);
            return;
        }

        Vec3 defenderEye = Agent.GetEyeGlobalPosition();
        Vec3 attackerEye = attacker.GetEyeGlobalPosition();
        Scene? scene = Agent.Mission?.Scene;
        if (scene == null || !IsFinite(defenderEye) || !IsFinite(attackerEye)
            || !HasClearSight(attacker, scene, defenderEye, attackerEye))
        {
            _afterKickObservations.Remove(attacker);
            return;
        }

        if (!_afterKickObservations.TryGetValue(attacker, out AfterKickObservation? observation))
        {
            observation = new AfterKickObservation { Guard = guard, SeenAt = _sampledTime, WasRelease = release };
            _afterKickObservations.Add(attacker, observation);
        }
        else if (observation.Guard != guard || (observation.WasRelease && ready) || _sampledTime < observation.SeenAt)
        {
            observation.Guard = guard;
            observation.SeenAt = _sampledTime;
            observation.WasRelease = release;
        }
        else
        {
            observation.WasRelease = release;
        }

        if (_sampledTime - observation.SeenAt < _reactionDelaySeconds)
        {
            if (release)
            {
                _afterMissPendingRelease = true;
            }

            return;
        }

        float distanceSquared = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
        if (_afterMissAttacker != null && ((_afterMissRelease && !release)
            || (_afterMissRelease == release && distanceSquared >= _afterMissDistanceSquared)))
        {
            return;
        }

        _afterMissGuard = guard;
        _afterMissAttacker = attacker;
        _afterMissDistanceSquared = distanceSquared;
        _afterMissRelease = release;
    }

    private bool HasClearSight(Agent attacker, Scene scene, Vec3 defenderEye, Vec3 attackerEye)
    {
        if (_visibility.TryGetValue(attacker, out VisibilitySample sample)
            && ReferenceEquals(sample.Scene, scene) && _sampledTime >= sample.Time
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
}
