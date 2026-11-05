using NetworkMessages.FromServer;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsTeleportDoors : UsableMissionObject
{
    private GameEntity? _targetPoint;

    public override void OnUse(Agent userAgent, sbyte agentBoneIndex)
    {
        if (userAgent != null && userAgent.IsActive() && userAgent.IsHuman)
        {
            MissionPeer? missionPeer = userAgent.MissionPeer;
            NetworkCommunicator? networkPeer = missionPeer?.GetNetworkPeer();
            if (missionPeer == null || networkPeer == null)
            {
                return;
            }

            if (_targetPoint != null)
            {
                TeleportToTarget(userAgent, _targetPoint);
            }
        }
    }

    public void SetupDoor(GameEntity targetPoint)
    {
        _targetPoint = targetPoint;
    }

    public override TextObject GetDescriptionText(WeakGameEntity gameEntity)
    {
        return new TextObject("Teleport");
    }

    internal static void TeleportToTarget(Agent userAgent, GameEntity targetPoint)
    {
        if (userAgent.MissionPeer != null
            && Mission.Current.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(userAgent.MissionPeer) == true)
        {
            return;
        }

        MatrixFrame frame = targetPoint.GetGlobalFrame();
        userAgent.TeleportToPosition(frame.origin);
        WorldPosition worldPosition = new(Mission.Current.Scene, frame.origin);
        WorldFrame worldFrame = new(frame.rotation, worldPosition);
        Vec2 direction = worldFrame.Rotation.f.AsVec2.Normalized();
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new AgentTeleportToFrame(userAgent.Index, worldFrame.Origin.GetGroundVec3(), direction));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord, null);
    }
}
