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
        Description = $"输入 {ChatCommandsComponent.CommandPrefix}{Name} [1/2/3] 设置目标胜场：1=一局定胜负，2=三局两胜，3=五局三胜。";
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
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "请输入 !ft 1、!ft 2 或 !ft 3，数字表示先赢多少局。");
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "1=一局定胜负，2=三局两胜，3=五局三胜。");
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
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, $"已选择{AdimiDuel_Server.Duel.AdimiToolsDuelModes.GetModeName(input)}：先赢 {input} 局获胜。一对一双方需一致；队战需全员一致，否则按单局。已开赛胜制保持不变。");
        }
        else
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, "无法读取对局设置，请稍后重试。");
        }
    }
}
