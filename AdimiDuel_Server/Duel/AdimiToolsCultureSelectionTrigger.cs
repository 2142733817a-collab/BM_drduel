using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Local server hit observer, following the existing AI-terminal path. Original
// clients need neither this script nor a new synchronized mission-object ID.
internal sealed class AdimiToolsCultureSelectionTrigger : MissionObject
{
    private AdimiToolsCultureSelectionBehavior? _behavior;
    private string _cultureId = string.Empty;

    public void Bind(AdimiToolsCultureSelectionBehavior behavior, string cultureId)
    {
        _behavior = behavior;
        _cultureId = cultureId;
    }

    // Hit dispatch scans the entity's scripts directly. This observer must not
    // register a server-only MissionObject ID or publish its later removal.
    protected override void OnPreInit()
    {
    }

    protected override void OnRemoved(int removeReason)
    {
    }

    protected override bool OnHit(Agent attackerAgent, int damage, Vec3 impactPosition, Vec3 impactDirection, in MissionWeapon weapon, int affectorWeaponSlotOrMissileIndex, ScriptComponentBehavior attackerScriptComponentBehavior, out bool reportDamage, out float finalDamage, out float fireDamage, out float modifiedFireDamage)
    {
        reportDamage = false;
        finalDamage = 0f;
        fireDamage = -1f;
        modifiedFireDamage = -1f;
        if (_behavior?.CanReceiveHit(attackerAgent) == true && attackerAgent != null && attackerAgent.IsHuman && attackerAgent.IsActive()
            && attackerAgent.Health > 0f && !attackerAgent.IsFadingOut()
            && attackerAgent.Position.DistanceSquared(impactPosition) <= 36f)
        {
            _behavior?.OnTriggerHit(_cultureId, attackerAgent);
        }

        return false;
    }
}
