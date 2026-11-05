using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace AdimiToolsShared;

public class AdimiToolsNotifier
{
    public static void SendMessageToAllPeers(string message)
    {
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new ServerMessage("[!]: " + message));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.IncludeUnsynchronizedClients);
    }

    public static void ServerSendMessageToPlayer(NetworkCommunicator targetPlayer, string message)
    {
        if (targetPlayer.IsServerPeer == false)
        {
            if (!targetPlayer.IsInBList || !targetPlayer.IsInCList)
            {
                GameNetwork.BeginModuleEventAsServer(targetPlayer);
                GameNetwork.WriteMessage(new ServerMessage("[!]: " + message, false));
                GameNetwork.EndModuleEventAsServer();
            }
        }
    }

    public static void ServerSendGuidanceToPlayer(NetworkCommunicator targetPlayer, string message, bool playSound = false)
    {
        if (targetPlayer.IsServerPeer || !targetPlayer.IsConnectionActive || !targetPlayer.IsSynchronized)
        {
            return;
        }

        // The stock ChatBox routes this packet to the ordinary left-hand log.
        // It does not use the admin alert or quick-information sound pipeline.
        GameNetwork.BeginModuleEventAsServer(targetPlayer);
        try
        {
            GameNetwork.WriteMessage(new ServerMessage(message, false, false));
        }
        finally
        {
            GameNetwork.EndModuleEventAsServer();
        }

        if (playSound)
        {
            TryPlayGuidanceSound(targetPlayer);
        }
    }

    public static void AdminAnnouncement(NetworkCommunicator? networkPeer, string message, bool isBroadcast)
    {
        if (networkPeer == null)
        {
            GameNetwork.BeginBroadcastModuleEvent();
            GameNetwork.WriteMessage(new ServerAdminMessage(message, isBroadcast));
            GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None, null);
            return;
        }

        GameNetwork.BeginModuleEventAsServer(networkPeer);
        GameNetwork.WriteMessage(new ServerAdminMessage(message, isBroadcast));
        GameNetwork.EndModuleEventAsServer();
    }

    private static void TryPlayGuidanceSound(NetworkCommunicator targetPlayer)
    {
        try
        {
            Mission? mission = Mission.Current;
            MissionPeer? peer = targetPlayer.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            if (mission == null || agent == null || !mission.AllAgents.Contains(agent)
                || agent.State == AgentState.Deleted || !agent.IsActive() || agent.Health <= 0f || !agent.IsHuman
                || !ReferenceEquals(agent.MissionPeer, peer)
                || !targetPlayer.IsConnectionActive || !targetPlayer.IsSynchronized)
            {
                return;
            }

            int sound = SoundEvent.GetEventIdFromString("event:/ui/notification/alert");
            if (sound >= 0)
            {
                // Stock hit-confirmation audio uses this same recipient-bound
                // native API. It does not enqueue any administrator text.
                mission.MakeSoundOnlyOnRelatedPeer(sound, agent.Position, agent.Index);
            }
        }
        catch (Exception exception)
        {
            // The text has already been delivered. An optional audio failure
            // must not cause the caller to repeat an old paragraph.
            AdimiToolsConsoleLog.Log($"Guidance audio skipped: {exception.Message}");
        }
    }
}
