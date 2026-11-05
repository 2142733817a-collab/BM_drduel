using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaBehavior : MissionLogic
{
    private sealed class BarrierMotion
    {
        public SynchedMissionObject Synched { get; init; } = null!;
        public GameEntity Entity { get; init; } = null!;
        public MatrixFrame LoweredFrame { get; init; }
        // Keep the original local pose for an exact return under any parent.
        public MatrixFrame OriginalLocalFrame { get; init; }
        public bool IsLadder { get; init; }
        public MatrixFrame ActiveFrame { get; init; }
        public float CurrentPoseProgress { get; set; }
        public float StartPoseProgress { get; set; }
        public float TargetPoseProgress { get; set; }
        public float ActiveOffsetZ { get; init; } = AdimiToolsAiArenaSession<MissionPeer>.BarrierRiseMeters;
        public bool PhysicsPrepared { get; set; }
        public double StepAt { get; set; }
        public double StartedAt { get; set; }
        public MatrixFrame StartFrame { get; set; }
        public MatrixFrame? TargetFrame { get; set; }
        public int FinishAttempts { get; set; }
    }

    private sealed class Arena
    {
        public int Id { get; init; }
        public List<GameEntity> Triggers { get; init; } = new();
        public GameEntity Spawn { get; init; } = null!;
        public List<BarrierMotion> Barriers { get; init; } = new();
        public VolumeBox? Area { get; init; }
        public AdimiToolsSharedArena? Shared { get; init; }
        public AdimiToolsArenaGlowGroup Glows { get; init; } = null!;
        public bool GlowsHidden { get; set; }
        public AdimiToolsAiArenaSession<MissionPeer> Session { get; } = new();
        public AdimiToolsAiArenaNavigationGuard NavigationGuard { get; } = new();
        public Agent? OwnerAgent { get; set; }
        public Agent? OwnerMount { get; set; }
        public Team? OriginalMountTeam { get; set; }
        public string? MountKey { get; set; }
        public Team? OriginalTeam { get; set; }
        public Team? PlayerTeam { get; set; }
        public Team? BotTeam { get; set; }
        public Equipment? Equipment { get; set; }
        public Equipment? RegularRespawnEquipment { get; set; }
        public Banner? Banner { get; set; }
        public int Difficulty { get; set; } = AdimiToolsAiArenaDifficulty.Simple;
        public bool Enhanced => Difficulty != AdimiToolsAiArenaDifficulty.Simple;
        public AdimiToolsAiArenaHardProfile? HardProfile { get; set; }
        public AdimiToolsAiArenaSeries? Series { get; set; }
        public MatrixFrame OwnerRespawnFrame { get; set; }
        public bool RoundPending { get; set; }
        public bool AwaitingRespawn { get; set; }
        public bool OwnerDiedThisRound { get; set; }
        public bool RespawnNoticePending { get; set; }
        public MatrixFrame? PlayerDeathFrame { get; set; }
        public MatrixFrame LastPlayerFrame { get; set; }
        public AgentBuildData? OriginalCharacter { get; set; }
        public bool CompletionRecoveryStarted { get; set; }
        public bool CompletionHealthRestored { get; set; }
        public double? CompletionStartedAt { get; set; }
        public bool ScoreSettled { get; set; }
        public int StartedAiCount { get; set; }
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
        public bool AreaCleanupPending { get; set; }
        public double AreaCleanupUntil { get; set; }
        public double NextAreaCleanupAt { get; set; }
    }

    private sealed class CompletionRespawn
    {
        public MatrixFrame Frame { get; init; }
        public bool UseRegularSpawn { get; init; }
        public bool RestoreAppearance { get; init; }
        public AgentBuildData Character { get; init; } = null!;
        public Equipment Equipment { get; init; } = null!;
        public Banner? Banner { get; init; }
        public string? MountKey { get; init; }
        public Team Team { get; init; } = null!;
        public float HealthLimit { get; init; }
        public float BaseHealthLimit { get; init; }
        public bool Configured { get; set; }
        public Agent? SpawnedAgent { get; set; }
        public bool SpawnFinished { get; set; }
        public bool EquipmentRestored { get; set; }
        public bool HealthRestored { get; set; }
        public double NextRestoreAt { get; set; }
    }

    private const float CheckInterval = 0.25f;
    private const double BarrierStepInterval = 0.05d;
    private const double CompletedAiCleanupDelay = 3d;
    private const double CompletedBarrierOpenDelay = 1d;
    private readonly Dictionary<int, Arena> _arenas = new();
    private readonly Dictionary<MissionPeer, CompletionRespawn> _completionRespawns = new();
    private readonly Dictionary<MissionPeer, double> _nextInterferenceWarningAt = new();
    private AdimiToolsMissionMultiplayerDuel? _duelMode;
    private double _nextCheckAt;
    private bool _ending;
    private bool _sceneClearing;

    public override void AfterStart()
    {
        base.AfterStart();
        _duelMode = Mission.GetMissionBehavior<AdimiToolsMissionMultiplayerDuel>();
        InitializeGroups();
    }

    public void SynchronizeLadders(NetworkCommunicator networkPeer)
    {
        if (_ending || _sceneClearing || networkPeer.IsServerPeer || !networkPeer.IsConnectionActive || !networkPeer.IsSynchronized)
        {
            return;
        }

        foreach (BarrierMotion ladder in _arenas.Values.SelectMany(arena => arena.Barriers)
            .Where(motion => motion.IsLadder && motion.PhysicsPrepared))
        {
            // Native WriteToNetwork does not include changed body flags. A
            // directed stock message makes late joins independent of replay.
            GameNetwork.BeginModuleEventAsServer(networkPeer);
            try
            {
                GameNetwork.WriteMessage(new AddMissionObjectBodyFlags(ladder.Synched.Id, BodyFlags.Moveable, applyToChildren: true));
            }
            finally
            {
                GameNetwork.EndModuleEventAsServer();
            }
        }
    }

    public bool IsPeerReserved(MissionPeer peer)
    {
        return (_completionRespawns.TryGetValue(peer, out CompletionRespawn? completed) && completed.SpawnFinished)
            || _arenas.Values.Any(arena => (arena.Session.IsReserved && ReferenceEquals(arena.Session.Owner, peer))
            || (arena.CleanupPending && ReferenceEquals(arena.CleanupOwner, peer)));
    }

    public bool IsPositionInArena(Vec3 position) => !_sceneClearing && _arenas.Values.Any(arena => arena.Area?.IsPointIn(position) == true);

    public MatrixFrame? GetRoundRespawnFrame(MissionPeer peer)
    {
        CompletionRespawn? completed = GetCompletionRespawn(peer);
        if (completed != null)
        {
            return completed.UseRegularSpawn ? null : completed.Frame;
        }

        Arena? arena = FindRespawningArena(peer);
        return arena?.OwnerRespawnFrame;
    }

    public bool HasPendingAiRespawn(MissionPeer peer)
    {
        CompletionRespawn? completed = GetCompletionRespawn(peer);
        return (completed != null && !completed.SpawnFinished) || FindRespawningArena(peer) != null;
    }

    public bool UsesRegularCompletionSpawn(MissionPeer peer)
    {
        return GetCompletionRespawn(peer)?.UseRegularSpawn == true;
    }

    public bool QueueCompletedInfantryRespawn(MissionPeer peer, AgentBuildData character, Equipment equipment,
        Banner? banner, Team originalTeam, float healthLimit, float baseHealthLimit)
    {
        if (!GameNetwork.IsServer || _ending || _sceneClearing || peer == null || character == null
            || equipment == null || originalTeam == null || ReferenceEquals(originalTeam, Mission.SpectatorTeam)
            || !float.IsFinite(healthLimit) || healthLimit <= 0f || !float.IsFinite(baseHealthLimit) || baseHealthLimit <= 0f
            || !ReferenceEquals(peer.Team, originalTeam)
            || character.AgentCharacter is not BasicCharacterObject savedCharacter
            || savedCharacter.DefaultFormationClass is not (FormationClass.Infantry or FormationClass.HeavyInfantry))
        {
            return false;
        }

        NetworkCommunicator? networkPeer = peer.GetNetworkPeer();
        if (networkPeer == null || networkPeer.IsServerPeer || !networkPeer.IsConnectionActive || !networkPeer.IsSynchronized
            || IsLivingAgent(peer.ControlledAgent))
        {
            return false;
        }

        if (GetCompletionRespawn(peer) != null)
        {
            // Cleanup can retry. A valid ticket is already queued successfully;
            // never replace it with a later, damaged or different snapshot.
            return true;
        }

        var savedBuild = new AgentBuildData(savedCharacter);
        ApplyCompletionAppearance(savedBuild, character);
        _completionRespawns[peer] = new CompletionRespawn
        {
            UseRegularSpawn = true,
            RestoreAppearance = true,
            Character = savedBuild,
            Equipment = equipment.Clone(),
            Banner = banner == null ? null : new Banner(banner),
            MountKey = null,
            Team = originalTeam,
            HealthLimit = healthLimit,
            BaseHealthLimit = baseHealthLimit,
        };
        return true;
    }

    public bool ShouldDelayRespawn(MissionPeer peer)
    {
        Arena? arena = FindRespawningArena(peer);
        return arena != null && (Mission.CurrentTime < arena.NextRoundAt || !PreparePlayerRespawnFrame(arena));
    }

    public void ConfigureRoundRespawn(MissionPeer peer, AgentBuildData buildData)
    {
        CompletionRespawn? completed = GetCompletionRespawn(peer);
        if (completed != null)
        {
            ConfigureSavedRespawn(buildData, completed.Character, completed.Equipment, completed.MountKey, completed.Banner);
            if (completed.RestoreAppearance)
            {
                ApplyCompletionAppearance(buildData, completed.Character);
            }

            completed.Configured = true;
            return;
        }

        Arena? arena = FindRespawningArena(peer);
        if (arena?.Equipment == null || arena.OwnerAgent == null)
        {
            return;
        }

        ConfigureSavedRespawn(buildData, arena.OriginalCharacter ?? new AgentBuildData(arena.OwnerAgent.Character), arena.Equipment, arena.MountKey, arena.Banner);
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        MissionPeer? peer = agent.MissionPeer;
        CompletionRespawn? completed = peer == null ? null : GetCompletionRespawn(peer, allowNewAgent: true);
        if (completed != null && completed.Configured && agent.IsHuman && agent.Team == completed.Team
            && ReferenceEquals(agent.Character, completed.Character.AgentCharacter))
        {
            completed.SpawnedAgent = agent;
            return;
        }

        Arena? arena = peer == null ? null : FindRespawningArena(peer);
        if (arena == null || !agent.IsHuman || agent.Team != arena.PlayerTeam)
        {
            return;
        }

        arena.OwnerAgent = agent;
        arena.PlayerDeathFrame = null;
        arena.LastPlayerFrame = GetPlayerGroundFrame(agent);
        arena.AwaitingRespawn = false;
        arena.RespawnNoticePending = true;
        arena.NextRoundAt = Mission.CurrentTime + AdimiToolsAiArenaSession<MissionPeer>.CountdownSeconds;
        if (agent.MountAgent != null)
        {
            arena.RespawnedPlayerMounts.Add(agent.MountAgent);
        }

        _nextCheckAt = 0d;
    }

    // Called by the native spawning path after its perks have been applied.
    public void CompleteRoundRespawn(MissionPeer peer, Agent agent)
    {
        CompletionRespawn? completed = GetCompletionRespawn(peer);
        if (completed != null && ReferenceEquals(completed.SpawnedAgent, agent))
        {
            completed.SpawnFinished = true;
            TryRestoreCompletedRespawn(peer, completed);
            return;
        }

        Arena? arena = _arenas.Values.FirstOrDefault(candidate => candidate.Session.IsReserved
            && candidate.RoundPending && ReferenceEquals(candidate.Session.Owner, peer) && ReferenceEquals(candidate.OwnerAgent, agent));
        if (arena != null)
        {
            agent.HealthLimit = arena.HealthLimit;
            agent.BaseHealthLimit = arena.BaseHealthLimit;
            agent.Health = agent.HealthLimit;
        }
    }

    public void OnTriggerHit(int groupId, Agent agent, bool enhanced)
        => OnTriggerHit(groupId, agent, enhanced ? AdimiToolsAiArenaDifficulty.Normal : AdimiToolsAiArenaDifficulty.Simple);

    public void OnTriggerHit(int groupId, Agent agent, int difficulty = AdimiToolsAiArenaDifficulty.Simple)
    {
        MissionPeer? peer = agent.MissionPeer;
        NetworkCommunicator? networkPeer = peer?.GetNetworkPeer();
        if (difficulty is < AdimiToolsAiArenaDifficulty.Simple or > AdimiToolsAiArenaDifficulty.Hard
            || _ending || peer == null || networkPeer == null || !networkPeer.IsSynchronized || !networkPeer.IsConnectionActive
            || !ReferenceEquals(peer.ControlledAgent, agent) || !IsLivingAgent(agent)
            || peer.Team == null || peer.Team == Mission.SpectatorTeam
            || !_arenas.TryGetValue(groupId, out Arena? arena) || arena.Failed)
        {
            return;
        }

        double now = Mission.CurrentTime;
        if (arena.Shared?.Owns(arena) == true && !arena.Session.IsReserved)
        {
            Notify(peer, "本场地仍在完成清理和障碍复位，请稍后再试。");
            return;
        }

        if (arena.CleanupPending)
        {
            Notify(peer, "本组挑战正在结束，请稍后再试。");
            return;
        }

        if (arena.AreaCleanupPending && !arena.Session.IsReserved)
        {
            Notify(peer, "训练区域正在清理，请稍后再试。");
            return;
        }

        if (now < arena.LoweringUntil || (!arena.Session.IsReserved
            && arena.Barriers.Any(barrier => barrier.TargetFrame.HasValue || !barrier.Synched.SynchronizeCompleted)))
        {
            Notify(peer, "障碍物正在复位，请稍后再试。");
            return;
        }

        if (_duelMode?.IsPeerBusyForAiChallenge(peer) == true || (!arena.Session.IsReserved && IsPeerReserved(peer))
            || Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(peer) == true
            || Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPositionInActiveArena(agent.Position) == true)
        {
            Notify(peer, "请先结束当前决斗或 AI 挑战。");
            return;
        }

        if (arena.Area != null && !arena.Area.IsPointIn(agent.Position))
        {
            Notify(peer, "请进入本组训练区域后再敲击。");
            return;
        }

        if (arena.Session.State == AdimiToolsAiArenaState.CountingDown && ReferenceEquals(arena.Session.Owner, peer)
            && arena.Difficulty != difficulty)
        {
            Notify(peer, $"本次已选择{AdimiToolsAiArenaDifficulty.GetName(arena.Difficulty)} AI，请继续敲击同一种触发物，或等本次挑战结束后再切换。");
            return;
        }

        AdimiToolsAiArenaHardProfile? hardProfile = null;
        if (!arena.Session.IsReserved && difficulty == AdimiToolsAiArenaDifficulty.Hard
            && !AdimiToolsAiArenaHardProfile.TryCreate(peer, out hardProfile, out string reason))
        {
            Notify(peer, reason);
            return;
        }

        bool acquiredShared = arena.Shared != null && !arena.Shared.Owns(arena);
        if (arena.Shared?.TryAcquire(arena) == false)
        {
            Notify(peer, "本场地正在进行玩家队战或清理，请稍后再试。");
            return;
        }

        AdimiToolsAiArenaHitResult result = arena.Session.TryHit(peer, now);
        if (acquiredShared && result != AdimiToolsAiArenaHitResult.Reserved && result != AdimiToolsAiArenaHitResult.Counted)
        {
            arena.Shared!.Release(arena);
        }

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
                arena.GlowsHidden = false;
                arena.Glows.Update(false, now);
                _duelMode?.CancelIncomingDuelRequestsForAiChallenge(peer);
                arena.NavigationGuard.Reset();
                arena.CompletionRecoveryStarted = false;
                arena.CompletionHealthRestored = false;
                arena.CompletionStartedAt = null;
                arena.ScoreSettled = false;
                arena.StartedAiCount = 0;
                arena.PlayerDeathFrame = null;
                arena.LastPlayerFrame = GetPlayerGroundFrame(agent);
                arena.OriginalCharacter = new AgentBuildData(agent.Character);
                arena.Difficulty = difficulty;
                arena.HardProfile = hardProfile;
                arena.OwnerAgent = agent;
                arena.OriginalTeam = peer.Team;
                arena.Equipment = SnapshotEquipment(agent);
                // Infantry keeps the loadout it brought into this challenge,
                // including a shield that may later break. Other roles retain
                // their class/perk new-life kit. Decide while the actor is alive.
                arena.RegularRespawnEquipment = agent.Character.DefaultFormationClass is FormationClass.Infantry or FormationClass.HeavyInfantry
                    ? arena.Equipment.Clone()
                    : SnapshotInitialEquipment(peer, agent);
                arena.Banner = AdimiToolsDuelAppearance.CreateBanner(peer.Culture);
                arena.MountKey = IsLivingAgent(agent.MountAgent) ? agent.MountAgent!.HorseCreationKey : null;
                arena.OwnerRespawnFrame = arena.Spawn.GetGlobalFrame();
                arena.HealthLimit = agent.HealthLimit;
                arena.BaseHealthLimit = agent.BaseHealthLimit;
                arena.ArmorHead = agent.AgentDrivenProperties.ArmorHead;
                arena.ArmorTorso = agent.AgentDrivenProperties.ArmorTorso;
                arena.ArmorArms = agent.AgentDrivenProperties.ArmorArms;
                arena.ArmorLegs = agent.AgentDrivenProperties.ArmorLegs;
                MoveBarrier(arena, raised: true);
            }

            string message = arena.Difficulty switch
            {
                AdimiToolsAiArenaDifficulty.Normal => $"3 秒后生成 {arena.Session.RequestedCount} 个一般 AI：与你当前兵种、装备相同，熟练度 +50，生命上限翻倍。",
                AdimiToolsAiArenaDifficulty.Hard => $"3 秒后生成 {arena.Session.RequestedCount} 个困难 AI：六国随机的固定步兵，熟练度 330，生命上限翻倍并加强护甲。",
                _ => $"3 秒后生成 {arena.Session.RequestedCount} 个简单 AI：与你当前兵种、装备相同。",
            };
            Notify(peer, message);
        }
        catch (Exception exception)
        {
            FailArena(arena, exception);
        }
    }

    public override void OnMissionTick(float dt)
    {
        if (_ending)
        {
            return;
        }

        double now = Mission.CurrentTime;
        foreach (MissionPeer peer in _completionRespawns.Keys.ToArray())
        {
            CompletionRespawn? completed = GetCompletionRespawn(peer);
            if (completed?.SpawnFinished == true && now >= completed.NextRestoreAt)
            {
                TryRestoreCompletedRespawn(peer, completed);
            }
        }

        foreach (MissionPeer peer in _nextInterferenceWarningAt.Keys.ToArray())
        {
            if (!peer.GetNetworkPeer().IsConnectionActive)
            {
                _nextInterferenceWarningAt.Remove(peer);
            }
        }

        foreach (Arena arena in _arenas.Values)
        {
            if (arena.Shared != null && !arena.Shared.Owns(arena))
            {
                continue;
            }

            try
            {
                UpdateBarrierMovement(arena, now);
                if (arena.GlowsHidden && !arena.Session.IsReserved && !arena.CleanupPending
                    && !arena.AreaCleanupPending && now >= arena.LoweringUntil
                    && arena.Shared == null)
                {
                    arena.GlowsHidden = false;
                }

                arena.Glows.Update(arena.GlowsHidden, now);
                ReleaseSharedIfClean(arena, now);
            }
            catch (Exception exception)
            {
                FailArena(arena, exception);
            }
        }

        if (now < _nextCheckAt)
        {
            return;
        }

        _nextCheckAt = now + CheckInterval;
        foreach (Arena arena in _arenas.Values)
        {
            if (arena.Shared != null && !arena.Shared.Owns(arena))
            {
                continue;
            }

            bool activeRound = arena.Session.State == AdimiToolsAiArenaState.Running && !arena.RoundPending;
            bool completionWaiting = arena.CompletionStartedAt.HasValue
                && now < arena.CompletionStartedAt.Value + CompletedAiCleanupDelay;
            if (!activeRound && !completionWaiting && arena.Area != null && now >= arena.NextAreaCleanupAt
                && (arena.AreaCleanupPending || now <= arena.AreaCleanupUntil))
            {
                TryAreaCleanup(arena);
            }

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

                // A living owner leaving the arena forfeits the whole series,
                // before bot deaths can award a round. Ignore the old corpse
                // while waiting for the losing player to respawn.
                if (arena.Area != null && IsLivingAgent(ownerAgent)
                    && ReferenceEquals(owner.ControlledAgent, ownerAgent) && !arena.Area.IsPointIn(ownerAgent.Position))
                {
                    if (arena.Session.State == AdimiToolsAiArenaState.Running && arena.Series != null)
                    {
                        arena.RoundPending = true;
                        arena.Series.ForfeitPlayer();
                        ResetArena(arena, "你已离开训练区域，整场挑战判负，AI 获胜。区域恢复开放。");
                    }
                    else
                    {
                        ResetArena(arena, "挑战开始前离开训练区域，本次 AI 挑战已取消。");
                    }

                    continue;
                }

                if (ShouldForfeitForNavigation(arena, owner, ownerAgent, now))
                {
                    arena.RoundPending = true;
                    arena.Series?.ForfeitPlayer();
                    ResetArena(arena, "你已连续 5 秒脱离 AI 索敌范围，本次挑战自动判负。");
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

                        if (!IsLivingAgent(ownerAgent) || !ReferenceEquals(owner.ControlledAgent, ownerAgent))
                        {
                            ResetArena(arena, "玩家状态发生变化，本次 AI 系列已取消。");
                            continue;
                        }

                        if (arena.RespawnNoticePending || !arena.Bots.Any(IsLivingAgent))
                        {
                            arena.RespawnNoticePending = false;
                            if (!PreparePendingRound(arena, owner, ownerAgent, now))
                            {
                                continue;
                            }
                        }
                    }
                    else if (arena.OwnerDiedThisRound || !IsLivingAgent(ownerAgent))
                    {
                        ResolveRound(arena, playerWon: false, now);
                        continue;
                    }
                    else if (!arena.Bots.Any(IsLivingAgent))
                    {
                        ResolveRound(arena, playerWon: true, now);
                        continue;
                    }
                }

                if (!IsLivingAgent(ownerAgent) || !ReferenceEquals(owner.ControlledAgent, ownerAgent))
                {
                    ResetArena(arena, "AI 挑战已结束，区域恢复开放。");
                    continue;
                }

                arena.LastPlayerFrame = GetPlayerGroundFrame(ownerAgent);

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

                    if (arena.Area != null && !EvictOtherPlayers(arena, owner))
                    {
                        ResetArena(arena, "战斗附近存在无法遣返的玩家，本次挑战已取消。");
                        continue;
                    }

                    foreach (Agent bot in arena.Bots.Where(IsLivingAgent))
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
                    if (!arena.PlayerDeathFrame.HasValue)
                    {
                        try
                        {
                            arena.PlayerDeathFrame = GetPlayerGroundFrame(affectedAgent);
                        }
                        catch (Exception exception)
                        {
                            AdimiToolsConsoleLog.Log($"AI group {arena.Id} death frame fallback: {exception.Message}");
                        }
                    }

                    arena.OwnerDiedThisRound = true;
                }

                _nextCheckAt = 0d;
            }
        }
    }

    public override void OnClearScene()
    {
        // ResetMission already called Agent.OnDelete for every entry, but has
        // not removed them from AllAgents yet. Do not touch native actors here.
        _sceneClearing = true;
        _completionRespawns.Clear();
        _nextInterferenceWarningAt.Clear();
        foreach (Arena arena in _arenas.Values)
        {
            arena.Glows.RestoreAndForget(Mission.CurrentTime);
        }

        CleanupAll();
        foreach (Arena arena in _arenas.Values)
        {
            arena.Shared?.Release(arena);
        }

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
        foreach (Arena arena in _arenas.Values)
        {
            arena.Shared?.Release(arena);
        }

        base.OnEndMission();
    }

    private static void ApplyCompletionAppearance(AgentBuildData target, AgentBuildData saved)
    {
        if (saved.BodyPropertiesOverriden)
        {
            target.BodyProperties(saved.AgentBodyProperties);
        }

        if (saved.GenderOverriden)
        {
            target.IsFemale(saved.AgentIsFemale);
        }

        if (saved.AgeOverriden)
        {
            target.Age(saved.AgentAge);
        }
        else if (saved.BodyPropertiesOverriden)
        {
            target.Age((int)saved.AgentBodyProperties.Age);
        }

        if (saved.AgentMonster != null)
        {
            target.Monster(saved.AgentMonster);
        }
    }

    private static void ConfigureSavedRespawn(AgentBuildData buildData, AgentBuildData character, Equipment equipment, string? mountKey, Banner? banner)
    {
        buildData.Character(character.AgentCharacter)
            .TroopOrigin(new BasicBattleAgentOrigin(character.AgentCharacter))
            .Equipment(equipment.Clone())
            .Banner(banner == null ? null : new Banner(banner))
            .FixedEquipment(true)
            .NoHorses(equipment[EquipmentIndex.Horse].IsEmpty);
        if (!string.IsNullOrEmpty(mountKey))
        {
            buildData.MountKey(mountKey);
        }
    }

    private static void Notify(MissionPeer peer, string message)
    {
        NetworkCommunicator? networkPeer = peer.GetNetworkPeer();
        if (networkPeer != null && networkPeer.IsConnectionActive)
        {
            // Non-broadcast admin messages color the chat log without entering
            // the client's timed quick-information queue. Audio is targeted separately.
            AdimiToolsNotifier.ServerSendAiStatusToPlayer(networkPeer, message);
        }
    }

    private static void MoveBarrier(Arena arena, bool raised, bool immediately = false)
    {
        if (arena.Shared != null)
        {
            if (raised)
            {
                arena.BarrierEngaged = true;
            }

            arena.Shared.Move(arena, raised, immediately);
        }
        else
        {
            MoveBarrierCore(arena, raised, immediately);
        }
    }

    private static void MoveBarrierCore(Arena arena, bool raised, bool immediately = false)
    {
        if (arena.Barriers.Count == 0)
        {
            return;
        }

        UpdateBarrierMovementCore(arena, Mission.Current.CurrentTime);
        if (raised)
        {
            arena.BarrierEngaged = true;
        }

        var failures = new List<Exception>();
        foreach (BarrierMotion barrier in arena.Barriers)
        {
            // One invalid object must not prevent the remaining independent
            // roots from moving or returning underground during cleanup.
            AttemptCleanup(() => MoveBarrierMotion(arena.Id, barrier, raised, immediately), failures);
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }

    private static void MoveBarrierMotion(int groupId, BarrierMotion barrier, bool raised, bool immediately)
    {
        if (raised && barrier.IsLadder && !barrier.PhysicsPrepared)
        {
            // The native Synched helper skips all children when the root
            // already has this flag. Apply recursively and publish explicitly.
            barrier.Entity.AddBodyFlags(BodyFlags.Moveable, applyToChildren: true);
            GameNetwork.BeginBroadcastModuleEvent();
            try
            {
                GameNetwork.WriteMessage(new AddMissionObjectBodyFlags(barrier.Synched.Id, BodyFlags.Moveable, applyToChildren: true));
            }
            finally
            {
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
            }

            barrier.PhysicsPrepared = true;
        }

        MatrixFrame startFrame = barrier.Entity.GetGlobalFrame();
        MatrixFrame frame = barrier.LoweredFrame;
        float targetPoseProgress = raised ? 1f : 0f;
        if (barrier.IsLadder)
        {
            frame = raised ? barrier.ActiveFrame : barrier.LoweredFrame;
        }
        else
        {
            frame.origin.x = startFrame.origin.x;
            frame.origin.y = startFrame.origin.y;
            if (raised)
            {
                frame.origin.z += barrier.ActiveOffsetZ;
            }
        }

        if (immediately)
        {
            ApplyBarrierFrame(barrier, ref frame, forceBroadcast: true,
                restoreOriginalLocal: barrier.IsLadder && !raised);
            if (barrier.IsLadder)
            {
                barrier.CurrentPoseProgress = targetPoseProgress;
            }
        }

        barrier.TargetFrame = immediately ? null : frame;
        barrier.StartFrame = startFrame;
        barrier.StartPoseProgress = barrier.CurrentPoseProgress;
        barrier.TargetPoseProgress = targetPoseProgress;
        barrier.StartedAt = Mission.Current.CurrentTime;
        barrier.FinishAttempts = 0;
        barrier.StepAt = barrier.StartedAt;
        AdimiToolsConsoleLog.Log($"AI group {groupId} {(barrier.IsLadder ? "ladder rotation" : "barrier")} {(raised ? "activate" : "restore")}, object {barrier.Synched.Id}, activeOffsetZ={barrier.ActiveOffsetZ:F1}: start {FormatBarrierFrame(startFrame)}; idle {FormatBarrierFrame(barrier.LoweredFrame)}; target {FormatBarrierFrame(frame)}; duration={(immediately ? 0f : AdimiToolsAiArenaSession<MissionPeer>.BarrierTravelSeconds):F1}s.");
    }

    private static void UpdateBarrierMovement(Arena arena, double now)
    {
        if (arena.Shared != null)
        {
            arena.Shared.Tick(arena, now);
        }
        else
        {
            UpdateBarrierMovementCore(arena, now);
        }
    }

    private static void UpdateBarrierMovementCore(Arena arena, double now)
    {
        foreach (BarrierMotion barrier in arena.Barriers)
        {
            UpdateBarrierMotion(arena.Id, barrier, now);
        }
    }

    private static void UpdateSharedBoundaryMovement(Arena arena, double now)
    {
        UpdateBarrierMovementCore(arena, now);
        if (arena.Barriers.Any(barrier => barrier.TargetFrame.HasValue && barrier.FinishAttempts >= 3))
        {
            throw new InvalidOperationException("A shared AI barrier could not reach its requested frame after three attempts.");
        }
    }

    private static bool AreOwnBarriersAtTarget(Arena arena, bool raised)
        => arena.Barriers.All(barrier => !barrier.TargetFrame.HasValue && barrier.Synched.SynchronizeCompleted
            && (barrier.IsLadder
                ? Math.Abs(barrier.CurrentPoseProgress - (raised ? 1f : 0f)) <= 0.0001f
                : Math.Abs(barrier.Entity.GetGlobalFrame().origin.z
                    - (barrier.LoweredFrame.origin.z + (raised ? barrier.ActiveOffsetZ : 0f))) <= 0.001f));

    private static bool CanReleaseShared(Arena arena, double now)
        => arena.Shared != null && arena.Shared.Owns(arena) && !arena.Resetting
            && !arena.Session.IsReserved && !arena.CleanupPending && !arena.AreaCleanupPending
            && now > arena.AreaCleanupUntil && now >= arena.LoweringUntil
            && arena.Shared.IsAtTarget(arena, raised: false);

    private static void UpdateBarrierMotion(int groupId, BarrierMotion barrier, double now)
    {
        if (!barrier.TargetFrame.HasValue || barrier.FinishAttempts >= 3
            || (now + 0.000001d < barrier.StepAt && (barrier.FinishAttempts > 0
                || now < barrier.StartedAt + AdimiToolsAiArenaSession<MissionPeer>.BarrierTravelSeconds)))
        {
            return;
        }

        MatrixFrame target = barrier.TargetFrame!.Value;
        float progress = (float)System.Math.Clamp((now - barrier.StartedAt) / AdimiToolsAiArenaSession<MissionPeer>.BarrierTravelSeconds, 0d, 1d);
        MatrixFrame step = target;
        float poseProgress = barrier.StartPoseProgress
            + ((barrier.TargetPoseProgress - barrier.StartPoseProgress) * progress);
        if (barrier.IsLadder)
        {
            // Interpolate the two saved rotations. Rebuilding from the idle
            // pose avoids accumulated rotations, scale changes and pivot drift.
            step = progress >= 1f ? target
                : AdimiToolsAiArenaLadderRotation.Interpolate(barrier.LoweredFrame,
                    barrier.ActiveFrame, poseProgress);
        }
        else if (progress < 1f)
        {
            step.origin.z = barrier.StartFrame.origin.z + ((target.origin.z - barrier.StartFrame.origin.z) * progress);
        }

        try
        {
            ApplyBarrierFrame(barrier, ref step, forceBroadcast: progress >= 1f,
                restoreOriginalLocal: barrier.IsLadder && progress >= 1f && barrier.TargetPoseProgress == 0f);
            if (barrier.IsLadder)
            {
                barrier.CurrentPoseProgress = poseProgress;
            }

            barrier.StepAt = now + BarrierStepInterval;
            if (progress >= 1f)
            {
                MatrixFrame actual = barrier.Entity.GetGlobalFrame();
                AdimiToolsConsoleLog.Log($"AI group {groupId} barrier finished, object {barrier.Synched.Id}: target {FormatBarrierFrame(target)}; actual {FormatBarrierFrame(actual)}.");
                barrier.TargetFrame = null;
            }
        }
        catch (Exception exception)
        {
            barrier.FinishAttempts++;
            barrier.StepAt = now + 2d;
            AdimiToolsConsoleLog.Log($"AI group {groupId} barrier object {barrier.Synched.Id} step failed (attempt {barrier.FinishAttempts}/3): {exception.Message}");
        }
    }

    private static void ApplyBarrierFrame(BarrierMotion barrier, ref MatrixFrame target, bool forceBroadcast,
        bool restoreOriginalLocal = false)
    {
        // v1.4.8's global-frame messages write F/S/U but read S/F/U, swapping
        // the first two axes on clients. The original local-frame codec keeps
        // S/F/U in the same order. Convert only the coordinate space here; the
        // desired world pose may translate or rotate. The completed ladder
        // return replays the original local frame without an inverse round trip.
        var parent = barrier.Entity.Parent;
        MatrixFrame localTarget = restoreOriginalLocal ? barrier.OriginalLocalFrame : parent != null
            ? parent.GetGlobalFrame().TransformToLocalNonOrthogonal(in target)
            : target;
        bool alreadyAtTarget = barrier.Entity.GetFrame().Equals(localTarget);
        barrier.Synched.SetFrameSynched(ref localTarget);
        if (alreadyAtTarget && forceBroadcast)
        {
            // The native setter returns early for an equal server frame. The
            // original frame message still reasserts the pose on each client.
            GameNetwork.BeginBroadcastModuleEvent();
            try
            {
                GameNetwork.WriteMessage(new SetMissionObjectFrame(barrier.Synched.Id, ref localTarget));
            }
            finally
            {
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
            }
        }
    }

    private static string FormatBarrierFrame(MatrixFrame frame)
    {
        return $"XYZ=({frame.origin.x:F3},{frame.origin.y:F3},{frame.origin.z:F3}) S=({frame.rotation.s.x:F5},{frame.rotation.s.y:F5},{frame.rotation.s.z:F5}) F=({frame.rotation.f.x:F5},{frame.rotation.f.y:F5},{frame.rotation.f.z:F5}) U=({frame.rotation.u.x:F5},{frame.rotation.u.y:F5},{frame.rotation.u.z:F5})";
    }

    private static List<GameEntity> SelectBarrierRoots(IReadOnlyCollection<GameEntity> barriers)
    {
        var tagged = barriers.ToHashSet();
        var roots = new List<GameEntity>();
        foreach (GameEntity entity in barriers.Distinct())
        {
            GameEntity? ancestor = entity.Parent;
            while (ancestor != null && !tagged.Contains(ancestor))
            {
                ancestor = ancestor.Parent;
            }

            if (ancestor == null)
            {
                roots.Add(entity);
            }
        }

        return roots;
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

    private CompletionRespawn? GetCompletionRespawn(MissionPeer peer, bool allowNewAgent = false)
    {
        if (!_completionRespawns.TryGetValue(peer, out CompletionRespawn? completed))
        {
            return null;
        }

        NetworkCommunicator? networkPeer = peer.GetNetworkPeer();
        if (_ending || _sceneClearing || networkPeer == null
            || !networkPeer.IsConnectionActive || !networkPeer.IsSynchronized || peer.Team != completed.Team
            || (completed.SpawnFinished && !IsLivingAgent(completed.SpawnedAgent))
            || (!allowNewAgent && IsLivingAgent(peer.ControlledAgent) && !ReferenceEquals(peer.ControlledAgent, completed.SpawnedAgent)))
        {
            _completionRespawns.Remove(peer);
            return null;
        }

        return completed;
    }

    private void TryRestoreCompletedRespawn(MissionPeer peer, CompletionRespawn completed)
    {
        Agent? agent = completed.SpawnedAgent;
        if (!completed.SpawnFinished || !IsLivingAgent(agent) || !ReferenceEquals(peer.ControlledAgent, agent))
        {
            return;
        }

        try
        {
            if (!completed.HealthRestored)
            {
                agent!.HealthLimit = completed.HealthLimit;
                agent.BaseHealthLimit = completed.BaseHealthLimit;
                AdimiToolsMissionMultiplayerDuel.RestoreAgentForAiChallenge(agent);
                completed.HealthRestored = true;
            }

            if (!completed.EquipmentRestored)
            {
                // SpawnAgent/native deployment initialization and SpawnEnd
                // callbacks may have removed or replaced weapons after build.
                AdimiToolsAiArenaEquipment.Restore(agent!, completed.Equipment, completed.Banner);
                completed.EquipmentRestored = true;
            }

            _completionRespawns.Remove(peer);
        }
        catch (Exception exception)
        {
            completed.NextRestoreAt = Mission.CurrentTime + 1d;
            AdimiToolsConsoleLog.Log($"Completed AI respawn equipment restore will retry for {peer.Peer.Id}: {exception.Message}");
        }
    }

    private bool ShouldForfeitForNavigation(Arena arena, MissionPeer owner, Agent ownerAgent, double now)
    {
        // Folding a ladder does not create a raised enclosure. Keep the
        // existing navmesh safeguard when no raised barrier or box is present.
        bool enabled = arena.Barriers.All(motion => motion.IsLadder) && arena.Area == null
            && arena.Session.State == AdimiToolsAiArenaState.Running
            && !arena.AwaitingRespawn && IsLivingAgent(ownerAgent)
            && ReferenceEquals(owner.ControlledAgent, ownerAgent);
        return arena.NavigationGuard.Check(enabled, !enabled || IsOwnerOnNavigationMesh(ownerAgent), now);
    }

    private bool IsOwnerOnNavigationMesh(Agent ownerAgent)
    {
        // A mounted rider's position can be above the navmesh height tolerance;
        // query the living mount's feet instead of the saddle position.
        Agent footing = IsLivingAgent(ownerAgent.MountAgent) ? ownerAgent.MountAgent! : ownerAgent;
        Vec3 position = footing.Position;
        if (!position.IsValid || Mission.Scene.GetNavigationMeshForPosition(in position, out _, Scene.NavMeshHeightLimit, excludeDynamicNavigationMeshes: false) == UIntPtr.Zero)
        {
            return false;
        }

        // Use the current 3D position and reject disabled faces. Do not replace
        // an absent face with a nearest-navmesh or terrain projection.
        PathFaceRecord face = PathFaceRecord.NullFaceRecord;
        Mission.Scene.GetNavMeshFaceIndex(ref face, position, checkIfDisabled: true);
        return face.IsValid();
    }

    private Equipment SnapshotInitialEquipment(MissionPeer peer, Agent owner)
    {
        // A completed loss starts a new life with the class/perks selected at
        // reservation, independently of picked-up or dropped training weapons.
        Equipment equipment = owner.Character.Equipment.Clone();
        var alternatives = MPPerkObject.GetOnSpawnPerkHandler(peer)?.GetAlternativeEquipments(isPlayer: true);
        if (alternatives != null)
        {
            foreach (var alternative in alternatives)
            {
                equipment[alternative.Item1] = alternative.Item2;
            }
        }

        if (_duelMode != null)
        {
            _duelMode.AddCosmeticItemsToEquipment(equipment, _duelMode.GetUsedCosmeticsFromPeer(peer, owner.Character));
        }

        return equipment;
    }

    private Equipment SnapshotEquipment(Agent owner)
    {
        Equipment equipment = owner.SpawnEquipment.Clone();
        for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
        {
            MissionWeapon weapon = owner.Equipment[slot];
            equipment[slot] = weapon.IsEmpty ? EquipmentElement.Invalid : new EquipmentElement(weapon.Item, weapon.ItemModifier);
        }

        if (!IsLivingAgent(owner.MountAgent))
        {
            equipment[EquipmentIndex.Horse] = EquipmentElement.Invalid;
            equipment[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
        }
        else
        {
            equipment[EquipmentIndex.Horse] = owner.MountAgent!.SpawnEquipment[EquipmentIndex.Horse];
            equipment[EquipmentIndex.HorseHarness] = owner.MountAgent.SpawnEquipment[EquipmentIndex.HorseHarness];
        }

        return equipment;
    }

    private void Teleport(Agent agent, MatrixFrame frame)
    {
        if (!IsLivingAgent(agent))
        {
            return;
        }

        if (agent.IsUsingGameObject)
        {
            agent.StopUsingGameObject(isSuccessful: false, Agent.StopUsingGameObjectFlags.None);
        }

        Vec2 direction = frame.rotation.f.AsVec2.Normalized();
        if (direction.LengthSquared < 0.1f)
        {
            direction = new Vec2(0f, 1f);
        }

        Agent? mount = agent.MountAgent;
        if (IsLivingAgent(mount))
        {
            mount!.TeleportToPosition(frame.origin);
        }

        agent.TeleportToPosition(frame.origin);
        WorldPosition position = new(Mission.Current.Scene, frame.origin);
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new AgentTeleportToFrame(agent.Index, position.GetGroundVec3(), direction));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
    }

    private void ReleaseSharedIfClean(Arena arena, double now)
    {
        if (CanReleaseShared(arena, now) && TryAreaCleanup(arena))
        {
            arena.GlowsHidden = false;
            arena.Glows.Update(false, now);
            arena.Shared!.Release(arena);
        }
    }

    private void InitializeGroups()
    {
        string[] roles =
        {
            AdimiToolsAiArenaTags.TriggerRoleTag, AdimiToolsAiArenaTags.SpawnRoleTag,
            AdimiToolsAiArenaTags.BarrierRoleTag, AdimiToolsAiArenaTags.AreaRoleTag,
            AdimiToolsAiArenaTags.EnhancedTriggerRoleTag, AdimiToolsAiArenaTags.SimpleTriggerRoleTag,
            AdimiToolsAiArenaTags.NormalTriggerRoleTag, AdimiToolsAiArenaTags.HardTriggerRoleTag,
            AdimiToolsAiArenaTags.LadderRoleTag,
        };
        var entities = roles.SelectMany(role => Mission.Scene.FindEntitiesWithTag(role))
            .Concat(Mission.Scene.FindEntitiesWithTagExpression("adimi_ai_trigger(_.*)*")).Distinct().ToList();
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
            // Numbered difficulty tags modify one trigger role. Preserve the
            // original role and group when copying an existing statue.
            bool isTrigger = entity.Tags.Any(AdimiToolsAiArenaTags.IsTriggerTag);
            int roleCount = (isTrigger ? 1 : 0)
                + roles.Skip(1).Take(3).Count(entity.HasTag)
                + (entity.HasTag(AdimiToolsAiArenaTags.LadderRoleTag) ? 1 : 0);
            if (tags.Length != 1 || groupIds.Length != 1 || roleCount != 1
                || (isTrigger && !AdimiToolsAiArenaTags.TryGetTriggerDifficulty(entity.Tags, out _)))
            {
                foreach (int id in groupIds)
                {
                    invalidGroups.Add(id);
                }

                AdimiToolsConsoleLog.Log($"AI arena skipped entity '{entity.Name}': use exactly one role, one difficulty selector and one canonical group tag.");
                continue;
            }

            if (!groups.TryGetValue(groupIds[0], out List<GameEntity>? members))
            {
                members = new List<GameEntity>();
                groups.Add(groupIds[0], members);
            }

            members.Add(entity);
        }

        var barrierGroups = groups.SelectMany(group => group.Value
                .Where(entity => entity.HasTag(AdimiToolsAiArenaTags.BarrierRoleTag) || entity.HasTag(AdimiToolsAiArenaTags.LadderRoleTag))
                .Select(entity => (Entity: entity, GroupId: group.Key)))
            .ToDictionary(entry => entry.Entity, entry => entry.GroupId);
        foreach (var entry in barrierGroups)
        {
            for (GameEntity? parent = entry.Key.Parent; parent != null; parent = parent.Parent)
            {
                if (barrierGroups.TryGetValue(parent, out int parentGroup)
                    && (parentGroup != entry.Value || parent.HasTag(AdimiToolsAiArenaTags.LadderRoleTag) != entry.Key.HasTag(AdimiToolsAiArenaTags.LadderRoleTag)))
                {
                    invalidGroups.Add(parentGroup);
                    invalidGroups.Add(entry.Value);
                    AdimiToolsConsoleLog.Log($"AI groups {parentGroup}/{entry.Value} disabled: moving entity '{entry.Key.Name}' is below a different group or motion role; a moving hierarchy must use one group and direction.");
                }
            }
        }

        foreach (var group in groups)
        {
            List<GameEntity> normalTriggers = group.Value.Where(entity => AdimiToolsAiArenaTags.TryGetTriggerDifficulty(entity.Tags, out int level) && level == AdimiToolsAiArenaDifficulty.Simple).ToList();
            List<GameEntity> enhancedTriggers = group.Value.Where(entity => AdimiToolsAiArenaTags.TryGetTriggerDifficulty(entity.Tags, out int level) && level == AdimiToolsAiArenaDifficulty.Normal).ToList();
            List<GameEntity> hardTriggers = group.Value.Where(entity => AdimiToolsAiArenaTags.TryGetTriggerDifficulty(entity.Tags, out int level) && level == AdimiToolsAiArenaDifficulty.Hard).ToList();
            List<GameEntity> triggers = normalTriggers.Concat(enhancedTriggers).Concat(hardTriggers).ToList();
            List<GameEntity> spawns = group.Value.Where(entity => entity.HasTag(roles[1])).ToList();
            List<GameEntity> barriers = group.Value.Where(entity => entity.HasTag(roles[2])).ToList();
            List<GameEntity> ladders = group.Value.Where(entity => entity.HasTag(AdimiToolsAiArenaTags.LadderRoleTag)).ToList();
            List<GameEntity> areas = group.Value.Where(entity => entity.HasTag(roles[3])).ToList();
            if (invalidGroups.Contains(group.Key) || !AdimiToolsAiArenaTags.IsValidRoleCounts(normalTriggers.Count, spawns.Count, barriers.Count, areas.Count, enhancedTriggers.Count, hardTriggers.Count, ladders.Count))
            {
                AdimiToolsConsoleLog.Log($"AI group {group.Key} disabled: needs one spawn and at least one trigger (at most one per difficulty); barriers/ladders may be omitted or multiple, area may be omitted or appear once; moving hierarchies cannot span groups or directions.");
                continue;
            }

            List<GameEntity> movingEntities = barriers.Concat(ladders).ToList();
            List<GameEntity> barrierRoots = SelectBarrierRoots(movingEntities);
            var motions = new List<BarrierMotion>();
            bool invalidBarrier = false;
            foreach (GameEntity root in barrierRoots)
            {
                SynchedMissionObject? synched = root.GetFirstScriptOfType<SynchedMissionObject>();
                if (synched == null || synched.GetType() != typeof(SynchedMissionObject) || synched.Id.Id < 0)
                {
                    invalidBarrier = true;
                    AdimiToolsConsoleLog.Log($"AI group {group.Key} disabled: moving root '{root.Name}' needs the original SynchedMissionObject and a valid scene ID; set this root to Dynamic in the editor.");
                    continue;
                }

                bool isLadder = root.HasTag(AdimiToolsAiArenaTags.LadderRoleTag);
                MatrixFrame idleWorld = root.GetGlobalFrame();
                MatrixFrame activeFrame = idleWorld;
                if (isLadder)
                {
                    try
                    {
                        activeFrame = AdimiToolsAiArenaLadderRotation.CreateActiveFrame(in idleWorld);
                    }
                    catch (ArgumentException exception)
                    {
                        invalidBarrier = true;
                        AdimiToolsConsoleLog.Log($"AI group {group.Key} disabled: ladder '{root.Name}' has an invalid rotation frame: {exception.Message}");
                        continue;
                    }
                }

                motions.Add(new BarrierMotion
                {
                    Synched = synched,
                    Entity = root,
                    LoweredFrame = idleWorld,
                    OriginalLocalFrame = root.GetFrame(),
                    IsLadder = isLadder,
                    ActiveFrame = activeFrame,
                    ActiveOffsetZ = isLadder ? 0f : AdimiToolsAiArenaSession<MissionPeer>.BarrierRiseMeters,
                });
            }

            VolumeBox? area = areas.Count == 0 ? null : areas[0].GetFirstScriptOfType<VolumeBox>();
            if (invalidBarrier
                || (areas.Count != 0 && area == null)
                || triggers.Any(trigger => trigger.HasTag("teleport_door") || trigger.HasTag(AdimiToolsDuelModes.CycleTag)
                    || AdimiToolsDuelModes.GetAnvilLimit(trigger.Tags) != 0)
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
                Triggers = triggers,
                Spawn = spawns[0],
                Barriers = motions,
                Area = area,
                Shared = area != null && areas[0].HasTag("adimi_team_area") ? AdimiToolsSharedArena.For(Mission, area) : null,
                Glows = AdimiToolsArenaGlowGroup.Create(Mission.Scene, areas.Count == 0 ? null : areas[0]),
            };
            try
            {
                foreach (GameEntity triggerEntity in arena.Triggers)
                {
                    AdimiToolsAiArenaTrigger? trigger = triggerEntity.GetFirstScriptOfType<AdimiToolsAiArenaTrigger>();
                    if (trigger == null)
                    {
                        triggerEntity.CreateAndAddScriptComponent(nameof(AdimiToolsAiArenaTrigger), callScriptCallbacks: true);
                        trigger = triggerEntity.GetFirstScriptOfType<AdimiToolsAiArenaTrigger>();
                    }

                    if (trigger == null)
                    {
                        throw new InvalidOperationException("Could not attach AI hit observer.");
                    }

                    AdimiToolsAiArenaTags.TryGetTriggerDifficulty(triggerEntity.Tags, out int difficulty);
                    trigger.Bind(this, arena.Id, difficulty);
                }

                arena.Shared?.RegisterBoundary(arena, isAi: true, arena.Barriers.Select(barrier => barrier.Entity),
                    (raised, immediately) => MoveBarrierCore(arena, raised, immediately),
                    now => UpdateSharedBoundaryMovement(arena, now),
                    raised => AreOwnBarriersAtTarget(arena, raised));
                _arenas.Add(arena.Id, arena);
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} ready; barrier={barriers.Count != 0}, moving roots={motions.Count}, ladder roots={motions.Count(motion => motion.IsLadder)}, inherited tagged children={movingEntities.Count - barrierRoots.Count}, area={area != null}, simple triggers={normalTriggers.Count}, normal triggers={enhancedTriggers.Count}, hard triggers={hardTriggers.Count}.");
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} initialization failed: {exception}");
            }
        }
    }

    private bool IsAgentPresent(Agent? agent)
    {
        return !_sceneClearing && agent != null && Mission.AllAgents.Contains(agent);
    }

    private bool IsLivingAgent(Agent? agent)
    {
        return agent != null && IsAgentPresent(agent) && agent.IsActive() && agent.Health > 0f && !agent.IsFadingOut();
    }

    private MatrixFrame GetPlayerGroundFrame(Agent agent)
    {
        Agent? mount = agent.MountAgent;
        return IsLivingAgent(mount) ? mount!.Frame : agent.Frame;
    }

    private bool BeginAreaCleanup(Arena arena)
    {
        if (arena.Area == null)
        {
            return true;
        }

        // A second pass catches equipment dropped by deferred corpse removal.
        arena.AreaCleanupUntil = Mission.CurrentTime + 1d;
        if (arena.AreaCleanupPending && Mission.CurrentTime < arena.NextAreaCleanupAt)
        {
            return false;
        }

        return TryAreaCleanup(arena);
    }

    private bool TryAreaCleanup(Arena arena)
    {
        if (_sceneClearing)
        {
            // Native ClearScene owns old actors and items during mission reset.
            return true;
        }

        bool cleared;
        try
        {
            cleared = AdimiToolsAiArenaCleanup.TryClear(Mission, arena.Area);
        }
        catch (Exception exception)
        {
            cleared = false;
            AdimiToolsConsoleLog.Log($"AI group {arena.Id} area cleanup will retry: {exception.Message}");
        }

        arena.AreaCleanupPending = !cleared;
        arena.NextAreaCleanupAt = Mission.CurrentTime + (cleared ? CheckInterval : 1d);
        return cleared;
    }

    private Arena? FindRespawningArena(MissionPeer peer)
    {
        return _arenas.Values.FirstOrDefault(arena => !arena.Failed && arena.Session.IsReserved
            && ReferenceEquals(arena.Session.Owner, peer) && arena.Series?.IsComplete == false
            && arena.RoundPending && arena.AwaitingRespawn);
    }

    private void StartChallenge(Arena arena, MissionPeer owner, Agent ownerAgent, double now)
    {
        if (arena.Shared != null && !arena.Shared.IsAtTarget(arena, raised: true))
        {
            return;
        }

        if (!BeginAreaCleanup(arena))
        {
            return;
        }

        if (!ClearPlayersBeforeRound(arena, owner, out string failure))
        {
            ResetArena(arena, failure);
            return;
        }

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
        arena.StartedAiCount = arena.Session.RequestedCount;
        arena.RoundPending = false;
        arena.OwnerDiedThisRound = false;

        arena.OwnerMount = IsLivingAgent(ownerAgent.MountAgent) ? ownerAgent.MountAgent : null;
        arena.OriginalMountTeam = arena.OwnerMount?.Team;
        owner.Team = arena.PlayerTeam;
        ownerAgent.SetTeam(arena.PlayerTeam!, sync: true);
        arena.OwnerMount?.SetTeam(arena.PlayerTeam!, sync: true);
        PrepareRoundOwner(arena, ownerAgent);
        SpawnRoundBots(arena, ownerAgent);
        arena.GlowsHidden = true;
        arena.Glows.Update(true, now);
        _duelMode?.BeginAiChallengeDisplay(arena.Id, owner, ownerAgent, arena.StartedAiCount, arena.Difficulty, arena.HardProfile?.TroopName);
        Notify(owner, $"已生成 {arena.Bots.Count} 个{AdimiToolsAiArenaDifficulty.GetName(arena.Difficulty)} AI，只会与你交战。本次{AdimiToolsDuelModes.GetModeName(arena.Series.FirstToLimit)}。");
    }

    private void SpawnRoundBots(Arena arena, Agent ownerAgent, bool activate = true)
    {
        arena.PlayerTeam!.SetIsEnemyOf(arena.BotTeam!, activate);
        arena.BotTeam!.SetIsEnemyOf(arena.PlayerTeam, activate);
        var botCharacter = arena.HardProfile?.Character ?? arena.OriginalCharacter!.AgentCharacter;
        Equipment botEquipment = arena.HardProfile?.Equipment ?? arena.Equipment!;

        foreach (Vec3 position in arena.SpawnPositions)
        {
            Vec2 direction = (ownerAgent.Position - position).AsVec2.Normalized();
            if (direction.LengthSquared < 0.1f)
            {
                direction = new Vec2(0f, 1f);
            }

            var build = new AgentBuildData(botCharacter)
                .Team(arena.BotTeam!)
                .Controller(AgentControllerType.AI)
                .TroopOrigin(arena.Enhanced
                    ? new AdimiToolsAiArenaEnhancedOrigin(botCharacter, arena.Difficulty, arena.HardProfile)
                    : new BasicBattleAgentOrigin(botCharacter))
                .Equipment(botEquipment.Clone())
                .FixedEquipment(true)
                .NoHorses(botEquipment[EquipmentIndex.Horse].IsEmpty)
                .BodyProperties(ownerAgent.BodyPropertiesValue)
                .IsFemale(ownerAgent.IsFemale)
                .ClothingColor1(ownerAgent.ClothingColor1)
                .ClothingColor2(ownerAgent.ClothingColor2)
                .InitialPosition(in position)
                .InitialDirection(in direction);
            if (arena.HardProfile == null && !string.IsNullOrEmpty(arena.MountKey))
            {
                build.MountKey(arena.MountKey);
            }

            AdimiToolsDuelAppearance.ApplyToBuild(build, arena.HardProfile?.Culture ?? arena.Session.Owner?.Culture);

            // Never attach MissionPeer(owner): that would replace the human's
            // ControlledAgent. Each AI has independent equipment and ownership.
            Agent bot = Mission.SpawnAgent(build, spawnFromAgentVisuals: false);
            arena.Bots.Add(bot);
            if (bot.MountAgent != null)
            {
                arena.Mounts.Add(bot.MountAgent);
            }

            float healthMultiplier = arena.Enhanced ? AdimiToolsAiArenaDifficulty.EnhancedHealthMultiplier : 1f;
            bot.HealthLimit = arena.HardProfile?.HealthLimit ?? arena.HealthLimit * healthMultiplier;
            bot.BaseHealthLimit = arena.HardProfile?.BaseHealthLimit ?? arena.BaseHealthLimit * healthMultiplier;
            bot.Health = bot.HealthLimit;
            // MP armor bonuses normally come from a player's MissionPeer. These
            // independent bots must not claim that peer just to obtain armor.
            bot.AgentDrivenProperties.ArmorHead = arena.HardProfile?.ArmorHead ?? arena.ArmorHead;
            bot.AgentDrivenProperties.ArmorTorso = arena.HardProfile?.ArmorTorso ?? arena.ArmorTorso;
            bot.AgentDrivenProperties.ArmorArms = arena.HardProfile?.ArmorArms ?? arena.ArmorArms;
            bot.AgentDrivenProperties.ArmorLegs = arena.HardProfile?.ArmorLegs ?? arena.ArmorLegs;
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
        _duelMode?.RecordAiChallengeDisplayRound(arena.Id, playerWon);
        if (finished)
        {
            ResetArena(arena, $"AI 系列结束：你 {arena.Series.PlayerWins}:{arena.Series.AiWins} AI，{(arena.Series.WinnerPlayer == true ? "你获胜" : "AI 获胜")}。阻挡物将在 1 秒后开始复位，AI 在 3 秒后清理。");
            return;
        }

        arena.PlayerTeam!.SetIsEnemyOf(arena.BotTeam!, false);
        arena.BotTeam!.SetIsEnemyOf(arena.PlayerTeam, false);
        arena.AwaitingRespawn = !playerWon;
        PrepareRoundSurvivors(arena);
        BeginAreaCleanup(arena);
        arena.NextRoundAt = now + AdimiToolsAiArenaSession<MissionPeer>.CountdownSeconds;
        arena.RespawnDeadline = now + 30d;
        arena.OwnerDiedThisRound = false;
        if (playerWon)
        {
            PreparePendingRound(arena, arena.Session.Owner!, arena.OwnerAgent!, now);
            return;
        }
        else if (IsLivingAgent(arena.OwnerMount) && arena.OwnerMount!.Team == arena.PlayerTeam)
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

        Notify(arena.Session.Owner!, $"当前比分 {arena.Series.PlayerWins}:{arena.Series.AiWins}（{AdimiToolsDuelModes.GetModeName(arena.Series.FirstToLimit)}）。{(arena.Bots.Count > 0 ? "存活 AI 保留" : "双方阵亡")}，复活后继续本系列。");
    }

    private void PrepareRoundSurvivors(Arena arena)
    {
        foreach (Agent bot in arena.Bots.ToArray())
        {
            if (IsLivingAgent(bot))
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
            if (!IsLivingAgent(mount))
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
            if (!IsLivingAgent(mount) || arena.AwaitingRespawn || !IsLivingAgent(arena.OwnerAgent))
            {
                if (IsLivingAgent(mount))
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
        if (!BeginAreaCleanup(arena))
        {
            return false;
        }

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
        Notify(owner, $"当前比分 {arena.Series!.PlayerWins}:{arena.Series.AiWins}（{AdimiToolsDuelModes.GetModeName(arena.Series.FirstToLimit)}），下一局 3 秒后开始。");
        return true;
    }

    private void StartNextRound(Arena arena, MissionPeer owner, Agent ownerAgent)
    {
        if (arena.Shared != null && !arena.Shared.IsAtTarget(arena, raised: true))
        {
            return;
        }

        if (!BeginAreaCleanup(arena))
        {
            return;
        }

        if (!ClearPlayersBeforeRound(arena, owner, out string failure))
        {
            ResetArena(arena, failure);
            return;
        }

        if (!arena.Bots.Any(IsLivingAgent))
        {
            PreparePendingRound(arena, owner, ownerAgent, Mission.CurrentTime);
            return;
        }

        arena.OwnerDiedThisRound = false;
        arena.RoundPending = false;
        arena.PlayerTeam!.SetIsEnemyOf(arena.BotTeam!, true);
        arena.BotTeam!.SetIsEnemyOf(arena.PlayerTeam, true);
        foreach (Agent bot in arena.Bots.Where(IsLivingAgent))
        {
            bot.SetWatchState(Agent.WatchState.Alarmed);
            bot.SetAutomaticTargetSelection(false);
            bot.SetTargetAgent(ownerAgent);
        }

        Notify(owner, $"本局开始：你 {arena.Series!.PlayerWins}:{arena.Series.AiWins} AI（{AdimiToolsDuelModes.GetModeName(arena.Series.FirstToLimit)}）。");
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
        Equipment botEquipment = arena.HardProfile?.Equipment ?? arena.Equipment!;
        float spacing = botEquipment[EquipmentIndex.Horse].IsEmpty ? 1.6f : 3f;
        Vec3[] occupiedPositions = Mission.Agents.Where(IsLivingAgent).Select(agent => agent.Position).ToArray();
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
                || occupiedPositions.Any(occupied => occupied.DistanceSquared(ground) < spacing * spacing))
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

    private bool IsOutsideAllAreas(Vec3 position, Arena current)
    {
        return (current.Area == null || !current.Area.IsPointIn(position))
            && !_arenas.Values.Any(arena => arena.Area != null && arena.Area.IsPointIn(position))
            && Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPositionInArena(position) != true;
    }

    private MatrixFrame? GetOutsideSpawnForPlayer(Agent agent, Arena arena)
    {
        bool mounted = IsLivingAgent(agent.MountAgent);
        return Mission.Scene.FindEntitiesWithTag("spawnpoint")
            .Where(entity => IsOutsideAllAreas(entity.GlobalPosition, arena)
                && !(mounted && entity.HasTag("exclude_mounted"))
                && !(!mounted && entity.HasTag("exclude_footmen")))
            .OrderBy(entity => entity.GlobalPosition.DistanceSquared(agent.Position))
            .FirstOrDefault()?.GetGlobalFrame();
    }

    private bool ClearPlayersBeforeRound(Arena arena, MissionPeer owner, out string failure)
    {
        failure = string.Empty;
        if (arena.Area == null)
        {
            return true;
        }

        var evacuations = new List<(MissionPeer peer, Agent agent, MatrixFrame frame)>();
        foreach (NetworkCommunicator networkPeer in GameNetwork.NetworkPeers)
        {
            MissionPeer? peer = networkPeer.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            if (peer == null || ReferenceEquals(peer, owner) || !networkPeer.IsSynchronized || !networkPeer.IsConnectionActive
                || agent == null || !IsLivingAgent(agent) || !agent.IsHuman
                || !arena.Area.IsPointIn(GetPlayerGroundFrame(agent).origin))
            {
                continue;
            }

            // A new challenge cannot take over a different active match. A
            // pending invitation alone does not protect a normal bystander.
            if (_duelMode?.IsPeerInRealDuelForAiChallenge(peer) == true || IsPeerReserved(peer)
                || Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(peer) == true)
            {
                failure = "训练区域内有正在对战的玩家，本次 AI 挑战已取消，请等区域空闲后重试。";
                return false;
            }

            MatrixFrame? destination = GetOutsideSpawnForPlayer(agent, arena);
            if (!destination.HasValue)
            {
                failure = "没有适合场内玩家的区域外复活点，本次 AI 挑战已取消，请检查地图出生点。";
                return false;
            }

            evacuations.Add((peer, agent, destination.Value));
        }

        // Validate every destination before moving anyone. The owner stays in
        // place, and no AI/enemy relation is enabled until this pass succeeds.
        foreach (var evacuation in evacuations)
        {
            Teleport(evacuation.agent, evacuation.frame);
            Notify(evacuation.peer, "此区域的 AI 挑战即将开始，已将你移至区域外复活点。");
        }

        return true;
    }

    private bool EvictOtherPlayers(Arena arena, MissionPeer owner)
    {
        Agent? ownerAgent = arena.OwnerAgent;
        Agent[] bots = arena.Bots.Where(IsLivingAgent).ToArray();
        if (arena.Area == null || arena.Session.State != AdimiToolsAiArenaState.Running
            || arena.RoundPending || !IsLivingAgent(ownerAgent) || bots.Length == 0)
        {
            return true;
        }

        foreach (NetworkCommunicator networkPeer in GameNetwork.NetworkPeers)
        {
            MissionPeer? peer = networkPeer.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            if (peer == null || ReferenceEquals(peer, owner) || !networkPeer.IsSynchronized || !networkPeer.IsConnectionActive
                || agent == null || !IsLivingAgent(agent) || !agent.IsHuman)
            {
                continue;
            }

            float distanceSquared = bots.Select(bot => bot.Position.DistanceSquared(agent.Position))
                .Append(ownerAgent!.Position.DistanceSquared(agent.Position)).Min();
            if (distanceSquared >= AdimiToolsDuelInterference.WarningDistance * AdimiToolsDuelInterference.WarningDistance)
            {
                continue;
            }

            if (!_nextInterferenceWarningAt.TryGetValue(peer, out double nextWarning) || Mission.CurrentTime >= nextWarning)
            {
                _nextInterferenceWarningAt[peer] = Mission.CurrentTime + AdimiToolsDuelInterference.WarningIntervalSeconds;
                Notify(peer, "请勿靠近正在交战的玩家或 AI；靠近 1 米内会被移至区域外复活点。");
            }

            if (distanceSquared >= AdimiToolsDuelInterference.EvictionDistance * AdimiToolsDuelInterference.EvictionDistance)
            {
                continue;
            }

            if (_duelMode?.IsPeerInRealDuelForAiChallenge(peer) == true || IsPeerReserved(peer)
                || Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(peer) == true)
            {
                // Never teleport somebody out of another live PvP/AI match.
                return false;
            }

            MatrixFrame? destination = GetOutsideSpawnForPlayer(agent, arena);
            if (!destination.HasValue)
            {
                return false;
            }

            Teleport(agent, destination.Value);
            Notify(peer, "你距离交战者过近，已将你移至区域外复活点。");
        }

        return true;
    }

    private void RecoverCompletedOwner(Arena arena, MissionPeer owner)
    {
        if (IsLivingAgent(arena.OwnerAgent) && ReferenceEquals(owner.ControlledAgent, arena.OwnerAgent))
        {
            if (!arena.CompletionHealthRestored)
            {
                // Re-equipping or wielding weapons interrupts an attack. Refill
                // the surviving actor's current kit without replacing entities.
                arena.OwnerAgent!.HealthLimit = arena.HealthLimit;
                arena.OwnerAgent.BaseHealthLimit = arena.BaseHealthLimit;
                AdimiToolsMissionMultiplayerDuel.RestoreAgentForAiChallenge(arena.OwnerAgent);
                arena.CompletionHealthRestored = true;
            }

            return;
        }

        if (arena.RegularRespawnEquipment == null || arena.OriginalCharacter == null)
        {
            return;
        }

        _completionRespawns[owner] = new CompletionRespawn
        {
            Frame = arena.PlayerDeathFrame ?? arena.LastPlayerFrame,
            UseRegularSpawn = true,
            Character = arena.OriginalCharacter,
            Equipment = arena.RegularRespawnEquipment.Clone(),
            Banner = arena.Banner == null ? null : new Banner(arena.Banner),
            MountKey = null,
            Team = arena.OriginalTeam ?? Mission.AttackerTeam,
            HealthLimit = arena.HealthLimit,
            BaseHealthLimit = arena.BaseHealthLimit,
        };
    }

    private void FadeArenaActor(Agent actor)
    {
        if (!IsAgentPresent(actor) || actor.State == AgentState.Deleted || actor.IsFadingOut())
        {
            return;
        }

        Agent? rider = actor.IsMount ? actor.RiderAgent : null;
        if (IsLivingAgent(rider) && rider!.MissionPeer != null)
        {
            // A human may mount a surviving AI horse during the exit delay.
            // Never remove an occupied player mount from underneath its rider.
            actor.SetTeam(rider.Team, sync: true);
            return;
        }

        if (IsLivingAgent(actor) || Mission.GetMissionBehavior<MissionLobbyComponent>() is IAdimiToolsSingleRemovalLobby)
        {
            // The original lobby ledger prevents corpse fade callbacks from
            // counting the same bot death twice, including groups without Box.
            actor.FadeOut(hideInstantly: true, hideMount: false);
        }
    }

    private void ResetArena(Arena arena, string? message, bool immediately = false)
    {
        if (arena.Resetting || (arena.Shared != null && !arena.Shared.Owns(arena)))
        {
            return;
        }

        arena.Resetting = true;
        if (!arena.CompletionStartedAt.HasValue)
        {
            _duelMode?.EndAiChallengeDisplay(arena.Id);
        }

        MissionPeer? owner = arena.Session.Owner ?? arena.CleanupOwner;
        NetworkCommunicator? ownerNetwork = owner?.GetNetworkPeer();
        bool delayCompleted = !immediately && !_ending && !_sceneClearing && arena.Series?.IsComplete == true;
        if (delayCompleted)
        {
            arena.CompletionStartedAt ??= Mission.CurrentTime;
        }

        bool clearAi = !delayCompleted || Mission.CurrentTime >= arena.CompletionStartedAt!.Value + CompletedAiCleanupDelay;
        bool openBarrier = !delayCompleted || Mission.CurrentTime >= arena.CompletionStartedAt!.Value + CompletedBarrierOpenDelay;
        bool recoverCompleted = !arena.CompletionRecoveryStarted && arena.Series?.IsComplete == true
            && !_ending && !_sceneClearing && ownerNetwork != null && ownerNetwork.IsConnectionActive && ownerNetwork.IsSynchronized;
        arena.CleanupOwner = owner;
        arena.Session.Reset();
        arena.NavigationGuard.Reset();
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
                    if (IsLivingAgent(bot))
                    {
                        bot.SetTargetAgent(null!);
                    }

                    if (!clearAi)
                    {
                        return;
                    }

                    FadeArenaActor(bot);
                    arena.Bots.Remove(bot);
                }, failures);
            }

            foreach (Agent mount in arena.Mounts.ToArray())
            {
                AttemptCleanup(() =>
                {
                    if (!clearAi)
                    {
                        return;
                    }

                    FadeArenaActor(mount);
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

                if (ReferenceEquals(owner.ControlledAgent, arena.OwnerAgent) && IsLivingAgent(arena.OwnerAgent)
                    && arena.OwnerAgent!.Team == arena.PlayerTeam)
                {
                    AttemptCleanup(() => arena.OwnerAgent.SetTeam(team, sync: true), failures);
                }
            }

            if (IsLivingAgent(arena.OwnerMount) && arena.OwnerMount!.Team == arena.PlayerTeam)
            {
                AttemptCleanup(() => arena.OwnerMount.SetTeam(arena.OriginalMountTeam!, sync: true), failures);
            }

            foreach (Agent mount in arena.RespawnedPlayerMounts.ToArray())
            {
                AttemptCleanup(() =>
                {
                    if (IsLivingAgent(mount))
                    {
                        if (IsLivingAgent(arena.OwnerAgent) && ReferenceEquals(arena.OwnerAgent!.MountAgent, mount))
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

            if (recoverCompleted && owner != null)
            {
                AttemptCleanup(() =>
                {
                    RecoverCompletedOwner(arena, owner);
                    arena.CompletionRecoveryStarted = true;
                }, failures);
            }

            if (!arena.ScoreSettled && arena.Series?.IsComplete == true && owner != null
                && !_ending && !_sceneClearing && ownerNetwork?.IsConnectionActive == true && ownerNetwork.IsSynchronized)
            {
                AttemptCleanup(() =>
                {
                    int gainedScore = (_duelMode ?? throw new InvalidOperationException("AI result requires the duel game mode."))
                        .RecordAiChallengeResult(owner, arena.Series.WinnerPlayer == true);
                    arena.ScoreSettled = true;
                    AdimiToolsConsoleLog.Log($"AI result: playerId={owner.Peer.Id}, group={arena.Id}, aiCount={arena.StartedAiCount}, difficulty={arena.Difficulty}, enhanced={arena.Enhanced}, ft={arena.Series.FirstToLimit}, playerRounds={arena.Series.PlayerWins}, aiRounds={arena.Series.AiWins}, won={arena.Series.WinnerPlayer == true}, scoreGain={gainedScore}.");
                }, failures);
            }

            if (clearAi)
            {
                BeginAreaCleanup(arena);
            }

            if ((openBarrier && arena.BarrierEngaged) || immediately)
            {
                AttemptCleanup(() =>
                {
                    MoveBarrier(arena, raised: false, immediately);
                    arena.BarrierEngaged = false;
                    arena.LoweringUntil = arena.Barriers.Count == 0 || immediately ? 0d : Mission.CurrentTime + AdimiToolsAiArenaSession<MissionPeer>.BarrierTravelSeconds;
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
            bool completionWaiting = delayCompleted && (!clearAi || arena.BarrierEngaged);
            arena.CleanupPending = failures.Count != 0 || completionWaiting;
            if (completionWaiting && !clearAi)
            {
                double deadline = arena.CompletionStartedAt!.Value
                    + (!openBarrier && arena.BarrierEngaged ? CompletedBarrierOpenDelay : CompletedAiCleanupDelay);
                _nextCheckAt = Math.Min(_nextCheckAt, deadline);
            }

            if (failures.Count != 0)
            {
                arena.Failed = true;
                AdimiToolsConsoleLog.Log($"AI group {arena.Id} cleanup will retry: {new AggregateException(failures)}");
            }
            else if (!completionWaiting)
            {
                arena.CleanupOwner = null;
                arena.OwnerAgent = null;
                arena.OwnerMount = null;
                arena.OriginalMountTeam = null;
                arena.MountKey = null;
                arena.OriginalTeam = null;
                arena.Equipment = null;
                arena.RegularRespawnEquipment = null;
                arena.CompletionStartedAt = null;
                arena.Banner = null;
                arena.Difficulty = AdimiToolsAiArenaDifficulty.Simple;
                arena.HardProfile = null;
                arena.Series = null;
                arena.RoundPending = false;
                arena.AwaitingRespawn = false;
                arena.OwnerDiedThisRound = false;
                arena.RespawnNoticePending = false;
                arena.PlayerDeathFrame = null;
                arena.OriginalCharacter = null;
                arena.NextRoundAt = 0d;
                arena.RespawnDeadline = 0d;
                arena.SpawnPositions.Clear();
                if (!arena.AreaCleanupPending && Mission.CurrentTime >= arena.LoweringUntil && arena.Shared == null)
                {
                    arena.GlowsHidden = false;
                    arena.Glows.Update(false, Mission.CurrentTime);
                }
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
        _completionRespawns.Clear();
        _nextInterferenceWarningAt.Clear();
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
            finally
            {
                if (!_sceneClearing)
                {
                    arena.Glows.RestoreAndForget(Mission.CurrentTime);
                }
                else
                {
                    arena.Glows.Forget();
                }

                if (CanReleaseShared(arena, Mission.CurrentTime))
                {
                    arena.Shared?.Release(arena);
                }
            }
        }
    }
}
