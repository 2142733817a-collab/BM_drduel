using System.Threading;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Added before the guard components during agent construction. The later kick
// controller retains sole authority to emit a permitted counter-kick.
internal sealed class AdimiToolsAiArenaKickFilter : AgentComponent
{
    private AdimiToolsAiArenaKick? _kick;

    public AdimiToolsAiArenaKickFilter(Agent agent)
        : base(agent)
    {
    }

    public void Bind(AdimiToolsAiArenaKick kick)
    {
        kick.BindEarlyFilter();
        Volatile.Write(ref _kick, kick);
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,
        ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
    {
        Volatile.Read(ref _kick)?.FilterNativeKick(ref eventFlag, movementFlag);
    }
}
