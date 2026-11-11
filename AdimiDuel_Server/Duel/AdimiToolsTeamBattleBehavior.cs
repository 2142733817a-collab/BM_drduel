using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsTeamBattleBehavior : MissionLogic
{
    private sealed record SpatialArea(GameEntity Entity, VolumeBox Box, int Id, AdimiToolsSharedArena? Shared);

    private sealed class Member
    {
        public MissionPeer Peer { get; init; } = null!;
        public Agent Agent { get; set; } = null!;
        public Team OriginalTeam { get; init; } = null!;
        public Agent? Mount { get; init; }
        public Team? MountTeam { get; init; }
        public bool SideA { get; init; }
        public bool Eliminated { get; set; }
        public bool Withdrawn { get; set; }
        public bool Refilled { get; set; }
        public AgentBuildData Character { get; init; } = null!;
        public Equipment Equipment { get; init; } = null!;
        public Banner? Banner { get; init; }
        public string? MountKey { get; init; }
        public bool IsFemale { get; init; }
        public MatrixFrame InitialFrame { get; init; }
        public float HealthLimit { get; init; }
        public float BaseHealthLimit { get; init; }
        public float ArmorHead { get; init; }
        public float ArmorTorso { get; init; }
        public float ArmorArms { get; init; }
        public float ArmorLegs { get; init; }
        public HashSet<Agent> ManagedMounts { get; } = new(ReferenceEqualityComparer.Instance);
        public bool AwaitingRespawn { get; set; }
        public bool RespawnConfigured { get; set; }
        public bool SpawnFinished { get; set; }
        public bool RespawnHealthRestored { get; set; }
        public bool RespawnEquipmentRestored { get; set; }
        public bool RoundRefilled { get; set; }
        public double NextRestoreAt { get; set; }
        public MatrixFrame? RespawnFrame { get; set; }
    }

    private sealed class Arena
    {
        public int Id { get; init; }
        public VolumeBox SideA { get; init; } = null!;
        public VolumeBox SideB { get; init; } = null!;
        public VolumeBox Area { get; init; } = null!;
        public AdimiToolsSharedArena? Shared { get; init; }
        public AdimiToolsSharedArena? DuelBoundary { get; init; }
        public List<AdimiToolsTeamBattleBarrier> Barriers { get; init; } = new();
        public AdimiToolsTeamBattleReadiness<Agent> Readiness { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<MissionPeer, Member> Members { get; } = new();
        public HashSet<MissionPeer> Candidates { get; } = new();
        public Team? TeamA { get; set; }
        public Team? TeamB { get; set; }
        public AdimiToolsTeamBattleSeries? Series { get; set; }
        public bool FriendlyFireEnabled { get; set; }
        public int TargetWins { get; set; } = 1;
        public double NextModeToggleAt { get; set; }
        public double NextFriendlyFireToggleAt { get; set; }
        public bool NormalCompletion { get; set; }
        public double? NextRoundAt { get; set; }
        public double RespawnDeadline { get; set; }
        public double NextRoundCleanupAt { get; set; }
        public bool RoundAreaClean { get; set; }
        public double? EndedAt { get; set; }
        public HashSet<AdimiToolsTeamBattleBarrier> LoweringStarted { get; } = new();
        public bool SharedLoweringStarted { get; set; }
        public double? SweepUntil { get; set; }
        public double NextSweepAt { get; set; }
        public bool LastSweepSucceeded { get; set; }
        public bool Disabled { get; set; }
        public string LastNotice { get; set; } = string.Empty;
        public double NextErrorLogAt { get; set; }
    }

    private readonly List<Arena> _arenas = new();
    private AdimiToolsMissionMultiplayerDuel? _duelMode;
    private bool _stopped;

    public override void AfterStart()
    {
        base.AfterStart();
        if (GameNetwork.IsServer)
        {
            _duelMode = Mission.GetMissionBehavior<AdimiToolsMissionMultiplayerDuel>();
            if (_duelMode != null)
            {
                Initialize();
            }
        }
    }

    public bool IsPeerReserved(MissionPeer peer) => !_stopped && _arenas.Any(arena => IsReserved(arena, peer));

    public bool HasPendingRoundRespawn(MissionPeer peer)
    {
        var found = FindMember(peer);
        return found.arena != null && IsPendingRespawn(found.arena, found.member!);
    }

    public bool ShouldDelayRespawn(MissionPeer peer)
    {
        var found = FindMember(peer);
        if (found.arena == null)
        {
            return false;
        }

        Member member = found.member!;
        return !IsPendingRespawn(found.arena, member) || peer.ControlledAgent != null
            || !PrepareRespawnFrame(found.arena, member);
    }

    public MatrixFrame? GetRoundRespawnFrame(MissionPeer peer)
    {
        var found = FindMember(peer);
        return found.arena != null && IsPendingRespawn(found.arena, found.member!) ? found.member!.RespawnFrame : null;
    }

    public void ConfigureRoundRespawn(MissionPeer peer, AgentBuildData buildData)
    {
        var found = FindMember(peer);
        if (found.arena == null || !IsPendingRespawn(found.arena, found.member!))
        {
            return;
        }

        Member member = found.member!;
        buildData.Character(member.Character.AgentCharacter)
            .TroopOrigin(new BasicBattleAgentOrigin(member.Character.AgentCharacter))
            .Equipment(member.Equipment.Clone())
            .Banner(member.Banner == null ? null : new Banner(member.Banner))
            .Team(member.SideA ? found.arena.TeamA! : found.arena.TeamB!)
            .BodyProperties(member.Character.AgentBodyProperties)
            .IsFemale(member.IsFemale)
            .FixedEquipment(true)
            .NoHorses(member.Equipment[EquipmentIndex.Horse].IsEmpty);
        if (!string.IsNullOrEmpty(member.MountKey))
        {
            buildData.MountKey(member.MountKey);
        }

        member.RespawnConfigured = true;
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        MissionPeer? peer = agent.MissionPeer;
        var found = peer == null ? (arena: (Arena?)null, member: (Member?)null) : FindMember(peer);
        if (found.arena != null && found.member!.AwaitingRespawn && found.member.RespawnConfigured
            && !found.member.Withdrawn && found.arena.Readiness.Phase == AdimiToolsTeamBattlePhase.RoundWaiting
            && IsRespawnActor(found.arena, found.member, agent))
        {
            found.member.Agent = agent;
            RegisterMount(found.member);
        }
    }

    // The native SpawnEnd/perk pipeline must finish before this final restore.
    public void CompleteRoundRespawn(MissionPeer peer, Agent agent)
    {
        var found = FindMember(peer);
        if (found.arena == null || !found.member!.AwaitingRespawn || !found.member.RespawnConfigured
            || found.member.Withdrawn || found.arena.Readiness.Phase != AdimiToolsTeamBattlePhase.RoundWaiting
            || !IsLiving(agent) || !IsRespawnActor(found.arena, found.member, agent))
        {
            return;
        }

        found.member.Agent = agent;
        found.member.SpawnFinished = true;
        RegisterMount(found.member);
        TryFinishRoundRespawn(found.arena, found.member, Mission.CurrentTime);
    }

    public bool IsPositionInArena(Vec3 position) => !_stopped && _arenas.Any(arena => arena.Area.IsPointIn(position));

    public bool IsPositionInActiveArena(Vec3 position) => !_stopped && _arenas.Any(arena => arena.Readiness.Phase >= AdimiToolsTeamBattlePhase.Preparing && arena.Area.IsPointIn(position));

    public bool IsFightingIn(VolumeBox area)
        => !_stopped && _arenas.Any(arena => ReferenceEquals(arena.Area, area)
            && arena.Readiness.Phase is AdimiToolsTeamBattlePhase.Running or AdimiToolsTeamBattlePhase.RoundWaiting);

    public bool ShouldBlockDamage(Agent? attacker, Agent? victim)
    {
        if (_stopped || attacker == null || victim == null || ReferenceEquals(attacker, victim))
        {
            return false;
        }

        var a = FindMember(attacker);
        var b = FindMember(victim);
        if (a.arena == null && b.arena == null)
        {
            return false;
        }

        return a.arena == null || b.arena == null || !ReferenceEquals(a.arena, b.arena)
            || a.arena.Readiness.Phase != AdimiToolsTeamBattlePhase.Running
            || a.member!.Eliminated || b.member!.Eliminated
            || (a.member.SideA == b.member.SideA && !a.arena.FriendlyFireEnabled)
            || !IsLiveMember(a.arena, a.member) || !IsLiveMember(b.arena, b.member);
    }

    public override void OnMissionTick(float dt)
    {
        if (_stopped || !GameNetwork.IsServer || _duelMode == null)
        {
            return;
        }

        double now = Mission.CurrentTime;
        foreach (Arena arena in _arenas)
        {
            if (arena.DuelBoundary?.IsOwnedByOther(arena) == true)
            {
                AbortLearningRound(arena);
                ResetWaiting(arena);
                continue;
            }

            try
            {
                var frameErrors = new List<Exception>();
                if (arena.Shared != null)
                {
                    if (arena.Shared.Owns(arena))
                    {
                        Attempt(() => arena.Shared.Tick(arena, now), frameErrors);
                    }
                }
                else
                {
                    foreach (var barrier in arena.Barriers)
                    {
                        Attempt(() => barrier.Tick(now), frameErrors);
                    }
                }

                if (frameErrors.Count != 0)
                {
                    throw new AggregateException(frameErrors);
                }

                if (arena.Readiness.Phase == AdimiToolsTeamBattlePhase.Ending)
                {
                    FinishCleanup(arena, now);
                }
                else if (arena.Readiness.Phase == AdimiToolsTeamBattlePhase.Running)
                {
                    TickRunning(arena, now);
                }
                else if (arena.Readiness.Phase == AdimiToolsTeamBattlePhase.RoundWaiting)
                {
                    TickRoundWaiting(arena, now);
                }
                else if (arena.Readiness.Phase == AdimiToolsTeamBattlePhase.Preparing)
                {
                    if (arena.Members.Values.Any(member => !IsLiveMember(arena, member) || !arena.Area.IsPointIn(Ground(member.Agent))))
                    {
                        End(arena, AdimiToolsMessageLanguage.Select("有队员在开局准备时离开或状态变化，本场已取消。",
                            "The match was canceled because a player left or became unavailable during preparation."), now, immediately: true);
                    }
                    else if (!ClearBystanders(arena))
                    {
                        End(arena, AdimiToolsMessageLanguage.Select("场地被其他对局占用或缺少区域外出生点，本场已取消。",
                            "The match was canceled: the arena is occupied or an outside spawn point is missing."), now, immediately: true);
                    }
                    else if (BoundaryAtTarget(arena, raised: true))
                    {
                        arena.Readiness.Start();
                        SetEnemies(arena, true);
                        BeginLearningRound(arena);
                        string tk = arena.FriendlyFireEnabled
                            ? AdimiToolsMessageLanguage.Select("\n本场允许队友伤害。", "\nFriendly fire is enabled.") : string.Empty;
                        string startNotice = AdimiToolsMessageLanguage.IsEnglish
                            ? $"Team battle begins: Team A {arena.Members.Values.Count(m => m.SideA)}, Team B {arena.Members.Values.Count(m => !m.SideA)}; {AdimiToolsDuelModes.GetModeName(arena.Series!.TargetWins)}.{tk}"
                            : $"组队对战开始：A 队 {arena.Members.Values.Count(m => m.SideA)} 人，B 队 {arena.Members.Values.Count(m => !m.SideA)} 人；{AdimiToolsDuelModes.GetModeName(arena.Series!.TargetWins)}。{tk}";
                        Announce(arena, startNotice);
                    }
                }
                else if (!arena.Disabled)
                {
                    TickWaiting(arena, now);
                }
            }
            catch (Exception exception)
            {
                AbortLearningRound(arena);
                arena.Disabled = true;
                if (now >= arena.NextErrorLogAt)
                {
                    AdimiToolsConsoleLog.Log($"Team battle group {arena.Id} recovery pending: {exception}");
                    arena.NextErrorLogAt = now + 2d;
                }

                // Keep the reservation until cleanup succeeds. Do not let one
                // failed native frame/team operation interrupt other arenas.
                if (arena.Shared != null && !arena.Shared.Owns(arena))
                {
                    ResetWaiting(arena);
                    continue;
                }

                if (arena.Readiness.Phase != AdimiToolsTeamBattlePhase.Ending)
                {
                    arena.NormalCompletion = false;
                    arena.Readiness.End();
                    arena.EndedAt = now - 3d;
                }

                var recoveryErrors = new List<Exception>();
                Attempt(() => SetEnemies(arena, false), recoveryErrors);
                foreach (Member member in arena.Members.Values)
                {
                    Attempt(() => RestoreTeam(arena, member, touchAgents: true), recoveryErrors);
                }

                if (arena.Shared != null)
                {
                    Attempt(() =>
                    {
                        arena.Shared.Move(arena, raised: false, immediately: true);
                        arena.SharedLoweringStarted = true;
                    }, recoveryErrors);
                }
                else
                {
                    foreach (var barrier in arena.Barriers)
                    {
                        Attempt(() =>
                        {
                            barrier.Move(false, now, immediately: true);
                            arena.LoweringStarted.Add(barrier);
                        }, recoveryErrors);
                    }
                }

                Attempt(() => FinishCleanup(arena, now), recoveryErrors);
            }
        }
    }

    public override void OnClearScene()
    {
        Stop(sceneClearing: true);
        base.OnClearScene();
    }

    public override void OnRemoveBehavior()
    {
        // MissionState finalization removes behaviors after native teams and
        // the mission have been destroyed. Only discard managed state then;
        // live, explicit behavior removal must still restore the arena.
        if (Mission.IsFinalized)
        {
            _stopped = true;
            _arenas.Clear();
            _duelMode = null;
            base.OnRemoveBehavior();
            return;
        }

        Stop();
        base.OnRemoveBehavior();
    }

    internal Team? GetMarkerTeam(MissionPeer peer, bool combatOnly = false)
    {
        var (arena, member) = FindMember(peer);
        if (arena == null || member == null || member.Withdrawn || member.Eliminated
            || arena.Readiness.Phase is not (AdimiToolsTeamBattlePhase.Preparing
                or AdimiToolsTeamBattlePhase.Running or AdimiToolsTeamBattlePhase.RoundWaiting)
            || (combatOnly && arena.Readiness.Phase != AdimiToolsTeamBattlePhase.Running)
            || !IsLiveMember(arena, member))
        {
            return null;
        }

        return member.SideA ? arena.TeamA : arena.TeamB;
    }

    internal IEnumerable<(Agent agent, bool hasBox)> GetOrbOpponents(MissionPeer peer)
    {
        var (arena, member) = FindMember(peer);
        if (_stopped || arena == null || member == null || member.Withdrawn || member.Eliminated
            || arena.Readiness.Phase != AdimiToolsTeamBattlePhase.Running || !IsLiveMember(arena, member))
        {
            yield break;
        }

        foreach (Member opponent in arena.Members.Values)
        {
            if (opponent.SideA != member.SideA && !opponent.Withdrawn && !opponent.Eliminated && IsLiveMember(arena, opponent))
            {
                yield return (opponent.Agent, true);
            }
        }
    }

    internal void OnFriendlyFireToggleHit(VolumeBox area, Agent attacker)
    {
        if (!GameNetwork.IsServer || _duelMode == null || !attacker.IsHuman || !IsLiving(attacker)
            || attacker.MissionPeer is not MissionPeer peer || !ReferenceEquals(peer.ControlledAgent, attacker)
            || peer.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true })
        {
            return;
        }

        Arena? arena = _arenas.FirstOrDefault(candidate => ReferenceEquals(candidate.Area, area));
        if (arena != null && Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.TryToggleCoopFriendlyFire(area, peer, attacker) == true)
        {
            return;
        }

        if (arena == null || arena.Disabled || IsBusyElsewhere(peer, arena)
            || arena.DuelBoundary?.IsOwnedByOther(arena) == true || Mission.CurrentTime < arena.NextFriendlyFireToggleAt)
        {
            return;
        }

        bool inCombat = arena.Readiness.Phase == AdimiToolsTeamBattlePhase.Running;
        if (inCombat && !CanUseLiveSelector(arena, peer, attacker))
        {
            return;
        }

        arena.NextFriendlyFireToggleAt = Mission.CurrentTime + 0.5d;
        if (!inCombat && arena.Readiness.Phase >= AdimiToolsTeamBattlePhase.Preparing)
        {
            Notify(peer, AdimiToolsDuelGuidanceText.GetFriendlyFireNotice(AdimiToolsMessageLanguage.Select(
                "本场名单已锁定，无法切换队友伤害。", "Friendly fire cannot be changed after the roster is locked.")), ordinaryChat: true);
            return;
        }

        bool enabled = !arena.FriendlyFireEnabled;
        if (inCombat)
        {
            var failures = new List<Exception>();
            SetTeamSelfHostility(arena.TeamA!, arena.TeamB!, enabled, failures);
            if (failures.Count != 0)
            {
                SetTeamSelfHostility(arena.TeamA!, arena.TeamB!, arena.FriendlyFireEnabled, failures);
                AdimiToolsConsoleLog.Log($"Team TK switch failed: {new AggregateException(failures).Message}");
                Notify(peer, AdimiToolsDuelGuidanceText.GetFriendlyFireNotice(AdimiToolsMessageLanguage.Select(
                    "队友 TK 切换失败。", "Could not change friendly fire.")), ordinaryChat: true);
                return;
            }
        }

        arena.FriendlyFireEnabled = enabled;
        string notice = AdimiToolsDuelGuidanceText.GetFriendlyFireNotice(enabled
            ? AdimiToolsMessageLanguage.Select("此对局队友 TK 已开启。", "Friendly fire is enabled for this match.")
            : AdimiToolsMessageLanguage.Select("此对局队友 TK 已关闭。", "Friendly fire is disabled for this match."));
        foreach (MissionPeer recipient in GetSelectorRecipients(arena, peer, inCombat))
        {
            Notify(recipient, notice, ordinaryChat: true);
        }
    }

    internal bool TryHandleModeSelectorHit(Vec3 position, Agent? attacker, bool meleeInRange)
    {
        var matches = _arenas.Where(arena => arena.Area.IsPointIn(position)).Take(2).ToArray();
        if (matches.Length == 0)
        {
            return false;
        }

        Arena arena = matches[0];
        if (matches.Length != 1 || !meleeInRange || !GameNetwork.IsServer || _duelMode == null
            || arena.Disabled || !IsLiving(attacker) || !attacker!.IsHuman
            || attacker.MissionPeer is not MissionPeer peer || !ReferenceEquals(peer.ControlledAgent, attacker)
            || peer.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || !arena.Area.IsPointIn(Ground(attacker)) || IsBusyElsewhere(peer, arena)
            || arena.Shared?.IsOwnedByOther(arena) == true || arena.DuelBoundary?.IsOwnedByOther(arena) == true
            || Mission.CurrentTime < arena.NextModeToggleAt)
        {
            return true;
        }

        bool inCombat = arena.Readiness.Phase == AdimiToolsTeamBattlePhase.Running;
        if (inCombat ? !CanUseLiveSelector(arena, peer, attacker) || arena.Series == null
            : arena.Readiness.Phase >= AdimiToolsTeamBattlePhase.Preparing)
        {
            return true;
        }

        arena.NextModeToggleAt = Mission.CurrentTime + 0.5d;
        arena.TargetWins = AdimiToolsDuelModes.GetNextLimit(arena.TargetWins);
        arena.Series?.SetTargetWins(arena.TargetWins);
        string notice = AdimiToolsDuelGuidanceText.GetTeamModeSelection(AdimiToolsDuelModes.GetModeName(arena.TargetWins));
        foreach (MissionPeer recipient in GetSelectorRecipients(arena, peer, inCombat))
        {
            Notify(recipient, notice, ordinaryChat: true);
        }

        return true;
    }

    internal bool CanYieldAiReadiness(VolumeBox? area) => !_stopped && _arenas.Any(arena =>
        ReferenceEquals(arena.Area, area) && !arena.Disabled
        && arena.Readiness.Phase < AdimiToolsTeamBattlePhase.Preparing && arena.Shared?.Owns(arena) == true);

    internal bool IsPeerReservedForAi(MissionPeer peer, VolumeBox? area) => !_stopped && _arenas.Any(arena =>
        IsReserved(arena, peer) && (!ReferenceEquals(arena.Area, area)
            || arena.Disabled || arena.Readiness.Phase >= AdimiToolsTeamBattlePhase.Preparing));

    internal void YieldAiReadiness(VolumeBox area)
    {
        Arena? arena = _arenas.FirstOrDefault(candidate => ReferenceEquals(candidate.Area, area));
        if (arena != null && CanYieldAiReadiness(area))
        {
            ResetWaiting(arena);
            arena.Shared!.Release(arena);
        }
    }

    internal IReadOnlyList<MissionPeer> FindAiParticipants(VolumeBox area, MissionPeer owner, object? aiReservation = null)
    {
        Arena? arena = _arenas.FirstOrDefault(candidate => ReferenceEquals(candidate.Area, area));
        if (_stopped || _duelMode == null || arena == null || arena.Disabled
            || arena.Readiness.Phase >= AdimiToolsTeamBattlePhase.Preparing
            || arena.DuelBoundary?.IsOwnedByOther(aiReservation ?? arena) == true)
        {
            return Array.Empty<MissionPeer>();
        }

        var participants = new List<MissionPeer>();
        foreach (NetworkCommunicator network in GameNetwork.NetworkPeers)
        {
            MissionPeer? peer = network.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            if (peer == null || ReferenceEquals(peer, owner) || !network.IsConnectionActive || !network.IsSynchronized
                || !IsLiving(agent) || !agent!.IsHuman || peer.Team?.IsAttacker != true || !peer.TeamInitialPerkInfoReady
                || !ReferenceEquals(agent.MissionPeer, peer) || !ReferenceEquals(agent.Team, peer.Team)
                || IsBusyElsewhere(peer, arena))
            {
                continue;
            }

            Vec3 point = Ground(agent);
            if (area.IsPointIn(point) && (arena.SideA.IsPointIn(point) || arena.SideB.IsPointIn(point)))
            {
                participants.Add(peer);
            }
        }

        return participants;
    }

    private static bool IsReserved(Arena arena, MissionPeer peer)
        => arena.Members.ContainsKey(peer) || arena.Candidates.Contains(peer);

    private static void SetEnemies(Arena arena, bool active)
    {
        if (arena.TeamA != null && arena.TeamB != null)
        {
            var failures = new List<Exception>();
            Attempt(() => arena.TeamA.SetIsEnemyOf(arena.TeamB, active), failures);
            Attempt(() => arena.TeamB.SetIsEnemyOf(arena.TeamA, active), failures);
            if (arena.FriendlyFireEnabled)
            {
                SetTeamSelfHostility(arena.TeamA, arena.TeamB, active, failures);
            }

            if (failures.Count != 0)
            {
                throw new AggregateException(failures);
            }
        }
    }

    private static void SetTeamSelfHostility(Team sideA, Team sideB, bool active, List<Exception> failures)
    {
        // Stock Duel uses self-hostility to allow hits within one team.
        Attempt(() => sideA.SetIsEnemyOf(sideA, active), failures);
        Attempt(() => sideB.SetIsEnemyOf(sideB, active), failures);
    }

    private static void Notify(MissionPeer peer, string message, bool playSound = false, bool ordinaryChat = false)
    {
        var network = peer.GetNetworkPeer();
        if (network?.IsConnectionActive == true)
        {
            try
            {
                if (ordinaryChat)
                {
                    AdimiToolsNotifier.ServerSendGuidanceToPlayer(network, message, playSound);
                }
                else
                {
                    AdimiToolsNotifier.ServerSendAiStatusToPlayer(network, message, playSound);
                }
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Team battle notification: {exception.Message}");
            }
        }
    }

    private static void Announce(Arena arena, string message, bool playSound = false)
    {
        foreach (Member member in arena.Members.Values)
        {
            Notify(member.Peer, message, playSound);
        }
    }

    private static void Attempt(Action action, List<Exception> failures)
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

    private static void ArmRoundRespawn(Arena arena, Member member)
    {
        member.AwaitingRespawn = true;
        member.RespawnConfigured = false;
        member.SpawnFinished = false;
        member.RespawnHealthRestored = false;
        member.RespawnEquipmentRestored = false;
        member.RoundRefilled = false;
        member.RespawnFrame = null;
        member.NextRestoreAt = 0d;
        arena.NextRoundAt = null;
    }

    private static bool IsRespawnActor(Arena arena, Member member, Agent agent) => agent.IsHuman
        && ReferenceEquals(member.Peer.ControlledAgent, agent) && ReferenceEquals(agent.MissionPeer, member.Peer)
        && ReferenceEquals(agent.Team, member.SideA ? arena.TeamA : arena.TeamB)
        && ReferenceEquals(agent.Character, member.Character.AgentCharacter);

    private static void PrepareMemberState(Member member)
    {
        member.Agent.HealthLimit = member.HealthLimit;
        member.Agent.BaseHealthLimit = member.BaseHealthLimit;
        member.Agent.AgentDrivenProperties.ArmorHead = member.ArmorHead;
        member.Agent.AgentDrivenProperties.ArmorTorso = member.ArmorTorso;
        member.Agent.AgentDrivenProperties.ArmorArms = member.ArmorArms;
        member.Agent.AgentDrivenProperties.ArmorLegs = member.ArmorLegs;
        member.Agent.UpdateCustomDrivenProperties();
        AdimiToolsMissionMultiplayerDuel.RestoreAgentForAiChallenge(member.Agent);
        member.RoundRefilled = true;
    }

    private static bool HasOneTeamRole(GameEntity entity, string[] roles, bool sharedArea = false, bool sharedMover = false)
        => roles.Count(entity.HasTag) == 1 && !entity.Tags.Any(tag => tag.StartsWith("adimi_", StringComparison.Ordinal)
            && !tag.StartsWith("adimi_team_", StringComparison.Ordinal)
            && !((sharedArea || sharedMover) && AdimiToolsAiArenaTags.TryParseGroupTag(tag, out _))
            && !(sharedArea && tag == AdimiToolsAiArenaTags.AreaRoleTag)
            && !(sharedMover && (tag == AdimiToolsAiArenaTags.BarrierRoleTag || tag == AdimiToolsAiArenaTags.LadderRoleTag)));

    private static bool BoundaryAtTarget(Arena arena, bool raised)
        => arena.Shared != null ? arena.Shared.IsAtTarget(arena, raised)
            : arena.Barriers.All(barrier => !barrier.IsMoving && barrier.IsRaised == raised);

    private static void ResetWaiting(Arena arena)
    {
        if (arena.Readiness.Phase >= AdimiToolsTeamBattlePhase.Preparing)
        {
            return;
        }

        arena.Readiness.Reset();
        arena.NormalCompletion = false;
        arena.Candidates.Clear();
        arena.LastNotice = string.Empty;
    }

    private static bool HasMovingAncestor(GameEntity entity)
    {
        for (GameEntity? parent = entity.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.HasTag(AdimiToolsTeamBattlePolicy.Barrier) || parent.HasTag(AdimiToolsAiArenaTags.BarrierRoleTag)
                || parent.HasTag(AdimiToolsAiArenaTags.LadderRoleTag))
            {
                return true;
            }
        }

        return false;
    }

    private static GameEntity GetTeamBarrierRoot(GameEntity entity)
    {
        GameEntity root = entity;
        for (GameEntity? parent = entity.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.HasTag(AdimiToolsTeamBattlePolicy.Barrier))
            {
                root = parent;
            }
        }

        return root;
    }

    private static void ValidateBoxFrame(GameEntity entity)
    {
        MatrixFrame frame = entity.GetGlobalFrame();
        Vec3 scale = frame.rotation.GetScaleVector();
        double determinant = frame.rotation.s.x * (frame.rotation.f.y * frame.rotation.u.z - frame.rotation.f.z * frame.rotation.u.y)
            - frame.rotation.s.y * (frame.rotation.f.x * frame.rotation.u.z - frame.rotation.f.z * frame.rotation.u.x)
            + frame.rotation.s.z * (frame.rotation.f.x * frame.rotation.u.y - frame.rotation.f.y * frame.rotation.u.x);
        if (!frame.origin.IsValid || !scale.IsValid || scale.x <= 0.01f || scale.y <= 0.01f || scale.z <= 0.01f
            || !double.IsFinite(determinant) || determinant <= 0d)
        {
            throw new InvalidOperationException("VolumeBox world frame and scale must be finite, positive and non-degenerate.");
        }
    }

    private static SpatialArea[] FindSpatialOwners(List<SpatialArea> areas, HashSet<GameEntity> invalid, Vec3[] points, bool includeRaised)
    {
        var owners = new List<SpatialArea>();
        foreach (SpatialArea area in areas)
        {
            try
            {
                if (points.Any(point => area.Box.IsPointIn(point)
                    || (includeRaised && area.Box.IsPointIn(point + new Vec3(0f, 0f, 3f)))))
                {
                    owners.Add(area);
                }
            }
            catch (Exception exception)
            {
                invalid.Add(area.Entity);
                AdimiToolsConsoleLog.Log($"Team battle area {area.Id} containment failed: {exception.Message}");
            }
        }

        return owners.ToArray();
    }

    private bool CanUseLiveSelector(Arena arena, MissionPeer peer, Agent attacker)
        => arena.Members.TryGetValue(peer, out Member? member) && !member.Withdrawn && !member.Eliminated
            && IsLiveMember(arena, member) && arena.Area.IsPointIn(Ground(attacker));

    private IEnumerable<MissionPeer> GetSelectorRecipients(Arena arena, MissionPeer peer, bool inCombat)
    {
        if (inCombat)
        {
            return arena.Members.Values.Where(participant => !participant.Withdrawn).Select(participant => participant.Peer);
        }

        var (sideA, sideB) = CollectReadyPlayers(arena);
        return sideA.Concat(sideB).Select(agent => agent.MissionPeer!).Append(peer).Distinct();
    }

    private (List<Agent> sideA, List<Agent> sideB) CollectReadyPlayers(Arena arena)
    {
        var a = new List<Agent>();
        var b = new List<Agent>();
        foreach (NetworkCommunicator network in GameNetwork.NetworkPeers)
        {
            MissionPeer? peer = network.GetComponent<MissionPeer>();
            Agent? agent = peer?.ControlledAgent;
            if (peer == null || !network.IsConnectionActive || !network.IsSynchronized || !IsLiving(agent)
                || !agent!.IsHuman || peer.Team?.IsAttacker != true || !peer.TeamInitialPerkInfoReady
                || !ReferenceEquals(agent.MissionPeer, peer) || IsBusyElsewhere(peer, arena) || !arena.Area.IsPointIn(Ground(agent)))
            {
                continue;
            }

            Vec3 point = Ground(agent);
            bool inA = arena.SideA.IsPointIn(point);
            bool inB = arena.SideB.IsPointIn(point);
            if (inA == inB || _arenas.Any(other => other != arena
                && (other.SideA.IsPointIn(point) || other.SideB.IsPointIn(point))))
            {
                continue;
            }

            (inA ? a : b).Add(agent);
        }

        return (a, b);
    }

    private void TickWaiting(Arena arena, double now)
    {
        var (a, b) = CollectReadyPlayers(arena);
        if (arena.Shared != null)
        {
            if (a.Count != 0 && b.Count != 0)
            {
                if (!arena.Shared.TryAcquire(arena))
                {
                    ResetWaiting(arena);
                    return;
                }
            }
            else
            {
                arena.Shared.Release(arena);
            }
        }

        double? previousCountdownStart = arena.Readiness.StableSince;
        bool ready = arena.Readiness.Observe(a, b, now);
        bool countdownRestarted = arena.Readiness.Phase == AdimiToolsTeamBattlePhase.CountingDown
            && previousCountdownStart != arena.Readiness.StableSince;
        arena.Candidates.Clear();
        if (a.Count != 0 && b.Count != 0)
        {
            foreach (Agent agent in a.Concat(b))
            {
                arena.Candidates.Add(agent.MissionPeer!);
            }
        }

        foreach (Agent agent in a.Concat(b))
        {
            if (a.Count != 0 && b.Count != 0)
            {
                _duelMode!.CancelIncomingDuelRequestsForAiChallenge(agent.MissionPeer!);
            }
        }

        bool isCountdown = arena.Readiness.Phase == AdimiToolsTeamBattlePhase.CountingDown;
        string notice = arena.Readiness.Phase switch
        {
            AdimiToolsTeamBattlePhase.CountingDown => AdimiToolsDuelGuidanceText.GetTeamCountdown(
                a.Count, b.Count, Math.Max(0, (int)Math.Ceiling(arena.Readiness.CountdownEndsAt!.Value - now))),
            _ => AdimiToolsMessageLanguage.Select("等待双方进入准备区，每队至少 1 人，人数无需相等。",
                "Waiting for both teams. Each needs at least 1 player; team sizes may differ."),
        };
        if (notice != arena.LastNotice || countdownRestarted)
        {
            bool playCountdownSound = !ready && arena.Readiness.Phase == AdimiToolsTeamBattlePhase.CountingDown;
            foreach (Agent agent in a.Concat(b))
            {
                Notify(agent.MissionPeer!, notice, playCountdownSound, ordinaryChat: isCountdown);
            }

            arena.LastNotice = notice;
        }

        if (!ready)
        {
            return;
        }

        arena.NormalCompletion = false;
        arena.Readiness.Lock();
        foreach (Agent agent in a.Concat(b))
        {
            var peer = agent.MissionPeer!;
            arena.Members.Add(peer, new Member
            {
                Peer = peer,
                Agent = agent,
                OriginalTeam = peer.Team!,
                SideA = a.Contains(agent),
                Mount = IsLiving(agent.MountAgent) ? agent.MountAgent : null,
                MountTeam = IsLiving(agent.MountAgent) ? agent.MountAgent!.Team : null,
                Character = new AgentBuildData(agent.Character).BodyProperties(agent.BodyPropertiesValue)
                    .IsFemale(agent.IsFemale).Monster(agent.Monster),
                Equipment = SnapshotEquipment(agent),
                Banner = AdimiToolsDuelAppearance.CreateBanner(peer.Culture),
                MountKey = IsLiving(agent.MountAgent) ? agent.MountAgent!.HorseCreationKey : null,
                IsFemale = agent.IsFemale,
                InitialFrame = InitialFrame(agent),
                HealthLimit = agent.HealthLimit,
                BaseHealthLimit = agent.BaseHealthLimit,
                ArmorHead = agent.AgentDrivenProperties.ArmorHead,
                ArmorTorso = agent.AgentDrivenProperties.ArmorTorso,
                ArmorArms = agent.AgentDrivenProperties.ArmorArms,
                ArmorLegs = agent.AgentDrivenProperties.ArmorLegs,
            });
        }

        arena.Series = new AdimiToolsTeamBattleSeries(arena.TargetWins);
        string rosterMessage = AdimiToolsMessageLanguage.IsEnglish
            ? $"Roster locked. Match format: {AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)}."
            : $"名单已锁定，本场赛制为{AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)}。";
        Announce(arena, rosterMessage);

        if (!ClearBystanders(arena) || !AdimiToolsAiArenaCleanup.TryClear(Mission, arena.Area))
        {
            End(arena, AdimiToolsMessageLanguage.Select("场地尚未清理完毕或存在其他对局，本场已取消。",
                "The match was canceled because the arena is not clear or is occupied."), now, immediately: true);
            return;
        }

        EnsureTeams(arena);
        foreach (Member member in arena.Members.Values)
        {
            // A previous completed individual duel can leave a one-use weapon
            // restore on a living winner. This new match must not carry it to
            // the next ordinary life after a team-battle death.
            if (AdimiToolsMissionMultiplayerDuel.PlayersDuelConfig.TryGetValue(member.Peer.Peer.Id, out var config))
            {
                config.RespawnEquip = null;
                config.NoWeaponTreshold = 0;
            }

            AdimiToolsMissionMultiplayerDuel.PlayerDuelData.PlayerDuelList.Remove(member.Peer.Peer.Id);
            Team team = member.SideA ? arena.TeamA! : arena.TeamB!;
            member.Peer.Team = team;
            member.Agent.SetTeam(team, sync: true);
            member.Mount?.SetTeam(team, sync: true);
            RegisterMount(member);
            PrepareMemberState(member);
        }

        if (arena.Shared != null)
        {
            arena.Shared.Move(arena, raised: true);
        }
        else
        {
            foreach (var barrier in arena.Barriers)
            {
                barrier.Move(true, now);
            }
        }

        Announce(arena, AdimiToolsMessageLanguage.Select("名单已锁定，场地清理完成，屏障正在升起；屏障就位后开战。",
            "Roster locked and arena cleared. The battle begins when the barriers are raised."));
    }

    private void TickRunning(Arena arena, double now)
    {
        foreach (Member member in arena.Members.Values.Where(member => !member.Withdrawn && !member.Eliminated && IsLiving(member.Agent)))
        {
            RegisterMount(member);
            if (IsLiving(member.Agent.MountAgent) && member.Agent.MountAgent!.Team != member.Agent.Team)
            {
                member.Agent.MountAgent.SetTeam(member.Agent.Team, sync: true);
            }
        }

        RefreshParticipants(arena, now);
        if (EndIfSideWithdrawn(arena, now))
        {
            return;
        }

        int a = arena.Members.Values.Count(member => member.SideA && !member.Withdrawn && !member.Eliminated);
        int b = arena.Members.Values.Count(member => !member.SideA && !member.Withdrawn && !member.Eliminated);
        if (a == 0 || b == 0)
        {
            ResolveRound(arena, a == 0 && b == 0 ? null : a != 0, now);
        }

        // Clear the arena during preparation for each round. Once fighting,
        // keep spectators in place; barriers and damage isolation protect it.
    }

    private void ResolveRound(Arena arena, bool? sideAWon, double now)
    {
        if (arena.Readiness.Phase != AdimiToolsTeamBattlePhase.Running || arena.Series == null)
        {
            return;
        }

        try
        {
            Mission.GetMissionBehavior<AdimiToolsPvpLearningBehavior>()?.Finish(arena, sideAWon.HasValue ? (sideAWon.Value ? 0 : 1) : null,
            interrupted: !sideAWon.HasValue || arena.Members.Values.Any(member => member.Withdrawn));
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"Team PvP learning end skipped: {exception.Message}");
        }

        bool complete = arena.Series.RecordRound(sideAWon);
        if (complete)
        {
            string endMessage = AdimiToolsMessageLanguage.IsEnglish
                ? $"Team battle ended: Team {(arena.Series.WinnerSideA == true ? "A" : "B")} wins. Score A:B {arena.Series.SideAWins}:{arena.Series.SideBWins} ({AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)})."
                : $"组队对战结束：{(arena.Series.WinnerSideA == true ? "A" : "B")} 队获胜，比分 {arena.Series.SideAWins}:{arena.Series.SideBWins}（{AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)}）。";
            End(arena, endMessage, now, normalCompletion: true);
            return;
        }

        arena.Readiness.WaitForNextRound();
        SetEnemies(arena, false);
        arena.NextRoundAt = null;
        arena.RespawnDeadline = now + 30d;
        foreach (Member member in arena.Members.Values.Where(member => !member.Withdrawn))
        {
            member.RoundRefilled = false;
            if (!IsLiving(member.Agent))
            {
                ArmRoundRespawn(arena, member);
            }
        }

        arena.RoundAreaClean = AdimiToolsAiArenaCleanup.TryClear(Mission, arena.Area);
        arena.NextRoundCleanupAt = now + 0.25d;
        string result = !sideAWon.HasValue
            ? AdimiToolsMessageLanguage.Select("本局双方同时全灭，平局不加分，重新对战。", "Both teams were eliminated. The round is replayed without a point. ")
            : AdimiToolsMessageLanguage.IsEnglish ? $"Team {(sideAWon.Value ? "A" : "B")} wins the round. " : $"本局 {(sideAWon.Value ? "A" : "B")} 队获胜。";
        string scoreMessage = AdimiToolsMessageLanguage.IsEnglish
            ? $"{result}Score: {arena.Series.SideAWins}:{arena.Series.SideBWins} ({AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)}). The next round starts 3 seconds after respawns are ready."
            : $"{result}当前比分 {arena.Series.SideAWins}:{arena.Series.SideBWins}（{AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)}），阵亡者复活后准备 3 秒开始下一局。";
        Announce(arena, scoreMessage);
        TickRoundWaiting(arena, now);
    }

    private void TickRoundWaiting(Arena arena, double now)
    {
        RefreshParticipants(arena, now);
        if (EndIfSideWithdrawn(arena, now))
        {
            return;
        }

        if (!ClearBystanders(arena))
        {
            End(arena, AdimiToolsMessageLanguage.Select("局间场地被其他对局占用或无法清场，本场已取消。",
                "The match was canceled because the arena became occupied or could not be cleared."), now, immediately: true);
            return;
        }

        foreach (Member member in arena.Members.Values.Where(member => !member.Withdrawn))
        {
            if (member.SpawnFinished && member.AwaitingRespawn)
            {
                TryFinishRoundRespawn(arena, member, now);
            }

            if (!member.AwaitingRespawn && !member.Eliminated && !member.RoundRefilled && IsLiveMember(arena, member))
            {
                PrepareMemberState(member);
            }
        }

        if (now >= arena.NextRoundCleanupAt || (arena.NextRoundAt.HasValue && now >= arena.NextRoundAt.Value))
        {
            arena.RoundAreaClean = AdimiToolsAiArenaCleanup.TryClear(Mission, arena.Area);
            arena.NextRoundCleanupAt = now + 0.25d;
        }

        bool prepared = arena.RoundAreaClean && arena.Members.Values.Where(member => !member.Withdrawn)
            .All(member => !member.Eliminated && !member.AwaitingRespawn && member.RoundRefilled && IsLiveMember(arena, member));
        if (!prepared)
        {
            arena.NextRoundAt = null;
            if (now >= arena.RespawnDeadline)
            {
                End(arena, AdimiToolsMessageLanguage.Select("等待组队队员复活、装备恢复或区域清理超时，本场已取消。",
                    "The match was canceled: waiting for respawns, equipment, or arena cleanup timed out."), now, immediately: true);
            }

            return;
        }

        if (!arena.NextRoundAt.HasValue)
        {
            arena.NextRoundAt = now + 3d;
            string readyMessage = AdimiToolsMessageLanguage.IsEnglish
                ? $"Score: {arena.Series!.SideAWins}:{arena.Series.SideBWins}. Both teams are ready; the next round starts in 3 seconds."
                : $"当前比分 {arena.Series!.SideAWins}:{arena.Series.SideBWins}，双方准备完成，下一局 3 秒后开始。";
            Announce(arena, readyMessage, playSound: true);
            return;
        }

        if (now >= arena.NextRoundAt.Value)
        {
            arena.Readiness.StartNextRound();
            arena.NextRoundAt = null;
            SetEnemies(arena, true);
            BeginLearningRound(arena);
            string startMessage = AdimiToolsMessageLanguage.IsEnglish
                ? $"Team battle round {arena.Series!.RoundsPlayed + 1} begins. Score: {arena.Series.SideAWins}:{arena.Series.SideBWins} ({AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)})."
                : $"组队对战第 {arena.Series!.RoundsPlayed + 1} 局开始，当前比分 {arena.Series.SideAWins}:{arena.Series.SideBWins}（{AdimiToolsDuelModes.GetModeName(arena.Series.TargetWins)}）。";
            Announce(arena, startMessage);
        }
    }

    private void RefreshParticipants(Arena arena, double now)
    {
        foreach (Member member in arena.Members.Values.Where(member => !member.Withdrawn))
        {
            if (!IsConnectedMember(arena, member))
            {
                Withdraw(arena, member);
                continue;
            }

            if (!IsLiving(member.Agent))
            {
                // Agent.OnDelete may already have cleared MissionPeer and the
                // native pointer. The lock-time peer/snapshots own the next life.
                if (IsLiving(member.Peer.ControlledAgent) && !ReferenceEquals(member.Peer.ControlledAgent, member.Agent))
                {
                    Withdraw(arena, member);
                    continue;
                }

                member.Eliminated = true;
                if (arena.Readiness.Phase == AdimiToolsTeamBattlePhase.RoundWaiting && (!member.AwaitingRespawn || member.SpawnFinished))
                {
                    ArmRoundRespawn(arena, member);
                }

                continue;
            }

            if (!IsLiveMember(arena, member) || !arena.Area.IsPointIn(Ground(member.Agent)))
            {
                Withdraw(arena, member);
            }
        }
    }

    private void Withdraw(Arena arena, Member member)
    {
        member.Withdrawn = true;
        member.Eliminated = true;
        member.AwaitingRespawn = false;
        member.RespawnFrame = null;
        arena.NextRoundAt = null;
        RestoreTeam(arena, member, touchAgents: true);
        Notify(member.Peer, AdimiToolsMessageLanguage.Select("你已断线、换队或离开战斗区，整场退出，不能在后续小局重新加入。",
            "You disconnected, changed teams, or left the arena. You cannot rejoin this match."));
    }

    private bool EndIfSideWithdrawn(Arena arena, double now)
    {
        int a = arena.Members.Values.Count(member => member.SideA && !member.Withdrawn);
        int b = arena.Members.Values.Count(member => !member.SideA && !member.Withdrawn);
        if (a != 0 && b != 0)
        {
            return false;
        }

        bool? winner = a == 0 && b == 0 ? null : a != 0;
        arena.Series?.Forfeit(winner);
        string result = !winner.HasValue
            ? AdimiToolsMessageLanguage.Select("组队对战结束：双方均已整场退出。", "Team battle ended: both teams withdrew.")
            : AdimiToolsMessageLanguage.IsEnglish
                ? $"Team battle ended: Team {(winner.Value ? "B" : "A")} has no players left; Team {(winner.Value ? "A" : "B")} wins."
                : $"组队对战结束：{(winner.Value ? "B" : "A")} 队无人可继续，{(winner.Value ? "A" : "B")} 队获胜。";
        End(arena, result, now);
        return true;
    }

    private (Arena? arena, Member? member) FindMember(MissionPeer peer)
    {
        if (!_stopped)
        {
            foreach (Arena arena in _arenas)
            {
                if (arena.Members.TryGetValue(peer, out Member? member))
                {
                    return (arena, member);
                }
            }
        }

        return (null, null);
    }

    private bool IsConnectedMember(Arena arena, Member member) => !_stopped && !member.Withdrawn
        && member.Peer.GetNetworkPeer()?.IsConnectionActive == true && member.Peer.GetNetworkPeer().IsSynchronized
        && ReferenceEquals(member.Peer.Team, member.SideA ? arena.TeamA : arena.TeamB);

    private bool IsPendingRespawn(Arena arena, Member member) => arena.Readiness.Phase == AdimiToolsTeamBattlePhase.RoundWaiting
        && arena.RoundAreaClean && member.AwaitingRespawn && !member.SpawnFinished && IsConnectedMember(arena, member);

    private void RegisterMount(Member member)
    {
        if (IsLiving(member.Agent) && IsLiving(member.Agent.MountAgent))
        {
            member.ManagedMounts.Add(member.Agent.MountAgent!);
        }
    }

    private void TryFinishRoundRespawn(Arena arena, Member member, double now)
    {
        if (!member.SpawnFinished || !member.AwaitingRespawn || member.Withdrawn || now < member.NextRestoreAt || !IsLiveMember(arena, member))
        {
            return;
        }

        try
        {
            if (!member.RespawnHealthRestored)
            {
                PrepareMemberState(member);
                member.RespawnHealthRestored = true;
            }

            if (!member.RespawnEquipmentRestored)
            {
                AdimiToolsAiArenaEquipment.Restore(member.Agent, member.Equipment, member.Banner);
                member.RespawnEquipmentRestored = true;
            }

            member.AwaitingRespawn = false;
            member.Eliminated = false;
            member.RespawnConfigured = false;
            member.RespawnFrame = null;
            arena.NextRoundAt = null;
        }
        catch (Exception exception)
        {
            member.NextRestoreAt = now + 1d;
            AdimiToolsConsoleLog.Log($"Team battle round respawn restore will retry for {member.Peer.Peer.Id}: {exception.Message}");
        }
    }

    private bool PrepareRespawnFrame(Arena arena, Member member)
    {
        float spacing = member.Equipment[EquipmentIndex.Horse].IsEmpty ? 1.5f : 3f;
        var candidates = new List<Vec3> { member.InitialFrame.origin };
        for (int ring = 1; ring <= 4; ring++)
        {
            for (int step = 0; step < 8; step++)
            {
                float angle = step * System.MathF.PI * 0.25f;
                candidates.Add(member.InitialFrame.origin + new Vec3(System.MathF.Cos(angle) * ring * spacing, System.MathF.Sin(angle) * ring * spacing, 0f));
            }
        }

        foreach (Vec3 point in candidates)
        {
            if (!point.IsValid)
            {
                continue;
            }

            WorldPosition navigation = new(Mission.Scene, point);
            if (navigation.GetNavMesh() == UIntPtr.Zero)
            {
                continue;
            }

            Vec3 ground = navigation.GetNavMeshVec3();
            if (!ground.IsValid || System.MathF.Abs(ground.z - member.InitialFrame.origin.z) > 2f
                || !arena.Area.IsPointIn(ground) || !(member.SideA ? arena.SideA : arena.SideB).IsPointIn(ground))
            {
                continue;
            }

            PathFaceRecord face = PathFaceRecord.NullFaceRecord;
            Mission.Scene.GetNavMeshFaceIndex(ref face, ground, checkIfDisabled: true);
            if (!face.IsValid() || Mission.AllAgents.Any(agent => IsLiving(agent) && Ground(agent).DistanceSquared(ground) < spacing * spacing)
                || arena.Members.Values.Any(other => !ReferenceEquals(other, member) && !other.Withdrawn && other.AwaitingRespawn
                    && other.RespawnFrame.HasValue && other.RespawnFrame.Value.origin.DistanceSquared(ground) < spacing * spacing))
            {
                continue;
            }

            MatrixFrame frame = member.InitialFrame;
            frame.origin = ground;
            member.RespawnFrame = frame;
            return true;
        }

        member.RespawnFrame = null;
        return false;
    }

    private MatrixFrame InitialFrame(Agent agent)
    {
        MatrixFrame frame = agent.Frame;
        frame.origin = Ground(agent);
        return frame;
    }

    private Equipment SnapshotEquipment(Agent agent)
    {
        Equipment equipment = agent.SpawnEquipment.Clone();
        for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
        {
            MissionWeapon weapon = agent.Equipment[slot];
            equipment[slot] = weapon.IsEmpty ? EquipmentElement.Invalid : new EquipmentElement(weapon.Item, weapon.ItemModifier);
        }

        equipment[EquipmentIndex.Horse] = IsLiving(agent.MountAgent) ? agent.MountAgent!.SpawnEquipment[EquipmentIndex.Horse] : EquipmentElement.Invalid;
        equipment[EquipmentIndex.HorseHarness] = IsLiving(agent.MountAgent) ? agent.MountAgent!.SpawnEquipment[EquipmentIndex.HorseHarness] : EquipmentElement.Invalid;
        return equipment;
    }

    private void End(Arena arena, string message, double now, bool immediately = false, bool normalCompletion = false)
    {
        AbortLearningRound(arena);
        arena.NormalCompletion = normalCompletion;
        arena.Readiness.End();
        arena.EndedAt = immediately ? now - 3d : now;
        Announce(arena, message);
        FinishCleanup(arena, now);
    }

    private void AbortLearningRound(Arena arena)
    {
        try
        {
            Mission.GetMissionBehavior<AdimiToolsPvpLearningBehavior>()?.Finish(arena, null, interrupted: true);
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"Team PvP learning cancel skipped: {exception.Message}");
        }
    }

    private void BeginLearningRound(Arena arena)
    {
        try
        {
            var members = arena.Members.Values.Where(member => !member.Withdrawn)
                .Select(member => new AdimiToolsPvpLearningBehavior.Member(member.Peer, member.Agent, member.SideA ? 0 : 1)).ToArray();
            Mission.GetMissionBehavior<AdimiToolsPvpLearningBehavior>()?.Begin(arena, "team_pvp", arena.Id, members);
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"Team PvP learning start skipped: {exception.Message}");
        }
    }

    private void FinishCleanup(Arena arena, double now)
    {
        var failures = new List<Exception>();
        Attempt(() => SetEnemies(arena, false), failures);
        foreach (Member member in arena.Members.Values)
        {
            Attempt(() => RestoreTeam(arena, member, touchAgents: true), failures);
            Attempt(() =>
            {
                if (!member.Refilled && IsLiving(member.Agent) && ReferenceEquals(member.Peer.ControlledAgent, member.Agent))
                {
                    AdimiToolsMissionMultiplayerDuel.RestoreAgentForAiChallenge(member.Agent);
                    member.Refilled = true;
                }
            }, failures);
        }

        if (now >= arena.EndedAt!.Value + 1d)
        {
            if (arena.Shared != null)
            {
                if (!arena.SharedLoweringStarted)
                {
                    Attempt(() =>
                    {
                        arena.Shared.Move(arena, raised: false);
                        arena.SharedLoweringStarted = true;
                    }, failures);
                }
            }
            else
            {
                foreach (var barrier in arena.Barriers.Where(barrier => !arena.LoweringStarted.Contains(barrier)))
                {
                    Attempt(() =>
                    {
                        barrier.Move(false, now);
                        arena.LoweringStarted.Add(barrier);
                    }, failures);
                }
            }
        }

        if (now >= arena.EndedAt.Value + 3d && now >= arena.NextSweepAt)
        {
            Attempt(() =>
            {
                arena.LastSweepSucceeded = AdimiToolsAiArenaCleanup.TryClear(Mission, arena.Area);
                arena.NextSweepAt = now + 0.25d;
                if (arena.LastSweepSucceeded)
                {
                    arena.SweepUntil ??= now + 1d;
                }
            }, failures);
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }

        if (!arena.SweepUntil.HasValue || now < arena.SweepUntil.Value || !arena.LastSweepSucceeded
            || (arena.Shared != null ? !arena.SharedLoweringStarted || !BoundaryAtTarget(arena, raised: false)
                : arena.LoweringStarted.Count != arena.Barriers.Count || arena.Barriers.Any(barrier => barrier.IsMoving)))
        {
            return;
        }

        QueueCompletedInfantryRespawns(arena);
        arena.NormalCompletion = false;
        arena.Members.Clear();
        arena.Candidates.Clear();
        arena.Series = null;
        arena.FriendlyFireEnabled = false;
        arena.TargetWins = 1;
        arena.NextModeToggleAt = 0d;
        arena.NextFriendlyFireToggleAt = 0d;
        arena.NextRoundAt = null;
        arena.RespawnDeadline = 0d;
        arena.NextRoundCleanupAt = 0d;
        arena.RoundAreaClean = false;
        arena.Readiness.Reset();
        arena.EndedAt = null;
        arena.LoweringStarted.Clear();
        arena.SweepUntil = null;
        arena.NextSweepAt = 0d;
        arena.LastSweepSucceeded = false;
        arena.LastNotice = string.Empty;
        arena.SharedLoweringStarted = false;
        arena.Shared?.Release(arena);
    }

    private void QueueCompletedInfantryRespawns(Arena arena)
    {
        if (!arena.NormalCompletion)
        {
            return;
        }

        var aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        foreach (Member member in arena.Members.Values)
        {
            // Only saved class/equipment data is read: the final corpse may
            // already have lost its native pointer during delayed cleanup.
            if (member.Withdrawn || IsLiving(member.Agent) || IsLiving(member.Peer.ControlledAgent)
                || member.Character.AgentCharacter.DefaultFormationClass is not (FormationClass.Infantry or FormationClass.HeavyInfantry))
            {
                continue;
            }

            NetworkCommunicator? network = member.Peer.GetNetworkPeer();
            if (network?.IsConnectionActive != true || !network.IsSynchronized
                || !ReferenceEquals(member.Peer.Team, member.OriginalTeam))
            {
                continue;
            }

            if (aiArena?.QueueCompletedInfantryRespawn(member.Peer, member.Character, member.Equipment, member.Banner,
                member.OriginalTeam, member.HealthLimit, member.BaseHealthLimit) != true)
            {
                // A disconnected/changed peer or rejected saved data must not
                // hold the arena closed. The queue preserves existing tickets.
                AdimiToolsConsoleLog.Log($"Completed team infantry respawn was not queued for {member.Peer.Peer.Id}.");
            }
        }
    }

    private bool ClearBystanders(Arena arena)
    {
        var moves = new List<(Agent agent, MatrixFrame frame)>();
        foreach (Agent agent in Mission.AllAgents.ToArray())
        {
            if (!IsLiving(agent) || !agent.IsHuman || !arena.Area.IsPointIn(Ground(agent))
                || arena.Members.Values.Any(member => !member.Withdrawn && (!member.Eliminated || member.AwaitingRespawn) && ReferenceEquals(member.Agent, agent)))
            {
                continue;
            }

            MissionPeer? peer = agent.MissionPeer;
            if (peer == null || IsBusyElsewhere(peer, arena))
            {
                return false;
            }

            bool mounted = IsLiving(agent.MountAgent);
            var spawn = Mission.Scene.FindEntitiesWithTag("spawnpoint")
                .Where(entity => !_arenas.Any(candidate => candidate.Area.IsPointIn(entity.GlobalPosition))
                    && Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.IsPositionInArena(entity.GlobalPosition) != true
                    && !(mounted && entity.HasTag("exclude_mounted")) && !(!mounted && entity.HasTag("exclude_footmen")))
                .OrderBy(entity => entity.GlobalPosition.DistanceSquared(agent.Position)).FirstOrDefault();
            if (spawn == null)
            {
                return false;
            }

            moves.Add((agent, spawn.GetGlobalFrame()));
        }

        foreach (var move in moves)
        {
            if (move.agent.IsUsingGameObject)
            {
                move.agent.StopUsingGameObject(isSuccessful: false, Agent.StopUsingGameObjectFlags.None);
            }

            if (IsLiving(move.agent.MountAgent))
            {
                move.agent.MountAgent!.TeleportToPosition(move.frame.origin);
            }

            move.agent.TeleportToPosition(move.frame.origin);
            WorldPosition position = new(Mission.Scene, move.frame.origin);
            Vec2 direction = move.frame.rotation.f.AsVec2.Normalized();
            if (direction.LengthSquared < 0.1f)
            {
                direction = new Vec2(0f, 1f);
            }

            GameNetwork.BeginBroadcastModuleEvent();
            try
            {
                GameNetwork.WriteMessage(new AgentTeleportToFrame(move.agent.Index, position.GetGroundVec3(), direction));
            }
            finally
            {
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
            }

            Notify(move.agent.MissionPeer!, AdimiToolsMessageLanguage.Select("此处组队对战正在进行，已将你移至区域外出生点。",
                "A team battle is in progress here. You were moved to a spawn point outside the arena."));
        }

        return true;
    }

    private bool IsBusyElsewhere(MissionPeer peer, Arena current)
    {
        var ai = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        return _duelMode!.IsPeerBusyForAiChallenge(peer) || ai?.IsPeerReserved(peer) == true || ai?.HasPendingAiRespawn(peer) == true
            || _arenas.Any(arena => arena != current && IsReserved(arena, peer));
    }

    private bool IsLiving(Agent? agent) => !_stopped && agent != null && Mission.AllAgents.Contains(agent)
        && agent.State != AgentState.Deleted && agent.IsActive() && agent.Health > 0f && !agent.IsFadingOut();

    private bool IsLiveMember(Arena arena, Member member) => IsLiving(member.Agent) && ReferenceEquals(member.Peer.ControlledAgent, member.Agent)
        && member.Peer.GetNetworkPeer()?.IsConnectionActive == true && member.Peer.GetNetworkPeer().IsSynchronized
        && ReferenceEquals(member.Agent.MissionPeer, member.Peer)
        && ReferenceEquals(member.Peer.Team, member.SideA ? arena.TeamA : arena.TeamB)
        && ReferenceEquals(member.Agent.Team, member.Peer.Team);

    private Vec3 Ground(Agent agent) => IsLiving(agent.MountAgent) ? agent.MountAgent!.Position : agent.Position;

    private (Arena? arena, Member? member) FindMember(Agent actor)
    {
        if (actor.IsMount)
        {
            // A horse can be taken from the other team. Its living rider owns
            // the current combat side, ahead of the original mount snapshot.
            Agent? rider = actor.RiderAgent;
            if ((IsLiving(rider) && ReferenceEquals(rider!.MountAgent, actor))
                || (AdimiToolsMountSafety.TryGetCurrentRider(Mission, actor, out rider) && rider != null))
            {
                return FindMember(rider!);
            }
        }

        foreach (Arena arena in _arenas)
        {
            foreach (Member member in arena.Members.Values)
            {
                if (ReferenceEquals(actor, member.Agent) || ReferenceEquals(actor, member.Mount) || member.ManagedMounts.Contains(actor)
                    || (IsLiving(member.Agent) && ReferenceEquals(actor, member.Agent.MountAgent)))
                {
                    return (arena, member);
                }
            }
        }

        return (null, null);
    }

    private void EnsureTeams(Arena arena)
    {
        arena.TeamA ??= Mission.Teams.Add(BattleSideEnum.Attacker, uint.MaxValue, uint.MaxValue, null, isPlayerGeneral: false, isPlayerSergeant: false, isSettingRelations: false);
        arena.TeamB ??= Mission.Teams.Add(BattleSideEnum.Attacker, uint.MaxValue, uint.MaxValue, null, isPlayerGeneral: false, isPlayerSergeant: false, isSettingRelations: false);
        foreach (Team team in Mission.Teams)
        {
            arena.TeamA.SetIsEnemyOf(team, false);
            arena.TeamB.SetIsEnemyOf(team, false);
            team.SetIsEnemyOf(arena.TeamA, false);
            team.SetIsEnemyOf(arena.TeamB, false);
        }
    }

    private void RestoreTeam(Arena arena, Member member, bool touchAgents)
    {
        bool IsBattleTeam(Team? team) => team != null && (ReferenceEquals(team, arena.TeamA) || ReferenceEquals(team, arena.TeamB));
        if (IsBattleTeam(member.Peer.Team))
        {
            member.Peer.Team = member.OriginalTeam;
        }

        if (touchAgents && IsLiving(member.Agent) && IsBattleTeam(member.Agent.Team))
        {
            member.Agent.SetTeam(member.Peer.Team ?? member.OriginalTeam, sync: true);
        }

        foreach (Agent mount in member.ManagedMounts.Concat(member.Mount == null ? Array.Empty<Agent>() : new[] { member.Mount }).Distinct())
        {
            if (touchAgents && IsLiving(mount) && IsBattleTeam(mount.Team)
                && AdimiToolsMountSafety.TryGetCurrentRider(Mission, mount, out Agent? rider))
            {
                mount.SetTeam(rider == null ? member.MountTeam ?? member.OriginalTeam : rider.Team, sync: true);
            }
        }

        if (touchAgents && IsLiving(member.Agent) && IsLiving(member.Agent.MountAgent) && IsBattleTeam(member.Agent.MountAgent!.Team)
            && AdimiToolsMountSafety.TryGetCurrentRider(Mission, member.Agent.MountAgent, out Agent? currentRider)
            && ReferenceEquals(currentRider, member.Agent))
        {
            member.Agent.MountAgent.SetTeam(member.Agent.Team, sync: true);
        }
    }

    private void Initialize()
    {
        string[] roles = { AdimiToolsTeamBattlePolicy.SideA, AdimiToolsTeamBattlePolicy.SideB, AdimiToolsTeamBattlePolicy.Area, AdimiToolsTeamBattlePolicy.Barrier };
        var areas = new List<SpatialArea>();
        var assigned = new Dictionary<GameEntity, List<GameEntity>>();
        var invalid = new HashSet<GameEntity>();
        var ids = new HashSet<int>();
        int nextId = 1;
        foreach (GameEntity entity in Mission.Scene.FindEntitiesWithTag(AdimiToolsTeamBattlePolicy.Area).Distinct())
        {
            try
            {
                VolumeBox box = entity.GetFirstScriptOfType<VolumeBox>() ?? throw new InvalidOperationException("Area needs VolumeBox.");
                ValidateBoxFrame(entity);
                int id;
                if (AdimiToolsTeamBattlePolicy.TryGetGroupId(entity.Tags, out int legacyId) && ids.Add(legacyId))
                {
                    id = legacyId;
                }
                else
                {
                    while (ids.Contains(nextId))
                    {
                        nextId++;
                    }

                    id = nextId;
                    ids.Add(id);
                }

                bool sharedArea = entity.HasTag(AdimiToolsAiArenaTags.AreaRoleTag);
                AdimiToolsSharedArena? shared = sharedArea ? AdimiToolsSharedArena.For(Mission, box) : null;
                if (sharedArea)
                {
                    string[] aiGroups = entity.Tags.Where(tag => tag.StartsWith(AdimiToolsAiArenaTags.GroupPrefix, StringComparison.Ordinal)).ToArray();
                    if (aiGroups.Length != 1 || !AdimiToolsAiArenaTags.TryParseGroupTag(aiGroups[0], out _) || !shared!.HasAiController)
                    {
                        throw new InvalidOperationException("A shared area needs exactly one canonical AI group and its successfully initialized AI controller.");
                    }
                }

                areas.Add(new SpatialArea(entity, box, id, shared));
                assigned[entity] = new List<GameEntity>();
                if (!HasOneTeamRole(entity, roles, sharedArea: sharedArea) || HasMovingAncestor(entity))
                {
                    invalid.Add(entity);
                }
            }
            catch (Exception exception)
            {
                invalid.Add(entity);
                AdimiToolsConsoleLog.Log($"Team battle area discovery failed: {exception.Message}");
            }
        }

        foreach (GameEntity ready in Mission.Scene.FindEntitiesWithTag(AdimiToolsTeamBattlePolicy.SideA)
            .Concat(Mission.Scene.FindEntitiesWithTag(AdimiToolsTeamBattlePolicy.SideB)).Distinct())
        {
            SpatialArea[] owners = Array.Empty<SpatialArea>();
            try
            {
                Vec3 point = ready.GlobalPosition;
                if (!point.IsValid)
                {
                    throw new InvalidOperationException("A ready-box center must be finite.");
                }

                owners = FindSpatialOwners(areas, invalid, new[] { point }, includeRaised: false);
                if (owners.Length != 1)
                {
                    foreach (SpatialArea owner in owners)
                    {
                        invalid.Add(owner.Entity);
                    }

                    AdimiToolsConsoleLog.Log($"Team ready box '{ready.Name}' skipped: its center must belong to exactly one battle-area box.");
                    continue;
                }

                assigned[owners[0].Entity].Add(ready);
                if (!HasOneTeamRole(ready, roles) || HasMovingAncestor(ready))
                {
                    invalid.Add(owners[0].Entity);
                }
            }
            catch (Exception exception)
            {
                foreach (SpatialArea owner in owners)
                {
                    invalid.Add(owner.Entity);
                }

                AdimiToolsConsoleLog.Log($"Team ready-box discovery failed: {exception.Message}");
            }
        }

        var declarationsByRoot = new Dictionary<GameEntity, List<GameEntity>>();
        foreach (GameEntity declaration in Mission.Scene.FindEntitiesWithTag(AdimiToolsTeamBattlePolicy.Barrier).Distinct())
        {
            try
            {
                GameEntity root = GetTeamBarrierRoot(declaration);
                if (!declarationsByRoot.TryGetValue(root, out var declarations))
                {
                    declarationsByRoot[root] = declarations = new List<GameEntity>();
                }

                declarations.Add(declaration);
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Team barrier-root discovery failed: {exception.Message}");
            }
        }

        foreach (var (root, declarations) in declarationsByRoot)
        {
            SpatialArea[] owners = Array.Empty<SpatialArea>();
            try
            {
                Vec3[] points = declarations.Select(entity => entity.GlobalPosition).ToArray();
                if (points.Any(point => !point.IsValid))
                {
                    throw new InvalidOperationException("Barrier centers must be finite.");
                }

                owners = FindSpatialOwners(areas, invalid, points, includeRaised: true);
                if (owners.Length != 1)
                {
                    foreach (SpatialArea owner in owners)
                    {
                        invalid.Add(owner.Entity);
                    }

                    AdimiToolsConsoleLog.Log($"Team barrier '{root.Name}' skipped: its lowered or raised position must belong to exactly one battle area.");
                    continue;
                }

                AdimiToolsSharedArena? shared = owners[0].Shared;
                bool aiBoundary = shared?.IsAiBoundary(root) == true;
                if (declarations.Any(entity => !HasOneTeamRole(entity, roles, sharedMover: aiBoundary))
                    || (!aiBoundary && HasMovingAncestor(root)))
                {
                    invalid.Add(owners[0].Entity);
                }

                foreach (GameEntity aiMover in Mission.Scene.FindEntitiesWithTag(AdimiToolsAiArenaTags.BarrierRoleTag)
                    .Concat(Mission.Scene.FindEntitiesWithTag(AdimiToolsAiArenaTags.LadderRoleTag)))
                {
                    for (GameEntity? parent = aiMover; parent != null; parent = parent.Parent)
                    {
                        if (parent.Equals(root) && shared?.IsAiBoundary(aiMover) != true)
                        {
                            invalid.Add(owners[0].Entity);
                        }
                    }
                }

                if (!aiBoundary)
                {
                    assigned[owners[0].Entity].Add(root);
                }
            }
            catch (Exception exception)
            {
                foreach (SpatialArea owner in owners)
                {
                    invalid.Add(owner.Entity);
                }

                AdimiToolsConsoleLog.Log($"Team barrier discovery failed: {exception.Message}");
            }
        }

        foreach (var area in areas.OrderBy(area => area.Id))
        {
            try
            {
                var a = assigned[area.Entity].Where(entity => entity.HasTag(AdimiToolsTeamBattlePolicy.SideA)).ToArray();
                var b = assigned[area.Entity].Where(entity => entity.HasTag(AdimiToolsTeamBattlePolicy.SideB)).ToArray();
                if (invalid.Contains(area.Entity) || a.Length != 1 || b.Length != 1)
                {
                    throw new InvalidOperationException("The battle-area box needs exactly one spatially contained A ready box and one B ready box, without ambiguous or shared movement ownership.");
                }

                VolumeBox sideA = a[0].GetFirstScriptOfType<VolumeBox>() ?? throw new InvalidOperationException("A needs VolumeBox.");
                VolumeBox sideB = b[0].GetFirstScriptOfType<VolumeBox>() ?? throw new InvalidOperationException("B needs VolumeBox.");
                ValidateBoxFrame(a[0]);
                ValidateBoxFrame(b[0]);
                GameEntity[] roots = assigned[area.Entity].Where(entity => entity.HasTag(AdimiToolsTeamBattlePolicy.Barrier)).ToArray();
                List<AdimiToolsTeamBattleBarrier> barriers = roots.Select(root => new AdimiToolsTeamBattleBarrier(root)).ToList();

                AdimiToolsSharedArena duelBoundary = AdimiToolsSharedArena.For(Mission, area.Box);
                var arena = new Arena
                {
                    Id = area.Id,
                    SideA = sideA,
                    SideB = sideB,
                    Area = area.Box,
                    Shared = area.Shared,
                    DuelBoundary = duelBoundary,
                    Barriers = barriers,
                };
                duelBoundary.RegisterBoundary(arena, isAi: false, roots,
                    (raised, immediately) =>
                    {
                        var failures = new List<Exception>();
                        foreach (var barrier in barriers)
                        {
                            Attempt(() => barrier.Move(raised, Mission.CurrentTime, immediately), failures);
                        }

                        if (failures.Count != 0)
                        {
                            throw new AggregateException(failures);
                        }
                    },
                    now =>
                    {
                        var failures = new List<Exception>();
                        foreach (var barrier in barriers)
                        {
                            Attempt(() => barrier.Tick(now), failures);
                        }

                        if (failures.Count != 0)
                        {
                            throw new AggregateException(failures);
                        }
                    },
                    raised => barriers.All(barrier => !barrier.IsMoving && barrier.IsRaised == raised && barrier.IsSynchronized),
                    isAvailableForDuel: () => !_stopped && !arena.Disabled && arena.Readiness.Phase == AdimiToolsTeamBattlePhase.Waiting
                        && barriers.All(barrier => !barrier.IsMoving && !barrier.IsRaised && barrier.IsSynchronized));
                _arenas.Add(arena);
                AdimiToolsConsoleLog.Log($"Team battle area {area.Id} ready by spatial boxes: 3s stable + 3s countdown, unequal rosters allowed, barriers={roots.Length}.");
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Team battle area {area.Id} disabled: {exception.Message}");
            }
        }

        foreach (GameEntity selector in Mission.Scene.FindEntitiesWithTag(AdimiToolsTeamBattlePolicy.FriendlyFireToggle))
        {
            Arena[] matches = _arenas.Where(arena => arena.Area.IsPointIn(selector.GlobalPosition)).Take(2).ToArray();
            if (matches.Length != 1 || selector.HasTag(AdimiToolsDuelModes.CycleTag))
            {
                AdimiToolsConsoleLog.Log($"Team TK selector '{selector.Name}' needs one arena and its own tag.");
                continue;
            }

            try
            {
                if (selector.GetFirstScriptOfType<AdimiToolsTeamFriendlyFireTrigger>() == null)
                {
                    selector.CreateAndAddScriptComponent(nameof(AdimiToolsTeamFriendlyFireTrigger), callScriptCallbacks: true);
                }

                var trigger = selector.GetFirstScriptOfType<AdimiToolsTeamFriendlyFireTrigger>()
                    ?? throw new InvalidOperationException("Server hit observer was not created.");
                trigger.Bind(this, matches[0].Area);
                AdimiToolsConsoleLog.Log($"Team TK selector '{selector.Name}' ready for arena {matches[0].Id}.");
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Team TK selector '{selector.Name}' skipped: {exception.Message}");
            }
        }
    }

    private void Stop(bool sceneClearing = false)
    {
        _stopped = true;
        foreach (Arena arena in _arenas)
        {
            bool recoveryFailed = false;
            try
            {
                SetEnemies(arena, false);
            }
            catch (Exception exception)
            {
                recoveryFailed = true;
                AdimiToolsConsoleLog.Log($"Team battle relation cleanup: {exception.Message}");
            }

            foreach (Member member in arena.Members.Values)
            {
                try
                {
                    RestoreTeam(arena, member, touchAgents: false);
                }
                catch (Exception exception)
                {
                    recoveryFailed = true;
                    AdimiToolsConsoleLog.Log($"Team battle peer cleanup: {exception.Message}");
                }
            }

            if (arena.Shared != null)
            {
                if (arena.Shared.Owns(arena))
                {
                    // OnClearScene follows native actor deletion. Ordinary
                    // behavior removal must clear its still-live area first.
                    bool areaClean = sceneClearing;
                    if (!sceneClearing)
                    {
                        try
                        {
                            areaClean = AdimiToolsAiArenaCleanup.TryClear(Mission, arena.Area);
                        }
                        catch (Exception exception)
                        {
                            recoveryFailed = true;
                            AdimiToolsConsoleLog.Log($"Shared team battle area cleanup: {exception.Message}");
                        }
                    }

                    try
                    {
                        arena.Shared.Move(arena, raised: false, immediately: true);
                        // A stopped behavior cannot run the late-drop sweep
                        // after deferred deletion. Only native scene clearing
                        // proves that no outstanding area work remains here.
                        if (sceneClearing && !recoveryFailed && areaClean && arena.Shared.IsAtTarget(arena, raised: false))
                        {
                            arena.Shared.Release(arena);
                        }
                    }
                    catch (Exception exception)
                    {
                        // If removal fails to restore a boundary, its lease
                        // remains closed rather than admitting another match.
                        AdimiToolsConsoleLog.Log($"Shared team battle boundary cleanup: {exception.Message}");
                    }
                }
            }
            else if (arena.DuelBoundary?.IsOwnedByOther(arena) != true)
            {
                foreach (var barrier in arena.Barriers)
                {
                    try
                    {
                        barrier.Move(false, Mission.CurrentTime, immediately: true);
                    }
                    catch (Exception exception)
                    {
                        AdimiToolsConsoleLog.Log($"Team battle barrier cleanup: {exception.Message}");
                    }
                }
            }

            arena.NormalCompletion = false;
            arena.Members.Clear();
            arena.Candidates.Clear();
            arena.Series = null;
            arena.NextRoundAt = null;
            arena.RespawnDeadline = 0d;
            arena.RoundAreaClean = false;
            arena.SharedLoweringStarted = false;
            arena.Readiness.Reset();
        }
    }
}
