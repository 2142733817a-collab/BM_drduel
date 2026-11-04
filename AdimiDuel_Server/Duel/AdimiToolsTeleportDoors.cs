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
                userAgent.TeleportToPosition(_targetPoint.GetGlobalFrame().origin);

                WorldPosition worldPosition = new(Mission.Current.Scene, _targetPoint.GetGlobalFrame().origin);
                WorldFrame worldFrame = new(_targetPoint.GetGlobalFrame().rotation, worldPosition);
                Vec2 vec = worldFrame.Rotation.f.AsVec2.Normalized();
                GameNetwork.BeginBroadcastModuleEvent();
                GameNetwork.WriteMessage(new AgentTeleportToFrame(userAgent.Index, worldFrame.Origin.GetGroundVec3(), vec));
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord, null);
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
}
