using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsTeamFriendlyFireTrigger : MissionObject
{
    private AdimiToolsTeamBattleBehavior? _behavior;
    private VolumeBox? _area;

    public void Bind(AdimiToolsTeamBattleBehavior behavior, VolumeBox area)
    {
        _behavior = behavior;
        _area = area;
    }

    // This is a local hit observer. The prop needs no new client-side script ID.
    protected override void OnPreInit()
    {
    }

    protected override void OnRemoved(int removeReason)
    {
    }

    protected override bool OnHit(Agent attackerAgent, int damage, Vec3 impactPosition, Vec3 impactDirection, in MissionWeapon weapon,
        int affectorWeaponSlotOrMissileIndex, ScriptComponentBehavior attackerScriptComponentBehavior, out bool reportDamage,
        out float finalDamage, out float fireDamage, out float modifiedFireDamage)
    {
        reportDamage = false;
        finalDamage = 0f;
        fireDamage = -1f;
        modifiedFireDamage = -1f;
        if (_area != null && attackerAgent != null && weapon.CurrentUsageItem is { IsMeleeWeapon: true, IsRangedWeapon: false }
            && attackerAgent.Position.DistanceSquared(impactPosition) <= 36f)
        {
            _behavior?.OnFriendlyFireToggleHit(_area, attackerAgent);
        }

        return false;
    }
}
