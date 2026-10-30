using TaleWorlds.MountAndBlade;
using static AdimiDuel_Server.Duel.AdimiToolsMissionMultiplayerDuel;

namespace AdimiToolsShared.ChatCommands;
internal class FirstToDuelCommand : ChatCommand
{
    public FirstToDuelCommand()
        : base()
    {
        Name = "ft";
        Alias = new string[] { "firstto" };
        Description = $"'{ChatCommandsComponent.CommandPrefix}{Name} [number between 1 and 10] to set your duel limit.";
        Overloads = new CommandOverload[]
        {
            new(new[] { ChatCommandParameterType.Int32 }, Execute),
        };
    }

    protected override bool CheckRequirements(NetworkCommunicator fromPeer)
    {
        MissionMultiplayerGameModeBase gamemode = Mission.Current.GetMissionBehavior<MissionMultiplayerGameModeBase>();
        if (gamemode is not AdimiDuel_Server.Duel.AdimiToolsMissionMultiplayerDuel)
        {
            return false;
        }

        return true;
    }

    private void Execute(NetworkCommunicator fromPeer, object[] arguments)
    {
        int input = (int)arguments[0];

        if (!AdimiDuel_Server.Duel.AdimiToolsDuelModes.IsValidLimit(input))
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "Use !ft 1-10");
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "Example: !ft 7 sets you into a first to 7 mode.");
            return;
        }

        MissionPeer? missionPeer = fromPeer.GetComponent<MissionPeer>();
        if (missionPeer == null)
        {
            return;
        }

        if (missionPeer.Team?.IsDefender == true)
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, $"You cannot change the duel mode mid duel.");
            return;
        }

        if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig))
        {
            duelConfig.FirstToLimit = input;
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, $"You are now in a first to {duelConfig.FirstToLimit} mode. Your opponent must select the same limit.");
        }
        else
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, $"An error occured.");
        }
    }
}
