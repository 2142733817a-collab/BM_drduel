using AdimiToolsShared;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsMountSafety
{
    public static bool IsLiving(Mission mission, Agent? agent)
    {
        try
        {
            return IsLivingKnown(mission, agent);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A false result means the native relationship could not be inspected
    // safely. Callers must defer both deletion and cached-team restoration.
    public static bool TryGetCurrentRider(Mission mission, Agent? mount, out Agent? rider)
    {
        rider = null;
        try
        {
            if (!IsPresent(mission, mount) || !mount!.IsMount || mount.IsFadingOut())
            {
                return false;
            }

            Agent? direct = mount.RiderAgent;
            if (direct != null)
            {
                if (!IsPresent(mission, direct))
                {
                    return false;
                }

                if (IsLivingKnown(mission, direct))
                {
                    if (!ReferenceEquals(direct.MountAgent, mount))
                    {
                        return false;
                    }

                    rider = direct;
                }
            }

            // Mount/rider links can briefly disagree during a change of horse.
            // Also protect a horse referenced by any current living actor.
            foreach (Agent actor in mission.AllAgents.ToArray())
            {
                if (ReferenceEquals(actor, mount) || !IsLivingKnown(mission, actor)
                    || !ReferenceEquals(actor.MountAgent, mount))
                {
                    continue;
                }

                if (rider != null && !ReferenceEquals(rider, actor))
                {
                    rider = null;
                    return false;
                }

                rider = actor;
            }

            return true;
        }
        catch (Exception exception)
        {
            rider = null;
            AdimiToolsConsoleLog.Log($"Mount ownership inspection deferred cleanup: {exception.Message}");
            return false;
        }
    }

    // Preserve each caller's existing living/corpse policy; this gate only
    // verifies a saved mount is still valid and no living actor occupies it.
    public static bool CanFade(Mission mission, Agent? mount)
        => TryGetCurrentRider(mission, mount, out Agent? rider) && rider == null;

    private static bool IsPresent(Mission mission, Agent? agent)
        => mission != null && !mission.IsFinalized && agent != null && mission.AllAgents.Contains(agent)
            && agent.State != AgentState.Deleted;

    private static bool IsLivingKnown(Mission mission, Agent? agent)
        => IsPresent(mission, agent) && agent!.IsActive() && agent.Health > 0f && !agent.IsFadingOut();
}
