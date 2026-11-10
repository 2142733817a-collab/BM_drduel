using System;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// The mission thread plans and checks each step. The native input callback may
// run elsewhere, so it consumes only an immutable, short-lived input snapshot.
internal sealed class AdimiToolsAiArenaFootwork : AgentComponent
{
    private const double SafetyRecheckSeconds = 0.12d;
    private const long SnapshotMilliseconds = 100;
    private const Agent.MovementControlFlag TranslationMask = Agent.MovementControlFlag.Forward
        | Agent.MovementControlFlag.Backward | Agent.MovementControlFlag.StrafeLeft | Agent.MovementControlFlag.StrafeRight;
    private const Agent.EventControlFlag InterruptingEvents = Agent.EventControlFlag.Kick | Agent.EventControlFlag.Mount
        | Agent.EventControlFlag.Dismount | Agent.EventControlFlag.Rear | Agent.EventControlFlag.Jump
        | Agent.EventControlFlag.ToggleAlternativeWeapon | Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1
        | Agent.EventControlFlag.Wield2 | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 | Agent.EventControlFlag.Sheath1;

    private sealed class InputSnapshot
    {
        public InputSnapshot(float x, float y, bool requiresAttack, long expiresAt)
        {
            X = x;
            Y = y;
            RequiresAttack = requiresAttack;
            ExpiresAt = expiresAt;
        }

        public float X { get; }
        public float Y { get; }
        public bool RequiresAttack { get; }
        public long ExpiresAt { get; }
    }

    private readonly AdimiToolsAiArenaFootworkPolicy _policy = new();
    private volatile InputSnapshot? _snapshot;
    private double _nextSafetyCheck;
    private int _checkedTarget = -1;
    private float _checkedX;
    private float _checkedY;
    private float _checkedDistance;
    private double _unsafeSince = -1d;
    private double _unsafeUntil;

    public AdimiToolsAiArenaFootwork(Agent agent) : base(agent) { }

    public bool TryPlan(double now, int botIndex, int targetIndex, float distance, float weaponLength,
        out float toward, out float lateral, bool incomingKick = false, int difficulty = 3)
    {
        toward = 0f;
        lateral = 0f;
        // A rejection can originate from the counter approach before the
        // ordinary policy has ever initialized its bot/target identity.
        if (!MayStartCounterApproach(now)) return false;
        if (_policy.TryGetStep(now, botIndex, targetIndex, distance, weaponLength, out toward, out lateral, incomingKick, difficulty)) return true;
        Suspend();
        return false;
    }

    public bool NeedsSafetyCheck(double now, int targetIndex, float worldX, float worldY, float checkDistance = 1f)
    {
        float dot = worldX * _checkedX + worldY * _checkedY;
        return targetIndex != _checkedTarget || now >= _nextSafetyCheck
            || now < _nextSafetyCheck - SafetyRecheckSeconds || dot < 0.94f
            || !float.IsFinite(checkDistance) || checkDistance > _checkedDistance + 0.001f;
    }

    public void ReportSafety(double now, int targetIndex, float worldX, float worldY, bool safe, float checkDistance = 1f)
    {
        if (!safe)
        {
            _policy.Reject(now);
            if (double.IsFinite(now) && now >= 0d)
            {
                _unsafeSince = now;
                _unsafeUntil = now + 0.45d;
            }
            Suspend();
            _checkedTarget = -1;
            return;
        }

        _checkedTarget = targetIndex;
        _checkedX = worldX;
        _checkedY = worldY;
        _checkedDistance = checkDistance;
        _nextSafetyCheck = now + SafetyRecheckSeconds;
    }

    // Counter-kick approach uses its own short path, but cannot bypass a
    // previous rejected step simply by changing the target or movement mode.
    public bool MayStartCounterApproach(double now)
    {
        if (!double.IsFinite(now) || now < 0d) { Suspend(); return false; }
        if (_unsafeSince >= 0d && now < _unsafeSince)
        {
            _unsafeSince = -1d;
            _unsafeUntil = 0d;
        }
        if (now >= _unsafeUntil) return true;
        Suspend();
        return false;
    }

    public void Publish(float x, float y, bool requiresAttack)
    {
        float lengthSquared = x * x + y * y;
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.0001f || lengthSquared > 1.001f)
        {
            Suspend();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long duration = Stopwatch.Frequency * SnapshotMilliseconds / 1000;
        _snapshot = new InputSnapshot(x, y, requiresAttack, now > long.MaxValue - duration ? long.MaxValue : now + duration);
    }

    // Used for short attack/animation transitions; it does not restart a step.
    public void Suspend() => _snapshot = null;

    public void Clear()
    {
        _snapshot = null;
        _policy.Reset();
        _checkedTarget = -1;
        _checkedDistance = 0f;
        _nextSafetyCheck = 0d;
        _unsafeSince = -1d;
        _unsafeUntil = 0d;
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        InputSnapshot? snapshot = _snapshot;
        if (snapshot == null || Stopwatch.GetTimestamp() > snapshot.ExpiresAt
            || (eventFlag & InterruptingEvents) != 0 || (movementFlag & Agent.MovementControlFlag.Action) != 0
            || (snapshot.RequiresAttack && (movementFlag & Agent.MovementControlFlag.AttackMask) == 0))
        {
            return;
        }

        // Native combines the digital direction bits and the analogue input.
        // Remove only translation bits so opposite native movement cannot cancel
        // this step. Preserve turn, all attack/defend bits and every event bit.
        movementFlag &= ~TranslationMask;
        inputVector = new Vec2(snapshot.X, snapshot.Y);
    }
}
