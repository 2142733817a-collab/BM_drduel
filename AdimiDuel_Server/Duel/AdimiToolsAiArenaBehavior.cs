using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaBehavior : MissionLogic
{
    private sealed class Arena
    {
        public int Id { get; init; }
        public GameEntity Trigger { get; init; } = null!;
        public GameEntity Spawn { get; init; } = null!;
        public SynchedMissionObject? Barrier { get; init; }
        public VolumeBox? Area { get; init; }
        public MatrixFrame LoweredFrame { get; init; }
        public AdimiToolsAiArenaSession<MissionPeer> Session { get; } = new();
        public Agent? OwnerAgent { get; set; }
        public Agent? OwnerMount { get; set; }
        public Team? OriginalMountTeam { get; set; }
        public string? MountKey { get; set; }
        public Team? OriginalTeam { get; set; }
        public Team? PlayerTeam { get; set; }
        public Team? BotTeam { get; set; }
        public Equipment? Equipment { get; set; }
        public AdimiToolsAiArenaSeries? Series { get; set; }
        public MatrixFrame OwnerRespawnFrame { get; set; }
        public bool RoundPending { get; set; }
        public bool AwaitingRespawn { get; set; }
        public bool OwnerDiedThisRound { get; set; }
        public bool RespawnNoticePending { get; set; }
        public double NextRoundAt { get; set; }
        public double RespawnDeadline { get; set; }
        public float HealthLimit { get; set; }
        public float BaseHealthLimit { get; set; }
        public float ArmorHead { get; set; }
        public float ArmorTorso { get; set; }
        public float ArmorArms { get; set; }
        public float ArmorLegs { get; set; }
        public List<Agent> RespawnedPlayerMounts { get; } = new();
        public List<Agent> Bots { get; } = new();
        public List<Agent> Mounts { get; } = new();
        public List<Vec3> SpawnPositions { get; } = new();
        public double LoweringUntil { get; set; }
        public bool BarrierEngaged { get; set; }
        public bool Resetting { get; set; }
        public bool Failed { get; set; }
        public bool CleanupPending { get; set; }
        public MissionPeer? CleanupOwner { get; set; }
    }

    private const float CheckInterval = 0.25f;
    private readonly Dictionary<int, Arena> _arenas = new();
    private AdimiToolsMissionMultiplayerDuel? _duelMode;
    private double _nextCheckAt;
    private bool _ending;

    public override void AfterStart()
    {
        base.AfterStart();
        _duelMode = Mission.GetMissionBehavior<AdimiToolsMissionMultiplayerDuel>();
        InitializeGroups();
    }

    public bool IsPeerReserved(MissionPeer peer)
    {
        return _arenas.Values.Any(arena => (arena.Session.IsReserved && ReferenceEquals(arena.Session.Owner, peer))
            || (arena.CleanupPending && ReferenceEquals(arena.CleanupOwner, peer)));
    }

    public MatrixFrame? GetRoundRespawnFrame(MissionPeer peer)
    {
        Arena? arena = FindRespawningArena(peer);
        return arena?.OwnerRespawnFrame;
    }

    public bool ShouldDelayRespawn(MissionPeer peer)
    {
        Arena? arena = FindRespawningArena(peer);
        return arena != null && (Mission.CurrentTime < arena.NextRoundAt || !PreparePlayerRespawnFrame(arena));
    }

    public void ConfigureRoundRespawn(MissionPeer peer, AgentBuildData buildData)
    {
        Arena? arena = FindRespawningArena(peer);
        if (arena?.Equipment == null || arena.OwnerAgent == null)
        {
            return;
        }

        buildData.Character(arena.OwnerAgent.Character)
            .Equipment(arena.Equipment.Clone())
            .FixedEquipment(true)
            .NoHorses(arena.Equipment[EquipmentIndex.Horse].IsEmpty);
        if (!string.IsNullOrEmpty(arena.MountKey))
        {
            buildData.MountKey(arena.MountKey);
        }
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        MissionPeer? peer = agent.MissionPeer;
        Arena? arena = peer == null ? null : FindRespawningArena(peer);
        if (arena == null || !agent.IsHuman || agent.Team != arena.PlayerTeam)
        {
            return;
        }

        arena.OwnerAgent = agent;
        arena.AwaitingRespawn = false;
        arena.RespawnNoticePending = true;
        arena.NextRoundAt = Mission.CurrentTime + AdimiToolsAiArenaSession<MissionPeer>.CountdownSeconds;
        if (agent.MountAgent != null)
        {
            arena.RespawnedPlayerMounts.Add(agent.MountAgent);
        }

        _nextCheckAt = 0d;
    }

    public void OnTriggerHit(int groupId, Agent agent)
    {
        MissionPeer? peer = agent.MissionPeer;
        NetworkCommunicator? networkPeer = peer?.GetNetworkPeer();
        if (_ending || peer == null || networkPeer == null || !networkPeer.IsSynchronized || !networkPeer.IsConnectionActive
            || !ReferenceEquals(peer.ControlledAgent, agent) || !agent.IsActive() || agent.Health <= 0f
            || peer.Team == null || peer.Team == Mission.SpectatorTeam
            || !_arenas.TryGetValue(groupId, out Arena? arena) || arena.Failed)
        {
            return;
        }

        double now = Mission.CurrentTime;
        if (now < arena.LoweringUntil || (arena.Barrier != null && !arena.Session.IsReserved && !arena.Barrier.SynchronizeCompleted))
        {
            Notify(peer, "障碍物正在复位，请稍后再试。");
            return;
        }

        if (_duelMode?.IsPeerBusyForAiChallenge(peer) == true || (!arena.Session.IsReserved && IsPeerReserved(peer)))
        {
            Notify(peer, "请先结束当前决斗或 AI 挑战。");
            return;
        }

        if (arena.Area != null && !arena.Area.IsPointIn(agent.Position))
        {
            Notify(peer, "请进入本组训练区域后再敲击。");
            return;
        }

        if (arena.Area != null && !HasOutsideRespawn(arena))
        {
            Notify(peer, "本组区域外没有可用复活点，暂时无法开始挑战。");
            return;
        }

        if (arena.Area != null && HasBusyIntruder(arena, peer))
        {
            Notify(peer, "区域内有正在决斗或训练的玩家，请等对方结束后再试。");
            return;
        }

        AdimiToolsAiArenaHitResult result = arena.Session.TryHit(peer, now);
        if (result == AdimiToolsAiArenaHitResult.OwnedByAnother || result == AdimiToolsAiArenaHitResult.AlreadyRunning)
        {
            Notify(peer, "本组 AI 挑战正在使用中。");
            return;
        }

        if (result == AdimiToolsAiArenaHitResult.MaximumReached)
        {
            Notify(peer, $"一次最多生成 {AdimiToolsAiArenaSession<MissionPeer>.MaximumAiCount} 个 AI。");
            return;
        }

        if (result != AdimiToolsAiArenaHitResult.Reserved && result != AdimiToolsAiArenaHitResult.Counted)
        {
            return;
        }

        try
        {
            if (result == AdimiToolsAiArenaHitResult.Reserved)
            {
                arena.OwnerAgent = agent;
                arena.OriginalTeam = peer.Team;
                arena.Equipment = SnapshotEquipment(agent);
                arena.MountKey = agent.MountAgent?.HorseCreationKey;
                arena.OwnerRespawnFrame = arena.Spawn.GetGlobalFrame();
                arena.HealthLimit = agent.HealthLimit;
                arena.BaseHealthLimit = agent.BaseHealthLimit;
                arena.ArmorHead = agent.AgentDrivenProperties.ArmorHead;
                arena.ArmorTorso = agent.AgentDrivenProperties.ArmorTorso;
                arena.ArmorArms = agent.AgentDrivenProperties.ArmorArms;
                arena.ArmorLegs = agent.AgentDrivenProperties.ArmorLegs;
                MoveBarrier(arena, raised: true);
            }

            Notify(peer, $"3 秒后生成 {arena.Session.RequestedCount} 个与你当前兵种、装备相同的 AI。");
        }
        catch (Exception exception)
        {
            FailArena(arena, exception);
        }
    }

    public override void OnMissionTick(float dt)
    {
        if (_ending || Mission.CurrentTime < _nextCheckAt)
        {
            return;
        }

        double now = Mission.CurrentTime;
        _nextCheckAt = now + CheckInterval;
        foreach (Arena arena in _arenas.Values)
        {
            if (arena.CleanupPending)
            {
                ResetArena(arena, null);
                continue;
            }

            if (!arena.Session.IsReserved || arena.Resetting || arena.Failed)
            {
                continue;
            }

            try
            {
                MissionPeer? owner = arena.Session.Owner;
                Agent? ownerAgent = arena.OwnerAgent;
                NetworkCommunicator? networkPeer = owner?.GetNetworkPeer();
                if (owner == null || ownerAgent == null || networkPeer == null || !networkPeer.IsConnectionActive
                    || !networkPeer.IsSynchronized
                    || owner.Team == null || owner.Team == Mission.SpectatorTeam
                    || (arena.Session.State == AdimiToolsAiArenaState.CountingDown && owner.Team != arena.OriginalTeam)
                    || (arena.Session.State == AdimiToolsAiArenaState.Running && owner.Team != arena.PlayerTeam))
                {
                    ResetArena(arena, "AI 挑战已结束，区域恢复开放。");
                    continue;
                }

                // Area protection remains active while waiting for a series respawn.
                if (arena.Area != null && !EvictOtherPlayers(arena, owner))
                {
                    ResetArena(arena, "区域无法清空或区域外没有可用复活点，本次挑战已取消。");
                    continue;
                }

                if (arena.Session.State == AdimiToolsAiArenaState.Running)
                {
                    if (arena.RoundPending)
                    {
                        if (arena.AwaitingRespawn)
                        {
                            if (now > arena.RespawnDeadline)
                            {
                                ResetArena(arena, "等待复活超时，本次 AI 系列已取消。");
                            }

                            continue;
                        }

                        if (!ownerAgent.IsActive() || ownerAgent.Health <= 0f || !ReferenceEquals(owner.ControlledAgent, ownerAgent))
                        {
                            ResetArena(arena, "玩家状态发生变化，本次 AI 系列已取消。");
                            continue;
                        }

                        if (arena.RespawnNoticePending || !arena.Bots.Any(bot => bot.IsActive() && bot.Health > 0f))
                        {
                            arena.RespawnNoticePending = false;
                            if (!PreparePendingRound(arena, owner, ownerAgent, now))
                            {
                                continue;
                            }
                        }
                    }
                    else if (arena.OwnerDiedThisRound || !ownerAgent.IsActive() || ownerAgent.Health <= 0f)
                    {
                        ResolveRound(arena, playerWon: false, now);
                        continue;
                    }
                    else if (!arena.Bots.Any(bot => bot.IsActive() && bot.Health > 0f))
                    {
                        ResolveRound(arena, playerWon: true, now);
                        continue;
                    }
                }

                if (!ownerAgent.IsActive() || ownerAgent.Health <= 0f || !ReferenceEquals(owner.ControlledAgent, ownerAgent)
                    || (arena.Area != null && !arena.Area.IsPointIn(ownerAgent.Position)))
                {
                    ResetArena(arena, "AI 挑战已结束，区域恢复开放。");
                    continue;
                }

                if (arena.Session.ShouldStart(now))
                {
                    StartChallenge(arena, owner, ownerAgent, now);
                }

                if (arena.Session.State == AdimiToolsAiArenaState.Running)
                {
                    if (arena.RoundPending)
                    {
                        if (now >= arena.NextRoundAt)
                        {
                            StartNextRound(arena, owner, ownerAgent);
                        }

                        continue;
                    }

                    foreach (Agent bot in arena.Bots.Where(bot => bot.IsActive() && bot.Health > 0f))
                    {
                        bot.SetAutomaticTargetSelection(false);
                        bot.SetTargetAgent(ownerAgent);
                        if (arena.Area != null && !arena.Area.IsPointIn(bot.Position))
                        {
                            Teleport(bot, arena.Spawn.GetGlobalFrame());
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                FailArena(arena, exception);
            }
        }
    }

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
    {
        // Defer all engine mutations to OnMissionTick, outside native removal callbacks.
        foreach (Arena arena in _arenas.Values)
        {
            if (ReferenceEquals(arena.OwnerAgent, affectedAgent) || arena.Bots.Contains(affectedAgent))
            {
                if (ReferenceEquals(arena.OwnerAgent, affectedAgent) && !arena.RoundPending)
                {
                    arena.OwnerDiedThisRound = true;
                }

                _nextCheckAt = 0d;
            }
        }
    }

    public override void OnClearScene()
    {
        CleanupAll();
        base.OnClearScene();
    }

    public override void OnRemoveBehavior()
    {
        CleanupAll();
        base.OnRemoveBehavior();
    }

    protected override void OnEndMission()
    {
        CleanupAll();
        base.OnEndMission();
    }

    private static Equipment SnapshotEquipment(Agent owner)
    {
        Equipment equipment = owner.SpawnEquipment.Clone();
        for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
        {
            MissionWeapon weapon = owner.Equipment[slot];
            equipment[slot] = weapon.IsEmpty ? EquipmentElement.Invalid : new EquipmentElement(weapon.Item, weapon.ItemModifier);
        }

        if (owner.MountAgent == null)
        {
            equipment[EquipmentIndex.Horse] = EquipmentElement.Invalid;
            equipment[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
        }
        else
        {
            equipment[EquipmentIndex.Horse] = owner.MountAgent.SpawnEquipment[EquipmentIndex.Horse];
            equipment[EquipmentIndex.HorseHarness] = owner.MountAgent.SpawnEquipment[EquipmentIndex.HorseHarness];
        }

        return equipment;
    }

    private static void Teleport(Agent agent, MatrixFrame frame)
    {
        Vec2 direction = frame.rotation.f.AsVec2.Normalized();
        if (direction.LengthSquared < 0.1f)
        {
            direction = new Vec2(0f, 1f);
        }

        Agent? mount = agent.MountAgent;
        if (mount != null && mount.IsActive())
        {
            mount.TeleportToPosition(frame.origin);
        }

        agent.TeleportToPosition(frame.origin);
        WorldPosition position = new(Mission.Current.Scene, frame.origin);
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new AgentTeleportToFrame(agent.Index, position.GetGroundVec3(), direction));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
    }

    private static void Notify(MissionPeer peer, string message)
    {
        NetworkCommunicator? networkPeer = peer.GetNetworkPeer();
        if (networkPeer != null && networkPeer.IsConnectionActive)
        {
            GameNetwork.BeginModuleEventAsServer(networkPeer);
            try
            {
                GameNetwork.WriteMessage(new ServerAdminMessage(message, true));
            }
            finally
            {
                GameNetwork.EndModuleEventAsServer();
            }
        }
    }

    private static void MoveBarrier(Arena arena, bool raised, bool immediately = false)
    {
        if (arena.Barrier == null)
        {
            return;
        }

        MatrixFrame frame = arena.LoweredFrame;
        if (raised)
        {
            arena.BarrierEngaged = true;
            frame.origin.z += AdimiToolsAiArenaSession<MissionPeer>.BarrierRiseMeters;
        }

        if (immediately)
        {
            arena.Barrier.SetGlobalFrameSynched(ref frame);
        }
        else
        {
            arena.Barrier.SetGlobalFrameSynchedOverTime(ref frame, AdimiToolsAiArenaSession<MissionPeer>.BarrierTravelSeconds);
        }
    }

    private static void AttemptCleanup(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void PrepareRoundOwner(Arena arena, Agent ownerAgent)
    {
        ownerAgent.HealthLimit = arena.HealthLimit;
        ownerAgent.BaseHealthLimit = arena.BaseHealthLimit;
        AdimiToolsMissionMultiplayerDuel.RestoreAgentForAiChallenge(ownerAgent);
        ownerAgent.AgentDrivenProperties.ArmorHead = arena.ArmorHead;
        ownerAgent.AgentDrivenProperties.ArmorTorso = arena.ArmorTorso;
        ownerAgent.AgentDrivenProperties.ArmorArms = arena.ArmorArms;
        ownerAgent.AgentDrivenProperties.ArmorLegs = arena.ArmorLegs;
        ownerAgent.UpdateCustomDrivenProperties();
    }

    private void InitializeGroups()
    {
        string[] roles = { AdimiToolsAiArenaTags.TriggerRoleTag, AdimiToolsAiArenaTags.SpawnRoleTag, AdimiToolsAiArenaTags.BarrierRoleTag, AdimiToolsAiArenaTags.AreaRoleTag };
        var entities = roles.SelectMany(role => Mission.Scene.FindEntitiesWithTag(role)).Distinct().ToList();
        var groups = new Dictionary<int, List<GameEntity>>();
        var invalidGroups = new HashSet<int>();
        foreach (GameEntity entity in entities)
        {
            string[] tags = entity.Tags.Where(tag => tag.StartsWith(AdimiToolsAiArenaTags.GroupPrefix, StringComparison.Ordinal)).ToArray();
            int[] groupIds = tags.Where(tag => AdimiToolsAiArenaTags.TryParseGroupTag(tag, out _)).Select(tag =>
            {
                AdimiToolsAiArenaTags.TryParseGroupTag(tag, out int id);
                return id;
            }).ToArray();
            if (tags.Length != 1 || groupIds.Length != 1 || roles.Count(entity.HasTag) != 1)
            {
                foreach (int id in groupIds)
                {
                    invalidGroups.Add(id);
                }

                AdimiToolsConsoleLog.Log($"AI arena skipped entity '{entity.Name}': use exactly one role and one canonical group tag.");
                continue;
            }

            if (!groups.TryGetValue(groupIds[0], out List<GameEntity>? members))
            {
                members = new List<GameEntity>();
                groups.Add(groupIds[0], members);
            }

            members.Add(entity);
        }

        foreach (var group in groups)
        {
            List<GameEntity> triggers = group.Value.Where(entity => entity.HasTag(roles[0])).ToList();
            List<GameEntity> spawns = group.Value.Where(entity => entity.HasTag(roles[1])).ToList();
            List<GameEntity> barriers = group.Value.Where(entity => entity.HasTag(roles[2])).ToList();
            List<GameEntity> areas = group.Value.Where(entity => entity.HasTag(roles[3])).ToList();
            if (invalidGroups.Contains(group.Key) || !AdimiToolsAiArenaTags.IsValidRoleCounts(triggers.Count, spawns.Count, barriers.Count, areas.Count))
            {
                AdimiToolsConsoleLog.Log($"AI group {group.Key} disabled: needs one trigger and one spawn; barrier/area may each be omitted or appear once.");
                continue;
            }

            SynchedMissionObject? barrier = barriers.Count == 0 ? null : barriers[0].GetFirstScriptOfType<SynchedMissionObject>();
            VolumeBox? area = areas.Count == 0 ? null : areas[0].GetFirstScriptOfType<VolumeBox>();
            if ((barriers.Count != 0 && (barrier == null || barrier.GetType() != typeof(SynchedMissionObject) || barrier.Id.Id < 0))
                || (areas.Count != 0 && area == null)
                || triggers[0].HasTag("teleport_door") || AdimiToolsDuelModes.GetAnvilLimit(triggers[0].Tags) != 0
                || spawns[0].HasTag("spawnpoint"))
            {
                AdimiToolsConsoleLog.Log($"AI group {group.Key} disabled: invalid optional script, conflicting trigger tag, or AI marker also used as a player spawnpoint.");
                continue;
            }

            if (area != null)
            {
                Vec3 scale = areas[0].GetGlobalFrame().rotation.GetScaleVector();
                if (scale.x <= 0.01f || scale.y <= 0.01f || scale.z <= 0.01f || !area.IsPointIn(spawns[0].GlobalPosition))
                {
                    AdimiToolsConsoleLog.Log($"AI group {group.Key} disabled: invalid volume scale or AI spawn outside volume.");
                    continue;
                }
            }

            var arena = new Arena
            {
                Id = group.Key,
                Trigger = triggers[0],
                Spawn = spawns[0],
                Barrier = barrier,
                Area = area,
                LoweredFrame = barrier == null ? MatrixFrame.Identity : barriers[0].GetGlobalFrame(),
            };
            try
            {
                AdimiToolsAiArenaTrigger? trigger = arena.Trigger.GetFirstScriptOfType<AdimiToolsAiArenaTrigger>();
                if (trigger == null)
                {
                    arena.Trigger.CreateAndAddScriptComponent(nameof(AdimiToolsAiArenaTrigger), callScriptCallbacks: true);
                    trigger = arena.Trigger.GetFirstScriptOfType<AdimiToolsAiArenaTrigger>();
                }

                if (trigger == null)
                {
                    throw new InvalidOperationException("Could not attach AI hit observer.");
                }

                trigger.Bind(this, arena.Id);
                _arenas.Add(arena.Id, arena);
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} ready; barrier={barrier != null}, area={area != null}.");
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} initialization failed: {exception}");
            }
        }
    }

    private Arena? FindRespawningArena(MissionPeer peer)
    {
        return _arenas.Values.FirstOrDefault(arena => !arena.Failed && arena.Session.IsReserved
            && ReferenceEquals(arena.Session.Owner, peer) && arena.Series?.IsComplete == false
            && arena.RoundPending && arena.AwaitingRespawn);
    }

    private void StartChallenge(Arena arena, MissionPeer owner, Agent ownerAgent, double now)
    {
        if (arena.Equipment == null || !PrepareSpawnPositions(arena, ownerAgent))
        {
            ResetArena(arena, "AI 出生点附近没有足够的可用导航网格，请调整地图后重试。");
            return;
        }

        EnsureTeams(arena);
        if (!arena.Session.MarkStarted(now))
        {
            return;
        }

        arena.Series = new AdimiToolsAiArenaSeries(arena.Session.RequestedCount, _duelMode?.GetFirstToLimitForAiChallenge(owner) ?? 1);
        arena.RoundPending = false;
        arena.OwnerDiedThisRound = false;

        arena.OwnerMount = ownerAgent.MountAgent;
        arena.OriginalMountTeam = arena.OwnerMount?.Team;
        owner.Team = arena.PlayerTeam;
        ownerAgent.SetTeam(arena.PlayerTeam!, sync: true);
        arena.OwnerMount?.SetTeam(arena.PlayerTeam!, sync: true);
        PrepareRoundOwner(arena, ownerAgent);
        SpawnRoundBots(arena, ownerAgent);
        Notify(owner, $"已生成 {arena.Bots.Count} 个 AI，只会与你交战。本次 FT{arena.Series.FirstToLimit}，先胜 {arena.Series.FirstToLimit} 局结束。");
    }

    private void SpawnRoundBots(Arena arena, Agent ownerAgent, bool activate = true)
    {
        arena.PlayerTeam!.SetIsEnemyOf(arena.BotTeam!, activate);
        arena.BotTeam!.SetIsEnemyOf(arena.PlayerTeam, activate);

        foreach (Vec3 position in arena.SpawnPositions)
        {
            Vec2 direction = (ownerAgent.Position - position).AsVec2.Normalized();
            if (direction.LengthSquared < 0.1f)
            {
                direction = new Vec2(0f, 1f);
            }

            var build = new AgentBuildData(ownerAgent.Character)
                .Team(arena.BotTeam!)
                .Controller(AgentControllerType.AI)
                .TroopOrigin(new BasicBattleAgentOrigin(ownerAgent.Character))
                .Equipment(arena.Equipment!.Clone())
                .FixedEquipment(true)
                .NoHorses(arena.Equipment[EquipmentIndex.Horse].IsEmpty)
                .BodyProperties(ownerAgent.BodyPropertiesValue)
                .IsFemale(ownerAgent.IsFemale)
                .ClothingColor1(ownerAgent.ClothingColor1)
                .ClothingColor2(ownerAgent.ClothingColor2)
                .InitialPosition(in position)
                .InitialDirection(in direction);
            if (!string.IsNullOrEmpty(arena.MountKey))
            {
                build.MountKey(arena.MountKey);
            }

            // Never attach MissionPeer(owner): that would replace the human's
            // ControlledAgent. Each AI has independent equipment and ownership.
            Agent bot = Mission.SpawnAgent(build, spawnFromAgentVisuals: false);
            arena.Bots.Add(bot);
            if (bot.MountAgent != null)
            {
                arena.Mounts.Add(bot.MountAgent);
            }

            bot.HealthLimit = arena.HealthLimit;
            bot.BaseHealthLimit = arena.BaseHealthLimit;
            bot.Health = bot.HealthLimit;
            // MP armor bonuses normally come from a player's MissionPeer. These
            // independent bots must not claim that peer just to obtain armor.
            bot.AgentDrivenProperties.ArmorHead = arena.ArmorHead;
            bot.AgentDrivenProperties.ArmorTorso = arena.ArmorTorso;
            bot.AgentDrivenProperties.ArmorArms = arena.ArmorArms;
            bot.AgentDrivenProperties.ArmorLegs = arena.ArmorLegs;
            bot.UpdateCustomDrivenProperties();
            bot.WieldInitialWeapons();
            bot.SetWatchState(Agent.WatchState.Alarmed);
            bot.SetAutomaticTargetSelection(false);
            bot.SetTargetAgent(activate ? ownerAgent : null!);
        }
    }

    private void ResolveRound(Arena arena, bool playerWon, double now)
    {
        if (arena.RoundPending || arena.Series == null)
        {
            return;
        }

        arena.RoundPending = true; // Lock this round before any removal callbacks.
        bool finished = arena.Series.RecordRound(playerWon);
        if (finished)
        {
            ResetArena(arena, $"AI 系列结束：你 {arena.Series.PlayerWins}:{arena.Series.AiWins} AI，{(arena.Series.WinnerPlayer == true ? "你获胜" : "AI 获胜")}。区域恢复开放。");
            return;
        }

        arena.PlayerTeam!.SetIsEnemyOf(arena.BotTeam!, false);
        arena.BotTeam!.SetIsEnemyOf(arena.PlayerTeam, false);
        arena.AwaitingRespawn = !playerWon;
        PrepareRoundSurvivors(arena);
        arena.NextRoundAt = now + AdimiToolsAiArenaSession<MissionPeer>.CountdownSeconds;
        arena.RespawnDeadline = now + 30d;
        arena.OwnerDiedThisRound = false;
        if (playerWon)
        {
            PreparePendingRound(arena, arena.Session.Owner!, arena.OwnerAgent!, now);
            return;
        }
        else if (arena.OwnerMount?.IsActive() == true && arena.OwnerMount.Team == arena.PlayerTeam)
        {
            // An original player horse may outlive its rider. It is not an AI
            // cleanup target and must retain its original ownership relation.
            arena.OwnerMount.SetTeam(arena.OriginalMountTeam!, sync: true);
        }

        if (!PreparePlayerRespawnFrame(arena))
        {
            ResetArena(arena, "AI 出生点附近没有可用复活位置，本系列已取消。");
            return;
        }

        Notify(arena.Session.Owner!, $"当前比分 {arena.Series.PlayerWins}:{arena.Series.AiWins}（FT{arena.Series.FirstToLimit}）。{(arena.Bots.Count > 0 ? "存活 AI 保留" : "双方阵亡")}，复活后继续本系列。");
    }

    private void PrepareRoundSurvivors(Arena arena)
    {
        foreach (Agent bot in arena.Bots.ToArray())
        {
            if (bot.IsActive() && bot.Health > 0f)
            {
                // Keep a surviving AI and its position. Only clear the old
                // target until the losing human has respawned and is ready.
                bot.SetAutomaticTargetSelection(false);
                bot.SetTargetAgent(null!);
            }
            else
            {
                arena.Bots.Remove(bot);
            }
        }

        foreach (Agent mount in arena.Mounts.ToArray())
        {
            if (!mount.IsActive() || mount.Health <= 0f)
            {
                arena.Mounts.Remove(mount);
            }
            else if (arena.Bots.Count == 0)
            {
                mount.FadeOut(hideInstantly: true, hideMount: false);
                arena.Mounts.Remove(mount);
            }
        }

        foreach (Agent mount in arena.RespawnedPlayerMounts.ToArray())
        {
            if (!mount.IsActive() || mount.Health <= 0f || (arena.AwaitingRespawn || arena.OwnerAgent?.IsActive() != true))
            {
                if (mount.IsActive() && mount.Health > 0f)
                {
                    mount.FadeOut(hideInstantly: true, hideMount: false);
                }

                arena.RespawnedPlayerMounts.Remove(mount);
            }
        }

        arena.SpawnPositions.Clear();
    }

    private bool PreparePlayerRespawnFrame(Arena arena)
    {
        if (!PrepareSpawnPositions(arena, arena.OwnerAgent!))
        {
            return false;
        }

        MatrixFrame frame = arena.Spawn.GetGlobalFrame();
        frame.origin = arena.SpawnPositions[0];
        arena.OwnerRespawnFrame = frame;
        return true;
    }

    private bool PreparePendingRound(Arena arena, MissionPeer owner, Agent ownerAgent, double now)
    {
        // The winner remains the same Agent at the same position. Only a dead
        // AI is replaced; a retained AI is never recreated or teleported here.
        PrepareRoundSurvivors(arena);
        if (arena.Bots.Count == 0)
        {
            if (!PrepareSpawnPositions(arena, ownerAgent))
            {
                ResetArena(arena, "下一局缺少可用 AI 出生位置，本系列已取消。");
                return false;
            }

            SpawnRoundBots(arena, ownerAgent, activate: false);
        }

        PrepareRoundOwner(arena, ownerAgent);
        foreach (Agent bot in arena.Bots)
        {
            AdimiToolsMissionMultiplayerDuel.RestoreAgentForAiChallenge(bot);
        }

        arena.NextRoundAt = now + AdimiToolsAiArenaSession<MissionPeer>.CountdownSeconds;
        Notify(owner, $"当前比分 {arena.Series!.PlayerWins}:{arena.Series.AiWins}（FT{arena.Series.FirstToLimit}），下一局 3 秒后开始。");
        return true;
    }

    private void StartNextRound(Arena arena, MissionPeer owner, Agent ownerAgent)
    {
        if (!arena.Bots.Any(bot => bot.IsActive() && bot.Health > 0f))
        {
            PreparePendingRound(arena, owner, ownerAgent, Mission.CurrentTime);
            return;
        }

        arena.OwnerDiedThisRound = false;
        arena.RoundPending = false;
        arena.PlayerTeam!.SetIsEnemyOf(arena.BotTeam!, true);
        arena.BotTeam!.SetIsEnemyOf(arena.PlayerTeam, true);
        foreach (Agent bot in arena.Bots.Where(bot => bot.IsActive() && bot.Health > 0f))
        {
            bot.SetWatchState(Agent.WatchState.Alarmed);
            bot.SetAutomaticTargetSelection(false);
            bot.SetTargetAgent(ownerAgent);
        }

        Notify(owner, $"本局开始：你 {arena.Series!.PlayerWins}:{arena.Series.AiWins} AI（FT{arena.Series.FirstToLimit}）。");
    }

    private bool PrepareSpawnPositions(Arena arena, Agent owner)
    {
        arena.SpawnPositions.Clear();
        MatrixFrame frame = arena.Spawn.GetGlobalFrame();
        Vec2 forward = frame.rotation.f.AsVec2.Normalized();
        if (forward.LengthSquared < 0.1f)
        {
            forward = new Vec2(0f, 1f);
        }

        Vec2 right = new(forward.y, -forward.x);
        float spacing = arena.Equipment![EquipmentIndex.Horse].IsEmpty ? 1.6f : 3f;
        var offsets = from x in Enumerable.Range(-4, 9)
                       from y in Enumerable.Range(-4, 9)
                       orderby x * x + y * y, y, x
                       select (x, y);
        foreach (var (x, y) in offsets)
        {
            Vec2 offset = (right * x + forward * y) * spacing;
            Vec3 position = frame.origin + new Vec3(offset.x, offset.y, 0f);
            WorldPosition world = new(Mission.Scene, position);
            if (world.GetNavMesh() == UIntPtr.Zero)
            {
                continue;
            }

            Vec3 ground = world.GetNavMeshVec3();
            if (!ground.IsValid || Math.Abs(ground.z - frame.origin.z) > 2f
                || (arena.Area != null && !arena.Area.IsPointIn(ground))
                || Mission.Agents.Any(agent => agent.IsActive() && agent.Health > 0f && agent.Position.DistanceSquared(ground) < spacing * spacing))
            {
                continue;
            }

            arena.SpawnPositions.Add(ground);
            if (arena.SpawnPositions.Count == arena.Session.RequestedCount)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureTeams(Arena arena)
    {
        arena.PlayerTeam ??= Mission.Teams.Add(BattleSideEnum.Attacker, uint.MaxValue, uint.MaxValue, null, isPlayerGeneral: false, isPlayerSergeant: false, isSettingRelations: false);
        arena.BotTeam ??= Mission.Teams.Add(BattleSideEnum.Attacker, uint.MaxValue, uint.MaxValue, null, isPlayerGeneral: false, isPlayerSergeant: false, isSettingRelations: false);
        // Clear only this arena's relations; never disturb another AI group or PvP duel.
        foreach (Team team in Mission.Teams)
        {
            arena.PlayerTeam.SetIsEnemyOf(team, false);
            arena.BotTeam.SetIsEnemyOf(team, false);
            if (team != arena.PlayerTeam && team != arena.BotTeam)
            {
                team.SetIsEnemyOf(arena.PlayerTeam, false);
                team.SetIsEnemyOf(arena.BotTeam, false);
            }
        }
    }

    private bool HasOutsideRespawn(Arena arena)
    {
        return Mission.Scene.FindEntitiesWithTag("spawnpoint").Any(entity => IsOutsideAllAreas(entity.GlobalPosition, arena));
    }

    private bool IsOutsideAllAreas(Vec3 position, Arena current)
    {
        return (current.Area == null || !current.Area.IsPointIn(position))
            && !_arenas.Values.Any(arena => arena.Area != null && arena.Area.IsPointIn(position));
    }

    private bool EvictOtherPlayers(Arena arena, MissionPeer owner)
    {
        foreach (NetworkCommunicator networkPeer in GameNetwork.NetworkPeers)
        {
            MissionPeer? peer = networkPeer.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            if (peer == null || ReferenceEquals(peer, owner) || !networkPeer.IsSynchronized || !networkPeer.IsConnectionActive
                || agent == null || !agent.IsHuman || !agent.IsActive() || !arena.Area!.IsPointIn(agent.Position))
            {
                continue;
            }

            if (_duelMode?.IsPeerBusyForAiChallenge(peer) == true || IsPeerReserved(peer))
            {
                // Never teleport somebody out of another live PvP/AI match.
                return false;
            }

            var candidates = Mission.Scene.FindEntitiesWithTag("spawnpoint")
                .Where(entity => IsOutsideAllAreas(entity.GlobalPosition, arena)
                    && !(agent.MountAgent != null && entity.HasTag("exclude_mounted"))
                    && !(agent.MountAgent == null && entity.HasTag("exclude_footmen")))
                .OrderBy(entity => entity.GlobalPosition.DistanceSquared(agent.Position)).ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            Teleport(agent, candidates[0].GetGlobalFrame());
            Notify(peer, "该区域正在进行 AI 挑战，已将你移至区域外复活点。");
        }

        return true;
    }

    private bool HasBusyIntruder(Arena arena, MissionPeer owner)
    {
        return GameNetwork.NetworkPeers.Any(networkPeer =>
        {
            MissionPeer? peer = networkPeer.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            return peer != null && !ReferenceEquals(peer, owner) && agent != null && agent.IsActive()
                && networkPeer.IsConnectionActive && networkPeer.IsSynchronized && arena.Area!.IsPointIn(agent.Position)
                && (_duelMode?.IsPeerBusyForAiChallenge(peer) == true || IsPeerReserved(peer));
        });
    }

    private void ResetArena(Arena arena, string? message, bool immediately = false)
    {
        if (arena.Resetting)
        {
            return;
        }

        arena.Resetting = true;
        MissionPeer? owner = arena.Session.Owner ?? arena.CleanupOwner;
        arena.CleanupOwner = owner;
        arena.Session.Reset();
        var failures = new List<Exception>();
        try
        {
            if (arena.PlayerTeam != null && arena.BotTeam != null)
            {
                AttemptCleanup(() => arena.PlayerTeam.SetIsEnemyOf(arena.BotTeam, false), failures);
                AttemptCleanup(() => arena.BotTeam.SetIsEnemyOf(arena.PlayerTeam, false), failures);
            }

            foreach (Agent bot in arena.Bots.ToArray())
            {
                AttemptCleanup(() =>
                {
                    if (bot.IsActive() && bot.Health > 0f)
                    {
                        bot.SetTargetAgent(null!);
                        bot.FadeOut(hideInstantly: true, hideMount: false);
                    }

                    arena.Bots.Remove(bot);
                }, failures);
            }

            foreach (Agent mount in arena.Mounts.ToArray())
            {
                AttemptCleanup(() =>
                {
                    if (mount.IsActive() && mount.Health > 0f)
                    {
                        mount.FadeOut(hideInstantly: true, hideMount: false);
                    }

                    arena.Mounts.Remove(mount);
                }, failures);
            }

            if (owner != null && arena.PlayerTeam != null)
            {
                Team team = arena.OriginalTeam ?? Mission.AttackerTeam;
                if (owner.Team == arena.PlayerTeam)
                {
                    AttemptCleanup(() => owner.Team = team, failures);
                }

                if (ReferenceEquals(owner.ControlledAgent, arena.OwnerAgent) && arena.OwnerAgent?.IsActive() == true
                    && arena.OwnerAgent.Team == arena.PlayerTeam)
                {
                    AttemptCleanup(() => arena.OwnerAgent.SetTeam(team, sync: true), failures);
                }
            }

            if (arena.OwnerMount?.IsActive() == true && arena.OwnerMount.Team == arena.PlayerTeam)
            {
                AttemptCleanup(() => arena.OwnerMount.SetTeam(arena.OriginalMountTeam!, sync: true), failures);
            }

            foreach (Agent mount in arena.RespawnedPlayerMounts.ToArray())
            {
                AttemptCleanup(() =>
                {
                    if (mount.IsActive() && mount.Health > 0f)
                    {
                        if (arena.OwnerAgent?.IsActive() == true && arena.OwnerAgent.Health > 0f
                            && ReferenceEquals(arena.OwnerAgent.MountAgent, mount))
                        {
                            mount.SetTeam(arena.OriginalTeam ?? Mission.AttackerTeam, sync: true);
                        }
                        else
                        {
                            mount.FadeOut(hideInstantly: true, hideMount: false);
                        }
                    }

                    arena.RespawnedPlayerMounts.Remove(mount);
                }, failures);
            }

            if (arena.BarrierEngaged || immediately)
            {
                AttemptCleanup(() =>
                {
                    MoveBarrier(arena, raised: false, immediately);
                    arena.BarrierEngaged = false;
                    arena.LoweringUntil = arena.Barrier == null || immediately ? 0d : Mission.CurrentTime + AdimiToolsAiArenaSession<MissionPeer>.BarrierTravelSeconds;
                }, failures);
            }

            if (owner != null && message != null)
            {
                try
                {
                    Notify(owner, message);
                }
                catch (Exception exception)
                {
                    // A failed informational message must not retain a clean
                    // arena or keep its human owner reserved indefinitely.
                    AdimiToolsConsoleLog.Log($"AI group {arena.Id} notification failed: {exception}");
                }
            }
        }
        finally
        {
            arena.CleanupPending = failures.Count != 0;
            if (arena.CleanupPending)
            {
                arena.Failed = true;
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} cleanup will retry: {new AggregateException(failures)}");
            }
            else
            {
                arena.CleanupOwner = null;
                arena.OwnerAgent = null;
                arena.OwnerMount = null;
                arena.OriginalMountTeam = null;
                arena.MountKey = null;
                arena.OriginalTeam = null;
                arena.Equipment = null;
                arena.Series = null;
                arena.RoundPending = false;
                arena.AwaitingRespawn = false;
                arena.OwnerDiedThisRound = false;
                arena.RespawnNoticePending = false;
                arena.NextRoundAt = 0d;
                arena.RespawnDeadline = 0d;
                arena.SpawnPositions.Clear();
            }

            arena.Resetting = false;
        }
    }

    private void FailArena(Arena arena, Exception exception)
    {
        AdimiToolsConsoleLog.Log($"AI group {arena.Id} disabled after error: {exception}");
        arena.Failed = true;
        try
        {
            ResetArena(arena, "本组 AI 挑战出现错误，已停止本组训练。");
        }
        catch (Exception cleanupException)
        {
            AdimiToolsConsoleLog.Log($"AI group {arena.Id} cleanup failed: {cleanupException}");
        }
    }

    private void CleanupAll()
    {
        if (_ending)
        {
            return;
        }

        _ending = true;
        foreach (Arena arena in _arenas.Values)
        {
            try
            {
                ResetArena(arena, null, immediately: true);
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} mission cleanup failed: {exception}");
            }
        }
    }
}
