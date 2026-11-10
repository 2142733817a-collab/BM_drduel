// Minimal input/action shapes; these do not emulate the Native game engine.
namespace TaleWorlds.Library
{
    public readonly struct Vec2
    {
        public Vec2(float x, float y) { this.x = x; this.y = y; }
        public readonly float x, y;
    }

    public readonly struct Vec3
    {
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public readonly float x, y, z;
    }
}

namespace TaleWorlds.MountAndBlade
{
    public sealed class Agent
    {
        [Flags]
        public enum MovementControlFlag : uint
        {
            None = 0, Forward = 0x1, Backward = 0x2, StrafeRight = 0x4, StrafeLeft = 0x8,
            TurnRight = 0x10, TurnLeft = 0x20,
            AttackLeft = 0x40, AttackRight = 0x80, AttackUp = 0x100, AttackDown = 0x200,
            DefendLeft = 0x400, DefendRight = 0x800, DefendUp = 0x1000, DefendDown = 0x2000,
            DefendAuto = 0x4000, DefendBlock = 0x8000, Action = 0x10000,
            AttackMask = 0x3C0, DefendMask = 0x7C00
        }

        [Flags]
        public enum EventControlFlag : uint
        {
            None = 0, Dismount = 0x1, Mount = 0x2, Rear = 0x4, Jump = 0x8,
            Wield0 = 0x10, Wield1 = 0x20, Wield2 = 0x40, Wield3 = 0x80,
            Sheath0 = 0x100, Sheath1 = 0x200, ToggleAlternativeWeapon = 0x400,
            Walk = 0x800, Run = 0x1000, Crouch = 0x2000, Stand = 0x4000, Kick = 0x8000
        }

        public enum ActionStage { None = -1, AttackReady, AttackQuickReady, AttackRelease,
            ReloadMidPhase, ReloadLastPhase, Defend, DefendParry, NumActionStages }
        public enum UsageDirection { None = -1, AttackUp, AttackDown, AttackLeft, AttackRight }
        public enum ActionCodeType
        {
            Other = 0, DefendFist = 1, DefendShield = 2, DefendForward2h = 3,
            DefendUp2h = 4, DefendRight2h = 5, DefendLeft2h = 6,
            DefendForward1h = 7, DefendUp1h = 8, DefendRight1h = 9, DefendLeft1h = 10,
            DefendForwardStaff = 11, DefendUpStaff = 12, DefendRightStaff = 13, DefendLeftStaff = 14,
            ReadyRanged = 15, ReleaseRanged = 16, ReleaseThrowing = 17, Reload = 18,
            ReadyMelee = 19, ReleaseMelee = 20, ParriedMelee = 21, BlockedMelee = 22,
            Fall = 23, JumpStart = 24, Jump = 25, JumpEnd = 26, JumpEndHard = 27,
            Kick = 28, KickContinue = 29, KickHit = 30, WeaponBash = 31, PassiveUsage = 32,
            EquipUnequip = 33, SwitchAlternative = 34, Idle = 35, Guard = 36,
            DefendAllBegin = DefendFist, DefendAllEnd = ReadyRanged
        }
    }

    public abstract class AgentComponent
    {
        protected AgentComponent(Agent agent) { }
        public virtual void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
            ref Agent.MovementControlFlag movementFlag, ref TaleWorlds.Library.Vec2 inputVector) { }
    }
}
