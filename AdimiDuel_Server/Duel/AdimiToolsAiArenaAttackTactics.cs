using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A bounded input experiment, not a learned player model. Sampling is done on
// the mission thread; the AI callback consumes only this component's snapshot.
internal sealed class AdimiToolsAiArenaAttackTactics : AgentComponent
{
    private enum Phase { Idle, FeintWait, FeintCancel, FeintConfirm, FeintRestart, ComboWait, ComboRestart, FlashPulse, Follow }

    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick | Agent.EventControlFlag.Mount
        | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear | Agent.EventControlFlag.Jump
        | Agent.EventControlFlag.ToggleAlternativeWeapon | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1
        | Agent.EventControlFlag.Wield2 | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;
    private const Agent.MovementControlFlag DefenseMask = Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock;
    private readonly object _gate = new();
    private Phase _phase;
    private object? _weapon;
    private object? _offhand;
    private int _target = -1;
    private bool _wasReady;
    private bool _wasRelease;
    private bool _swingBlocked;
    private bool _tryFlash;
    private bool _combo;
    private Agent.MovementControlFlag _first;
    private Agent.MovementControlFlag _next;
    private double _now;
    private double _until;
    private double _cooldownUntil;
    private double _afterHitUntil;
    private bool _afterHitAwaitAttack;
    private bool _afterHitSawReady;
    private long _snapshotUntil;

    public AdimiToolsAiArenaAttackTactics(Agent agent) : base(agent) { }

    // Other attack-selection components must yield once this component has
    // requested a cancel/restart, until the selected follow-up is released.
    public bool OwnsAttackInput
    {
        get
        {
            lock (_gate)
            {
                return Stopwatch.GetTimestamp() <= _snapshotUntil
                    && _phase is Phase.FeintCancel or Phase.FeintConfirm or Phase.FeintRestart or Phase.ComboRestart or Phase.FlashPulse or Phase.Follow;
            }
        }
    }

    public void Observe(double now, int targetIndex, object? weapon, object? offhand, bool eligible, bool threat,
        int difficulty, Agent.ActionCodeType action, Agent.ActionStage stage, Agent.UsageDirection direction, float progress,
        Agent.MovementControlFlag allowedAttacks, Agent.MovementControlFlag preferredSide, double feintRoll, double comboRoll,
        Agent.MovementControlFlag swingAttacks = Agent.MovementControlFlag.AttackMask)
    {
        lock (_gate)
        {
            if (!double.IsFinite(now) || now < 0d || now < _now)
            {
                ClearCore();
                return;
            }

            if (!eligible || threat || targetIndex < 0 || weapon == null
                || (difficulty != 2 && difficulty != 3) || !double.IsFinite(feintRoll) || !double.IsFinite(comboRoll))
            {
                ClearCore(preserveHitGrace: true, preserveBlockedSwing: true);
                return;
            }

            if (_target != targetIndex || !ReferenceEquals(_weapon, weapon) || !ReferenceEquals(_offhand, offhand))
            {
                ClearCore(preserveHitGrace: true, preserveBlockedSwing: true);
                _target = targetIndex;
                _weapon = weapon;
                _offhand = offhand;
            }

            _now = now;
            if (_afterHitAwaitAttack && now < _afterHitUntil)
            {
                _phase = Phase.Idle;
                _snapshotUntil = 0;
                if (action == Agent.ActionCodeType.ReadyMelee
                    && (stage is Agent.ActionStage.AttackReady or Agent.ActionStage.AttackQuickReady))
                    _afterHitSawReady = true;
                if (_afterHitSawReady && action == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease)
                {
                    _afterHitAwaitAttack = false;
                    _wasRelease = true;
                }
                return;
            }

            _afterHitAwaitAttack = false;

            long timestamp = Stopwatch.GetTimestamp();
            long duration = Stopwatch.Frequency / 10;
            _snapshotUntil = timestamp > long.MaxValue - duration ? long.MaxValue : timestamp + duration;
            allowedAttacks &= Agent.MovementControlFlag.AttackMask;
            swingAttacks &= allowedAttacks;
            Agent.MovementControlFlag current = Direction(direction);
            bool ready = action == Agent.ActionCodeType.ReadyMelee
                && (stage is Agent.ActionStage.AttackReady or Agent.ActionStage.AttackQuickReady);
            bool release = action == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease;
            if (ready && !_wasReady) _swingBlocked = false;
            bool neutral = stage == Agent.ActionStage.None
                && (action is Agent.ActionCodeType.Idle or Agent.ActionCodeType.Guard or Agent.ActionCodeType.Other);
            int actionValue = (int)action;
            bool defenseAnimation = actionValue >= (int)Agent.ActionCodeType.DefendAllBegin
                && actionValue < (int)Agent.ActionCodeType.DefendAllEnd && stage != Agent.ActionStage.DefendParry;

            if (_phase != Phase.Idle && now > _until)
            {
                Abort();
            }

            switch (_phase)
            {
                case Phase.FeintWait:
                    if (!ready || stage != Agent.ActionStage.AttackReady || current != _first) Abort();
                    else if (now >= _until - 0.30d) _phase = Phase.FeintCancel;
                    break;
                case Phase.FeintCancel:
                    if (!ready || stage != Agent.ActionStage.AttackReady || current != _first) Abort();
                    break;
                case Phase.FeintConfirm:
                    if (release || (!ready && !neutral && !defenseAnimation)) Abort();
                    else if (!ready && neutral) { _phase = Phase.FeintRestart; _until = now + 0.22d; }
                    // A brief defence animation is compatible with cancelling;
                    // wait for neutral and never overwrite native defence.
                    break;
                case Phase.FeintRestart:
                    if (ready && current == _next) { _phase = Phase.Follow; _until = now + 0.65d; }
                    else if (!neutral && !defenseAnimation) Abort();
                    break;
                case Phase.ComboRestart:
                    if (ready && current == _next)
                    {
                        // Experimental: the preceding callback requested the
                        // next swing (left-click). A quick-ready observation
                        // permits one short guard pulse (right-click).
                        _phase = _tryFlash && stage == Agent.ActionStage.AttackQuickReady ? Phase.FlashPulse : Phase.Follow;
                        _until = now + (_phase == Phase.FlashPulse ? 0.10d : 0.65d);
                    }
                    else if (!neutral && !release && !defenseAnimation) Abort();
                    break;
                case Phase.FlashPulse:
                    if (!ready || stage != Agent.ActionStage.AttackQuickReady || current != _next) Abort();
                    break;
                case Phase.ComboWait:
                    if (!release || !float.IsFinite(progress) || progress < 0f || progress > 1f) Abort();
                    else if (progress >= 0.55f && progress <= 0.90f) { _phase = Phase.ComboRestart; _until = now + 0.22d; }
                    else if (progress > 0.90f) Abort();
                    break;
                case Phase.Follow:
                    if (release || (!ready && !neutral)) Abort();
                    break;
            }

            if (_phase == Phase.Idle && now >= _cooldownUntil)
            {
                if (ready && !_wasReady && stage == Agent.ActionStage.AttackReady
                    && current != Agent.MovementControlFlag.None && (allowedAttacks & current) != 0
                    && feintRoll >= 0d && feintRoll < (difficulty == 3 ? 0.35d : 0.18d))
                {
                    Agent.MovementControlFlag next = ChooseNext(allowedAttacks, current, preferredSide);
                    if (next != Agent.MovementControlFlag.None)
                    {
                        _first = current;
                        _next = next;
                        _combo = false;
                        _phase = Phase.FeintWait;
                        // 0.10 seconds before a cancel, 0.40 seconds total.
                        _until = now + 0.40d;
                    }
                }
                else if (release && !_wasRelease && !_swingBlocked && (current is Agent.MovementControlFlag.AttackUp
                        or Agent.MovementControlFlag.AttackLeft or Agent.MovementControlFlag.AttackRight)
                    && (swingAttacks & current) != 0
                    && comboRoll >= 0d && comboRoll < (difficulty == 3 ? 0.45d : 0.25d))
                {
                    Agent.MovementControlFlag next = ChooseComboNext(swingAttacks, current, preferredSide);
                    if (next != Agent.MovementControlFlag.None)
                    {
                        _first = current;
                        _next = next;
                        _combo = true;
                        _phase = Phase.ComboWait;
                        // This is only an attempted input sequence, not a claim
                        // that the multiplayer animation will trigger on AI.
                        _tryFlash = comboRoll < (difficulty == 3 ? 0.16d : 0.08d);
                        _until = now + 0.9d;
                    }
                }
            }

            // Direction or equipment may change while the native animation is
            // still being evaluated. Never request an unsupported direction.
            if (_phase != Phase.Idle && (_next & allowedAttacks) == 0) Abort();
            if (_phase != Phase.Idle && _combo && (_next & swingAttacks) == 0) Abort();
            _wasReady = ready;
            _wasRelease = release;
        }
    }

    public void Clear()
    {
        lock (_gate) ClearCore();
    }

    // A transient target/range/weapon change must not erase the fact that an
    // already-started swing was blocked or that a hit counter is still due.
    public void Suspend()
    {
        lock (_gate) ClearCore(preserveHitGrace: true, preserveBlockedSwing: true);
    }

    public void NotifyReceivedHit(double now)
    {
        lock (_gate)
        {
            ClearCore();
            if (double.IsFinite(now) && now >= 0d)
            {
                _now = now;
                // Give the post-hit attack its own ready and release before a
                // later attack can be converted into a feint or combo.
                _afterHitAwaitAttack = true;
                _afterHitSawReady = false;
                _afterHitUntil = now + 3d;
            }
        }
    }

    public void NotifyAttackBlocked()
    {
        lock (_gate)
        {
            _swingBlocked = true;
            if (_phase != Phase.Idle) Abort();
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        lock (_gate)
        {
            if (Stopwatch.GetTimestamp() > _snapshotUntil || _phase == Phase.Idle) return;
            if ((eventFlag & InterruptingEvents) != 0 || (movementFlag & Agent.MovementControlFlag.Action) != 0)
            {
                Abort();
                return;
            }

            if (_phase == Phase.FeintConfirm) return;
            if ((movementFlag & DefenseMask) != 0)
            {
                // Back-swing guard, read guard and Native defences all win.
                Abort();
                return;
            }

            Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
            uint bits = (uint)attack;
            if (bits != 0 && (bits & (bits - 1u)) != 0) { Abort(); return; }
            if (_phase == Phase.FeintCancel)
            {
                if (attack == Agent.MovementControlFlag.None) { Abort(); return; }
                movementFlag = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | GuardFor(_first);
                _phase = Phase.FeintConfirm;
                _until = _now + 0.24d;
            }
            else if (_phase == Phase.FlashPulse)
            {
                // Only pulse while Native still supplies the same attack.
                // If it has already released or changed direction, inserting
                // a guard could cancel an unrelated swing: leave it alone.
                if (attack != _next)
                {
                    _tryFlash = false;
                    _phase = Phase.Follow;
                    _until = _now + 0.65d;
                    return;
                }

                movementFlag |= GuardFor(_next);
                _tryFlash = false;
                _phase = Phase.Follow;
                _until = _now + 0.65d;
            }
            else if (_phase is Phase.FeintRestart or Phase.ComboRestart)
            {
                movementFlag = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | _next;
            }
            else if (_phase == Phase.Follow && attack != Agent.MovementControlFlag.None)
            {
                movementFlag = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | _next;
            }
        }
    }

    private void Abort()
    {
        _phase = Phase.Idle;
        _first = Agent.MovementControlFlag.None;
        _next = Agent.MovementControlFlag.None;
        _tryFlash = false;
        _combo = false;
        _cooldownUntil = _now + 1.4d;
    }

    private void ClearCore(bool preserveHitGrace = false, bool preserveBlockedSwing = false)
    {
        double hitGrace = preserveHitGrace ? _afterHitUntil : 0d;
        bool awaitAttack = preserveHitGrace && _afterHitAwaitAttack;
        bool sawReady = preserveHitGrace && _afterHitSawReady;
        bool blockedSwing = preserveBlockedSwing && _swingBlocked;
        _phase = Phase.Idle;
        _target = -1;
        _weapon = null;
        _offhand = null;
        _wasReady = false;
        _wasRelease = false;
        _swingBlocked = blockedSwing;
        _tryFlash = false;
        _combo = false;
        _first = Agent.MovementControlFlag.None;
        _next = Agent.MovementControlFlag.None;
        _now = 0d;
        _until = 0d;
        _cooldownUntil = 0d;
        _afterHitUntil = hitGrace;
        _afterHitAwaitAttack = awaitAttack;
        _afterHitSawReady = sawReady;
        _snapshotUntil = 0;
    }

    private static Agent.MovementControlFlag ChooseComboNext(Agent.MovementControlFlag allowed,
        Agent.MovementControlFlag current, Agent.MovementControlFlag preferredSide)
    {
        if (current == Agent.MovementControlFlag.AttackUp)
        {
            Agent.MovementControlFlag sides = allowed & (Agent.MovementControlFlag.AttackLeft | Agent.MovementControlFlag.AttackRight);
            return ChooseNext(sides, current, preferredSide);
        }

        // Native quick-ready rejects directly opposite left/right swings.
        return (allowed & Agent.MovementControlFlag.AttackUp) != 0
            ? Agent.MovementControlFlag.AttackUp : Agent.MovementControlFlag.None;
    }

    internal static Agent.MovementControlFlag ChooseNext(Agent.MovementControlFlag allowed,
        Agent.MovementControlFlag current, Agent.MovementControlFlag preferredSide)
    {
        Agent.MovementControlFlag choices = allowed & Agent.MovementControlFlag.AttackMask & ~current;
        if ((choices & preferredSide) != 0 && preferredSide is Agent.MovementControlFlag.AttackLeft or Agent.MovementControlFlag.AttackRight)
            return preferredSide;
        foreach (Agent.MovementControlFlag candidate in new[] { Agent.MovementControlFlag.AttackUp,
            Agent.MovementControlFlag.AttackLeft, Agent.MovementControlFlag.AttackRight, Agent.MovementControlFlag.AttackDown })
            if ((choices & candidate) != 0) return candidate;
        return Agent.MovementControlFlag.None;
    }

    private static Agent.MovementControlFlag Direction(Agent.UsageDirection direction) => direction switch
    {
        Agent.UsageDirection.AttackUp => Agent.MovementControlFlag.AttackUp,
        Agent.UsageDirection.AttackDown => Agent.MovementControlFlag.AttackDown,
        Agent.UsageDirection.AttackLeft => Agent.MovementControlFlag.AttackLeft,
        Agent.UsageDirection.AttackRight => Agent.MovementControlFlag.AttackRight,
        _ => Agent.MovementControlFlag.None,
    };

    private static Agent.MovementControlFlag GuardFor(Agent.MovementControlFlag attack) => attack switch
    {
        Agent.MovementControlFlag.AttackLeft => Agent.MovementControlFlag.DefendLeft,
        Agent.MovementControlFlag.AttackRight => Agent.MovementControlFlag.DefendRight,
        Agent.MovementControlFlag.AttackDown => Agent.MovementControlFlag.DefendDown,
        _ => Agent.MovementControlFlag.DefendUp,
    };
}
