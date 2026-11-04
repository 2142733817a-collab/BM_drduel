using TaleWorlds.Engine;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsTeleportDoorStandingPoint : StandingPoint
{
    private GameEntity? _parentDoor;

    public override void OnUse(Agent userAgent, sbyte agentBoneIndex)
    {
        userAgent.StopUsingGameObject(isSuccessful: true, Agent.StopUsingGameObjectFlags.None);
        _parentDoor?.GetFirstScriptOfType<AdimiToolsTeleportDoors>()?.OnUse(userAgent, agentBoneIndex);
    }

    public override TextObject GetDescriptionText(WeakGameEntity gameEntity)
    {
        return new TextObject("Teleport");
    }

    public void SetParentDoor(GameEntity parentDoor)
    {
        _parentDoor = parentDoor;
    }

    protected override void OnInit()
    {
        base.OnInit();
    }
}
