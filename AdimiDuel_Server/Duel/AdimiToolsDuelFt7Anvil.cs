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
        if (GameNetwork.IsServer && GameEntity.HasTag(AdimiToolsDuelModes.CycleTag))
        {
            bool meleeInRange = attackerAgent != null && weapon.CurrentUsageItem is { IsMeleeWeapon: true, IsRangedWeapon: false }
                && attackerAgent.Position.DistanceSquared(impactPosition) <= 36f;
            if (Mission.Current?.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.TryHandleModeSelectorHit(
                GameEntity.GlobalPosition, attackerAgent, meleeInRange) == true)
            {
                return false;
            }
        }

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
                AdimiToolsNotifier.ServerSendGuidanceToPlayer(networkPeer,
                    AdimiToolsDuelGuidanceText.GetModeFailure(AdimiToolsMessageLanguage.Select(
                        "对局进行中，无法切换胜场设置。", "The match format cannot be changed during a match.")));
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
                AdimiToolsNotifier.ServerSendGuidanceToPlayer(networkPeer,
                    AdimiToolsDuelGuidanceText.GetModeSelection(AdimiToolsDuelModes.GetModeName(anvilLimit)));
            }
            else
            {
                AdimiToolsNotifier.ServerSendGuidanceToPlayer(networkPeer,
                    AdimiToolsDuelGuidanceText.GetModeFailure(AdimiToolsMessageLanguage.Select(
                        "无法读取对局设置，请稍后重试。", "Match settings are unavailable. Please try again.")));
            }
        }

        return false;
    }
}
