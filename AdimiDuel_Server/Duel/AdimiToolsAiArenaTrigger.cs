using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Server-only hit observer. Do not derive from SynchedMissionObject: an original
// client has no matching script/ID for this component in its scene.
internal sealed class AdimiToolsAiArenaTrigger : MissionObject
{
    private AdimiToolsAiArenaBehavior? _arenaBehavior;
    private int _groupId;

    public void Bind(AdimiToolsAiArenaBehavior arenaBehavior, int groupId)
    {
        _arenaBehavior = arenaBehavior;
        _groupId = groupId;
    }

    protected override bool OnHit(Agent attackerAgent, int damage, Vec3 impactPosition, Vec3 impactDirection, in MissionWeapon weapon, int affectorWeaponSlotOrMissileIndex, ScriptComponentBehavior attackerScriptComponentBehavior, out bool reportDamage, out float finalDamage, out float fireDamage, out float modifiedFireDamage)
    {
        reportDamage = false;
        finalDamage = 0f;
        fireDamage = -1f;
        modifiedFireDamage = -1f;
        if (GameNetwork.IsServer && attackerAgent != null && attackerAgent.IsHuman && attackerAgent.IsActive())
        {
            // Only nearby human hits can reserve an arena. A distant projectile
            // must not steal the arena from the player standing at its terminal.
            if (attackerAgent.Position.DistanceSquared(impactPosition) <= 36f)
            {
                _arenaBehavior?.OnTriggerHit(_groupId, attackerAgent);
            }
        }

        return false;
    }
}
