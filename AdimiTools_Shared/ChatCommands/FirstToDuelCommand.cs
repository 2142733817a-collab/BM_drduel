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
        Description = "输入 !1、!3.2 或 !5.3 切换赛制。";
        Overloads = new CommandOverload[]
        {
            new(new[] { ChatCommandParameterType.Int32 }, Execute),
        };
    }

    public bool TryExecuteShortcut(NetworkCommunicator fromPeer, string message)
    {
        int limit = message.Trim() switch
        {
            "!1" => 1,
            "!3.2" => 2,
            "!5.3" => 3,
            _ => 0,
        };
        if (limit == 0 || !CheckRequirements(fromPeer))
        {
            return false;
        }

        Execute(fromPeer, new object[] { limit });
        return true;
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
            AdimiToolsNotifier.ServerSendGuidanceToPlayer(fromPeer, AdimiDuel_Server.Duel.AdimiToolsDuelGuidanceText.GetModeUsage());
            return;
        }

        MissionPeer? missionPeer = fromPeer.GetComponent<MissionPeer>();
        if (missionPeer == null)
        {
            return;
        }

        if (missionPeer.Team?.IsDefender == true)
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "对局进行中，无法切换胜场设置。");
            return;
        }

        if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig))
        {
            duelConfig.FirstToLimit = input;
            AdimiToolsNotifier.ServerSendGuidanceToPlayer(fromPeer,
                AdimiDuel_Server.Duel.AdimiToolsDuelGuidanceText.GetModeSelection(AdimiDuel_Server.Duel.AdimiToolsDuelModes.GetModeName(input)));
        }
        else
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "无法读取对局设置，请稍后重试。");
        }
    }
}
