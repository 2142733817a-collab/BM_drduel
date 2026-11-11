using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Attach before every guard/attack component. The counter owns the bounded
// authorization; later guards always have the last word on a new threat.
internal sealed class AdimiToolsAiArenaParryGuardFilter : AgentComponent
{
    private volatile AdimiToolsAiArenaParryCounter? _counter;

    public AdimiToolsAiArenaParryGuardFilter(Agent agent)
        : base(agent)
    {
    }

    public void Bind(AdimiToolsAiArenaParryCounter? counter) => _counter = counter;

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
        ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
    {
        _counter?.FilterPassiveGuard(ref eventFlag, ref movementFlag);
    }
}
