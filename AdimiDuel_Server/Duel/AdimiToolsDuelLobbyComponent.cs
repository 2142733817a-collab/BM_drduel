using AdimiToolsShared;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.DedicatedCommunityServer;
using TaleWorlds.MountAndBlade.DedicatedCustomServer;

namespace AdimiDuel_Server.Duel;

// Native FadeOut can deliver a second Killed/Unconscious callback for a corpse.
// Keep native lobby services, but account for each human Agent's removal once.
internal static class AdimiToolsDuelLobbyComponent
{
    public static MissionLobbyComponent CreateBehavior()
    {
        return Protect(MissionLobbyComponent.CreateBehavior());
    }

    internal static MissionLobbyComponent Protect(MissionLobbyComponent original)
    {
        if (original.GetType() == typeof(MissionCustomGameServerComponent))
        {
            AdimiToolsConsoleLog.Log($"Duel lobby {original.GetType().FullName}: one-removal accounting enabled.");
            return new AdimiToolsCustomGameServerComponent();
        }

        if (original.GetType() == typeof(MissionCommunityServerComponent))
        {
            AdimiToolsConsoleLog.Log($"Duel lobby {original.GetType().FullName}: one-removal accounting enabled.");
            return new AdimiToolsCommunityServerComponent();
        }

        // Do not replace another mod's lobby or an uninspected native subtype.
        AdimiToolsConsoleLog.Log($"Duel lobby {original.GetType().FullName} retained; corpse cleanup uses the native lifetime.");
        return original;
    }
}

internal interface IAdimiToolsSingleRemovalLobby
{
}

internal sealed class AdimiToolsAgentRemovalLedger
{
    private readonly HashSet<Agent> _removed = new(ReferenceEqualityComparer.Instance);

    public bool TryBegin(Agent agent, AgentState state)
    {
        if (!GameNetwork.IsServer || agent == null || !agent.IsHuman
            || state is not (AgentState.Killed or AgentState.Unconscious or AgentState.Routed))
        {
            return true;
        }

        // Mark before calling native base logic, including reentrant callbacks.
        return _removed.Add(agent);
    }

    public void Forget(Agent agent)
    {
        _removed.Remove(agent);
    }

    public void Clear()
    {
        _removed.Clear();
    }
}

internal sealed class AdimiToolsCustomGameServerComponent : MissionCustomGameServerComponent, IAdimiToolsSingleRemovalLobby
{
    private readonly AdimiToolsAgentRemovalLedger _removals = new();

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
    {
        if (_removals.TryBegin(affectedAgent, agentState))
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, killingBlow);
        }
    }

    public override void OnAgentDeleted(Agent affectedAgent)
    {
        _removals.Forget(affectedAgent);
        base.OnAgentDeleted(affectedAgent);
    }

    public override void OnClearScene()
    {
        _removals.Clear();
        base.OnClearScene();
    }
}

internal sealed class AdimiToolsCommunityServerComponent : MissionCommunityServerComponent, IAdimiToolsSingleRemovalLobby
{
    private readonly AdimiToolsAgentRemovalLedger _removals = new();

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
    {
        if (_removals.TryBegin(affectedAgent, agentState))
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, killingBlow);
        }
    }

    public override void OnAgentDeleted(Agent affectedAgent)
    {
        _removals.Forget(affectedAgent);
        base.OnAgentDeleted(affectedAgent);
    }

    public override void OnClearScene()
    {
        _removals.Clear();
        base.OnClearScene();
    }
}
