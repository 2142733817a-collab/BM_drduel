using AdimiToolsShared;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsAiArenaCleanup
{
    public static bool TryClear(Mission mission, VolumeBox? area, bool clearUnoccupiedMounts = true)
    {
        if (area == null || !GameNetwork.IsServerOrRecorder)
        {
            return true;
        }

        int failures = 0;
        int removedAgents = 0;
        int removedItems = 0;
        bool canFadeCorpses = mission.GetMissionBehavior<MissionLobbyComponent>() is IAdimiToolsSingleRemovalLobby;
        Agent[] agents = mission.AllAgents.ToArray();
        var occupiedMounts = new HashSet<Agent>();
        bool riderSnapshotComplete = true;
        foreach (Agent agent in agents)
        {
            try
            {
                if (mission.AllAgents.Contains(agent) && agent.State != AgentState.Deleted
                    && agent.IsActive() && agent.Health > 0f && agent.MountAgent != null)
                {
                    occupiedMounts.Add(agent.MountAgent);
                }
            }
            catch (Exception exception)
            {
                failures++;
                riderSnapshotComplete = false;
                AdimiToolsConsoleLog.Log($"AI area rider snapshot will retry: {exception.Message}");
            }
        }

        foreach (Agent agent in agents)
        {
            try
            {
                // A previous native deletion can also remove another entry in
                // this snapshot. Do not touch that entry's native pointer again.
                if (!mission.AllAgents.Contains(agent) || agent.State == AgentState.Deleted || agent.IsFadingOut()
                    || occupiedMounts.Contains(agent) || (!riderSnapshotComplete && agent.IsMount))
                {
                    continue;
                }

                bool corpse = agent.State is AgentState.Killed or AgentState.Unconscious || agent.Health <= 0f;
                // An unknown lobby must retain its native corpse lifetime.
                // Fading an already removed human can re-enter death accounting.
                if (corpse && !canFadeCorpses)
                {
                    continue;
                }

                Agent? rider = agent.IsMount ? agent.RiderAgent : null;
                bool unoccupiedMount = clearUnoccupiedMounts && agent.IsMount
                    && (rider == null || !mission.AllAgents.Contains(rider) || !rider.IsActive() || rider.Health <= 0f);
                if ((corpse || unoccupiedMount) && area.IsPointIn(agent.Position))
                {
                    agent.FadeOut(hideInstantly: true, hideMount: false);
                    removedAgents++;
                }
            }
            catch (Exception exception)
            {
                failures++;
                AdimiToolsConsoleLog.Log($"AI area agent cleanup will retry: {exception.Message}");
            }
        }

        foreach (SpawnedItemEntity item in mission.ActiveMissionObjects.OfType<SpawnedItemEntity>().ToArray())
        {
            try
            {
                if (!item.CreatedAtRuntime || item.IsRemoved || item.IsDeactivated || item.HasUser || item.HasAIMovingTo
                    || !item.GameEntity.IsValid || !IsTransientItem(item) || !area.IsPointIn(item.GameEntity.GlobalPosition))
                {
                    continue;
                }

                // Corpse-attached missiles and dropped banners can have no
                // lifetime. Native deferred deletion only processes timed items.
                item.HasLifeTime = true;
                item.RequestDeletionOnNextTick();
                removedItems++;
            }
            catch (Exception exception)
            {
                failures++;
                AdimiToolsConsoleLog.Log($"AI area item cleanup will retry: {exception.Message}");
            }
        }

        if (removedAgents != 0 || removedItems != 0 || failures != 0)
        {
            AdimiToolsConsoleLog.Log($"AI area cleanup: agents={removedAgents}, items={removedItems}, retry failures={failures}.");
        }

        return failures == 0;
    }

    private static bool IsTransientItem(SpawnedItemEntity item)
    {
        return item.HasLifeTime || item.SpawnedOnACorpse
            || (item.SpawnFlags & (Mission.WeaponSpawnFlags.WithPhysics | Mission.WeaponSpawnFlags.AsMissile)) != 0;
    }
}
