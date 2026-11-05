using AdimiToolsShared;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using static AdimiDuel_Server.Duel.AdimiToolsMissionMultiplayerDuel;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsDuelFt7Anvil : SynchedMissionObject
{
    // Keep the original script name so existing scene entities remain compatible.
    protected override bool OnHit(Agent attackerAgent, int damage, Vec3 impactPosition, Vec3 impactDirection, in MissionWeapon weapon, int affectorWeaponSlotOrMissileIndex, ScriptComponentBehavior attackerScriptComponentBehavior, out bool reportDamage, out float finalDamage, out float fireDamage, out float modifiedFireDamage)
    {
        reportDamage = false;
        finalDamage = damage;
        fireDamage = -1f;
        modifiedFireDamage = -1f;
        if (attackerAgent != null && attackerAgent.IsActive() && attackerAgent.IsHuman)
        {
            MissionPeer? missionPeer = attackerAgent.MissionPeer;
            NetworkCommunicator? networkPeer = missionPeer?.GetNetworkPeer();
            if (missionPeer == null || networkPeer == null)
            {
                return false;
            }

            if (missionPeer.Team?.IsDefender == true)
            {
                AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, "对局进行中，无法切换胜场设置。");
                return false;
            }

            if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig))
            {
                int anvilLimit = AdimiToolsDuelModes.GetInteractionLimit(GameEntity.Tags, duelConfig.FirstToLimit);
                if (anvilLimit == 0)
                {
                    return false;
                }

                duelConfig.FirstToLimit = anvilLimit;
                AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, $"已选择{AdimiToolsDuelModes.GetModeName(anvilLimit)}：先赢 {anvilLimit} 局获胜。一对一双方需一致；队战需全员一致，否则按单局。已开赛胜制保持不变。");
            }
            else
            {
                AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, "无法读取对局设置，请稍后重试。");
            }
        }

        return false;
    }
}
