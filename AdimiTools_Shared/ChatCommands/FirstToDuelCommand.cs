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
        Description = $"输入 {ChatCommandsComponent.CommandPrefix}{Name} [1到10的整数] 设置先赢多少局获胜。";
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
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "请输入 !ft 1-10。");
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "例如：!ft 7 表示先赢 7 局获胜。");
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
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, $"已切换为 FT{duelConfig.FirstToLimit}：先赢 {duelConfig.FirstToLimit} 局获胜。对手需要选择相同的胜场设置。");
        }
        else
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "无法读取对局设置，请稍后重试。");
        }
    }
}
