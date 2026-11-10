using System.Collections.Generic;
using System.Diagnostics;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Input assistance for training bots only. All engine reads happen during the
// mission's pre-tick, after the previous agent tick has finished. The native AI
// callback can run on a different thread and reads only the published flag.
internal sealed class AdimiToolsAiArenaBackSwingGuard : AgentComponent
{
    private const float RearFacingThreshold = 0.15f;
    private const float ReachAllowance = 0.65f;
    private const double GuardWindowSeconds = 0.65d;
    private const double NativeDecisionWindowSeconds = 0.30d;
    private const long SnapshotMilliseconds = 100;
    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick | Agent.EventControlFlag.Mount
        | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear | Agent.EventControlFlag.Jump
        | Agent.EventControlFlag.ToggleAlternativeWeapon | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1
        | Agent.EventControlFlag.Wield2 | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;

    private sealed class InputSnapshot
    {
        public InputSnapshot(Agent.MovementControlFlag guard, long expiresAt) { Guard = guard; ExpiresAt = expiresAt; }
        public Agent.MovementControlFlag Guard { get; }
        public long ExpiresAt { get; }
    }

    private volatile InputSnapshot? _snapshot;
    private readonly object _publicationGate = new();
    private int _publicationVersion;
    private int _sampledPublicationVersion;
    private Agent.MovementControlFlag _sampledGuard;
    private float _sampledDistanceSquared;
    private double _sampledTime;
    private Agent? _sampledAttacker;
    private bool _sampledReleaseEdge;
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
            _guardUntil = 0d;
            _yieldUntil = 0d;
        }
        _currentReleases.Clear();
        _sampledGuard = Agent.MovementControlFlag.None;
        _sampledDistanceSquared = float.PositiveInfinity;
        _sampledTime = now;
        _sampledAttacker = null;
        _sampledReleaseEdge = false;
    }

    public void ConsiderAttacker(Agent attacker)
    {
        Agent.ActionCodeType action = attacker.GetCurrentActionType(1);
        Agent.ActionStage stage = attacker.GetCurrentActionStage(1);
        bool isRelease = action == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease;
        _currentReleases[attacker] = isRelease;
        bool releaseEdge = isRelease && (!_previousReleases.TryGetValue(attacker, out bool wasRelease) || !wasRelease);
        if (!isRelease || !double.IsFinite(_sampledTime))
        {
            return;
        }

        MissionWeapon weapon = attacker.WieldedWeapon;
        WeaponComponentData? usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
        if (usage == null || !usage.IsMeleeWeapon)
        {
            return;
        }

        // A bow in hand cannot be relied on to parry. If the bot has a shield
        // or a melee weapon, use ordinary guard input; do not switch equipment.
        MissionWeapon defenseWeapon = Agent.WieldedWeapon;
        MissionWeapon offhand = Agent.WieldedOffhandWeapon;
        bool canGuard = (!defenseWeapon.IsEmpty && defenseWeapon.CurrentUsageItem?.IsMeleeWeapon == true)
            || (!offhand.IsEmpty && offhand.CurrentUsageItem?.IsShield == true);
        if (!canGuard)
        {
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
            return;
        }

        Vec3 relativePosition = Agent.Position - attacker.Position;
        Vec2 relative = relativePosition.AsVec2;
        Vec2 attackerFacing = attacker.LookDirection.AsVec2;
        Vec2 defenderFacing = Agent.LookDirection.AsVec2;
        float distanceSquared = relative.LengthSquared + relativePosition.z * relativePosition.z;
        if (_sampledAttacker != null && distanceSquared >= _sampledDistanceSquared)
        {
            return;
        }

        Agent.MovementControlFlag guard = ChooseGuard(relative.x, relative.y, relativePosition.z,
            attackerFacing.x, attackerFacing.y, defenderFacing.x, defenderFacing.y,
            usage.GetRealWeaponLength(), attacker.GetCurrentActionDirection(1));
        if (guard != Agent.MovementControlFlag.None)
        {
            _sampledDistanceSquared = distanceSquared;
            _sampledGuard = guard;
            _sampledAttacker = attacker;
            _sampledReleaseEdge = releaseEdge;
        }
    }

    public void EndSampling()
    {
        if (!double.IsFinite(_sampledTime))
        {
            Clear();
            return;
        }

        (_previousReleases, _currentReleases) = (_currentReleases, _previousReleases);
        _lastTime = _sampledTime;
        _hasLastTime = true;
        if (_sampledGuard == Agent.MovementControlFlag.None || _sampledAttacker == null)
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

        if (_guardUntil <= 0d) _guardUntil = _sampledTime + GuardWindowSeconds;

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
                _snapshot = new InputSnapshot(_sampledGuard, expiresAt);
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

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        InputSnapshot? snapshot = _snapshot;
        if (snapshot == null || Stopwatch.GetTimestamp() >= snapshot.ExpiresAt
            || (eventFlag & InterruptingEvents) != 0
            || (movementFlag & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock
                | Agent.MovementControlFlag.Action)) != 0)
        {
            return;
        }

        // Preserve movement and event input. Do not introduce any kick or bash.
        // Native decides whether an ongoing ready can transition to this guard.
        movementFlag = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | snapshot.Guard;
    }

    internal static Agent.MovementControlFlag ChooseGuard(float dx, float dy, float dz, float attackLookX, float attackLookY,
        float defendLookX, float defendLookY, float weaponLength, Agent.UsageDirection attackDirection)
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
        if (attackerDot > RearFacingThreshold)
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
        return attackDirection switch
        {
            Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.DefendRight,
            Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.DefendLeft,
            Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.DefendUp,
            Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.DefendDown,
            _ => Agent.MovementControlFlag.None,
        };
    }
}
