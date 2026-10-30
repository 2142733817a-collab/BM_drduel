using AdimiToolsShared;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using static AdimiDuel_Server.Duel.AdimiToolsMissionMultiplayerDuel;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsDuelFt7Anvil : SynchedMissionObject
{
    // Keep the original script name so existing FT7 scene entities remain compatible.
    protected override bool OnHit(Agent attackerAgent, int damage, Vec3 impactPosition, Vec3 impactDirection, in MissionWeapon weapon, ScriptComponentBehavior attackerScriptComponentBehavior, out bool reportDamage)
    {
        reportDamage = false;
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
                AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, $"You cannot change the duel mode mid duel.");
                return false;
            }

            if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig))
            {
                int anvilLimit = AdimiToolsDuelModes.GetAnvilLimit(GameEntity.Tags);
                if (anvilLimit == 0)
                {
                    return false;
                }

                duelConfig.FirstToLimit = AdimiToolsDuelModes.ToggleLimit(duelConfig.FirstToLimit, anvilLimit);
                if (duelConfig.FirstToLimit == 1)
                {
                    AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, "You are now in a first to 1 mode.");
                }
                else
                {
                    AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, $"You are now in a first to {duelConfig.FirstToLimit} mode. Hit this anvil again to return to first to 1, or use !ft 1-10.");
                }
            }
            else
            {
                AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, $"An error occured.");
            }
        }

        return false;
    }
}
