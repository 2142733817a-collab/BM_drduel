using AdimiToolsHelper;
using AdimiToolsShared;
using NetworkMessages.FromClient;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.LinQuick;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.MissionRepresentatives;
using TaleWorlds.MountAndBlade.Network.Messages;
using TaleWorlds.ObjectSystem;
using TaleWorlds.PlayerServices;
using static TaleWorlds.MountAndBlade.Agent;

namespace AdimiDuel_Server.Duel;

/// <summary>
/// Mostly decompiled native duel gamemode with a few adjustments.
/// </summary>
internal class AdimiToolsMissionMultiplayerDuel : MissionMultiplayerGameModeBase
{
    public class DuelConfig
    {
        public int FirstToLimit { get; set; }
        public NetworkCommunicator NetworkPeer { get; set; }
        public MissionTime? LastTeleportWarningTimer { get; set; }
        public List<ItemObject>? RespawnEquip { get; set; } // RespawnEquip
        public MatrixFrame? DeathFrame { get; set; } // Last Death frame
        public int NoWeaponTreshold { get; set; }

        public DuelConfig(NetworkCommunicator networkPeer)
        {
            NetworkPeer = networkPeer;
            FirstToLimit = 1;
            LastTeleportWarningTimer = null;
            RespawnEquip = null;
            DeathFrame = null;
            NoWeaponTreshold = 0;
        }

        public void OnAgentWieldedItemChange()
        {
            AdimiToolsConsoleLog.Log("Called OnAgentWieldedItemChange");
            MissionPeer missionPeer = NetworkPeer.GetComponent<MissionPeer>();
            Agent agent = missionPeer.ControlledAgent;

            if (PlayersDuelConfig.TryGetValue(NetworkPeer.VirtualPlayer.Id, out DuelConfig? duelConfig))
            {
                EquipmentIndex wieldedItemIndex = agent.GetPrimaryWieldedItemIndex();
                if (wieldedItemIndex != EquipmentIndex.None)
                {
                    duelConfig.NoWeaponTreshold = 0;
                    AdimiToolsConsoleLog.Log("Reset NoWeaponTreshold");
                }
            }
        }
    }

    private class DuelInfo
    {
        private enum ChallengerType
        {
            None = -1,
            Requester,
            Requestee,
            NumChallengerType,
        }

        private struct Challenger
        {
            public readonly MissionPeer MissionPeer;

            public readonly NetworkCommunicator? NetworkPeer;

            public Agent? DuelingAgent { get; private set; }

            public Agent? MountAgent { get; private set; }
            public bool IsCavalry { get; private set; }

            public int KillCountInDuel { get; private set; }

            public Challenger(MissionPeer missionPeer)
            {
                MissionPeer = missionPeer;
                NetworkPeer = MissionPeer.GetNetworkPeer() ?? null;
                DuelingAgent = null;
                MountAgent = null;
                IsCavalry = false;
                KillCountInDuel = 0;
            }

            public void OnDuelPreparation(Team duelingTeam)
            {
                // MissionPeer.ControlledAgent?.FadeOut(hideInstantly: true, hideMount: true); // We do not want to respawn the player. Just start the duel in place.
                MissionPeer.Team = duelingTeam;
                Agent? agent = MissionPeer.ControlledAgent;
                if (AdimiToolsMountSafety.IsLiving(Mission.Current, agent) && ReferenceEquals(agent!.MissionPeer, MissionPeer))
                {
                    agent.SetTeam(duelingTeam, true);
                }

                MissionPeer.HasSpawnedAgentVisuals = true;
            }

            public void OnDuelEnded()
            {
                if (MissionPeer.GetNetworkPeer()?.IsConnectionActive == true)
                {
                    MissionPeer.Team = Mission.Current.AttackerTeam;
                    Agent? agent = MissionPeer.ControlledAgent;
                    if (AdimiToolsMountSafety.IsLiving(Mission.Current, agent) && ReferenceEquals(agent!.MissionPeer, MissionPeer))
                    {
                        agent.SetTeam(Mission.Current.AttackerTeam, true);
                    }

                    if (PlayerDuelData.PlayerDuelList.ContainsKey(MissionPeer.Peer.Id))
                    {
                        PlayerDuelData playerDuelData = PlayerDuelData.PlayerDuelList[MissionPeer.Peer.Id];
                        if (PlayersDuelConfig.TryGetValue(MissionPeer.Peer.Id, out DuelConfig? duelConfig) && !playerDuelData.Equipment.IsEmpty())
                        {
                            duelConfig.RespawnEquip = playerDuelData.Equipment;
                        }

                        PlayerDuelData.PlayerDuelList.Remove(MissionPeer.Peer.Id);
                    }
                }
            }

            public void IncreaseWinCount()
            {
                KillCountInDuel++;
            }

            public void SetAgents(Agent agent)
            {
                DuelingAgent = agent;
                bool living = AdimiToolsMountSafety.IsLiving(Mission.Current, agent);
                MountAgent = living ? agent.MountAgent : null;
                IsCavalry = living && agent.Character.DefaultFormationClass is
                    FormationClass.Cavalry or FormationClass.HeavyCavalry or FormationClass.LightCavalry or FormationClass.HorseArcher;
            }
        }

        public MissionPeer RequesterPeer => _challengers[0].MissionPeer;
        public int RequesterDamageDealt { get; set; }
        public MissionPeer RequesteePeer => _challengers[1].MissionPeer;
        public int RequesteeDamageDealt { get; set; }
        public int DuelAreaIndex { get; private set; }
        public TroopType DuelAreaTroopType { get; private set; }
        public MissionTime Timer { get; private set; }
        public Team? DuelingTeam { get; private set; }
        public bool Started { get; private set; }
        public bool ChallengeEnded { get; set; }
        public bool BoundaryAttempted { get; set; }
        public int FirstToLimit { get; private set; } = 1;

        private const float DuelStartCountdown = 3f;
        private readonly Challenger[] _challengers;
        private ChallengerType _winnerChallengerType = ChallengerType.None;

        public MissionPeer? ChallengeWinnerPeer
        {
            get
            {
                if (_winnerChallengerType != ChallengerType.None)
                {
                    return _challengers[(int)_winnerChallengerType].MissionPeer;
                }

                return null;
            }
        }

        public MissionPeer? ChallengeLoserPeer
        {
            get
            {
                if (_winnerChallengerType != ChallengerType.None)
                {
                    return _challengers[(_winnerChallengerType == ChallengerType.Requester) ? 1 : 0].MissionPeer;
                }

                return null;
            }
        }

        public DuelInfo(MissionPeer requesterPeer, MissionPeer requesteePeer, KeyValuePair<int, TroopType> duelAreaPair)
        {
            DuelAreaIndex = duelAreaPair.Key;
            DuelAreaTroopType = duelAreaPair.Value;
            _challengers = new Challenger[2];
            _challengers[0] = new Challenger(requesterPeer);
            _challengers[1] = new Challenger(requesteePeer);
            Timer = MissionTime.Now + MissionTime.Seconds(10 + DuelRequestTimeOutServerToleranceInSeconds);
        }

        public void OnDuelPreparation(Team duelTeam)
        {
            if (!Started)
            {
                GameNetwork.BeginBroadcastModuleEvent();
                GameNetwork.WriteMessage(new DuelPreparationStartedForTheFirstTime(_challengers[0].MissionPeer.GetNetworkPeer(), _challengers[1].MissionPeer.GetNetworkPeer(), DuelAreaIndex));
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
                _challengers[0].SetAgents(_challengers[0].MissionPeer.ControlledAgent); // Initially set agents, otherwise the values are invalid.
                _challengers[1].SetAgents(_challengers[1].MissionPeer.ControlledAgent);

                List<ItemObject> requesterItemList = new();
                for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumPrimaryWeaponSlots; i++)
                {
                    if (_challengers[0].MissionPeer.ControlledAgent.Equipment[i].Item != null)
                    {
                        requesterItemList.Add(_challengers[0].MissionPeer.ControlledAgent.Equipment[i].Item);
                    }
                }

                List<ItemObject> requesteeItemList = new();
                for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumPrimaryWeaponSlots; i++)
                {
                    if (_challengers[1].MissionPeer.ControlledAgent.Equipment[i].Item != null)
                    {
                        requesteeItemList.Add(_challengers[1].MissionPeer.ControlledAgent.Equipment[i].Item);
                    }
                }

                _ = new PlayerDuelData(_challengers[0].NetworkPeer!.VirtualPlayer.Id, _challengers[0].MissionPeer, requesterItemList);
                _ = new PlayerDuelData(_challengers[1].NetworkPeer!.VirtualPlayer.Id, _challengers[1].MissionPeer, requesteeItemList);

                DuelConfig duelConfig1 = PlayersDuelConfig.GetValueOrDefault(_challengers[0].NetworkPeer!.VirtualPlayer.Id)!;
                FirstToLimit = duelConfig1.FirstToLimit;

                AdimiToolsNotifier.ServerSendMessageToPlayer(_challengers[0].NetworkPeer!, $"你与 {_challengers[1].NetworkPeer!.UserName} 的对局已开始：{AdimiToolsDuelModes.GetModeName(FirstToLimit)}。");
                AdimiToolsNotifier.ServerSendMessageToPlayer(_challengers[1].NetworkPeer!, $"你与 {_challengers[0].NetworkPeer!.UserName} 的对局已开始：{AdimiToolsDuelModes.GetModeName(FirstToLimit)}。");
            }

            Started = false;
            DuelingTeam = duelTeam;
            _winnerChallengerType = ChallengerType.None;

            // Add spawned agent as current agent.
            RestoreAgent(_challengers[0].MissionPeer.ControlledAgent);
            RestoreAgent(_challengers[1].MissionPeer.ControlledAgent);
            for (int i = 0; i < 2; i++)
            {
                _challengers[i].OnDuelPreparation(DuelingTeam);
                _challengers[i].MissionPeer.GetComponent<DuelMissionRepresentative>().OnDuelPreparation(_challengers[0].MissionPeer, _challengers[1].MissionPeer);
            }

            Timer = MissionTime.Now + MissionTime.Seconds(DuelStartCountdown);
        }

        public void OnDuelStarted()
        {
            foreach (Challenger challenger in _challengers)
            {
                if (PlayersDuelConfig.TryGetValue(challenger.MissionPeer.Peer.Id, out DuelConfig? config))
                {
                    config.NoWeaponTreshold = 0;
                }
            }

            Started = true;
            DuelingTeam?.SetIsEnemyOf(DuelingTeam, isEnemyOf: true);
        }

        public void OnDuelEnding()
        {
            Timer = MissionTime.Now + MissionTime.Seconds(DuelEndInSeconds);
        }

        public void OnDuelEnded()
        {
            if (Started)
            {
                DuelingTeam?.SetIsEnemyOf(DuelingTeam, isEnemyOf: false);
            }

            DecideRoundWinner();

            for (int i = 0; i < 2; i++)
            {
                if (ChallengeEnded)
                {
                    _challengers[i].OnDuelEnded();
                    Agent? agent = _challengers[i].DuelingAgent ?? _challengers[i].MissionPeer.ControlledAgent;
                    // agent.FadeOut(hideInstantly: true, hideMount: false);
                    // Do not fade out winner. Heal him etc.
                    if (AdimiToolsMountSafety.IsLiving(Mission.Current, agent)
                        && ReferenceEquals(agent!.MissionPeer, _challengers[i].MissionPeer))
                    {
                        RestoreAgent(agent);
                    }
                    else
                    {
                        _challengers[i].MissionPeer.HasSpawnedAgentVisuals = true;
                    }
                }

                // Only the original living, unoccupied loser mount may be
                // removed. A saved mount may now belong to another rider.
                Agent? mount = _challengers[i].MountAgent;
                if (!AdimiToolsMountSafety.IsLiving(Mission.Current, _challengers[i].DuelingAgent)
                    && AdimiToolsMountSafety.IsLiving(Mission.Current, mount)
                    && AdimiToolsMountSafety.CanFade(Mission.Current, mount)
                    && (ChallengeEnded || mount!.RiderAgent == null))
                {
                    mount!.FadeOut(hideInstantly: true, hideMount: false);
                }
            }
        }

        public void OnAgentBuild(Agent agent)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_challengers[i].MissionPeer == agent.MissionPeer)
                {
                    _challengers[i].SetAgents(agent);
                    break;
                }
            }
        }

        public bool TryGetCapturedMountReset(out Agent? winner, out Agent? mount)
        {
            winner = null;
            mount = null;
            if (!Started || _winnerChallengerType == ChallengerType.None)
            {
                return false;
            }

            int winnerIndex = (int)_winnerChallengerType;
            Challenger victor = _challengers[winnerIndex];
            Challenger defeated = _challengers[1 - winnerIndex];
            Agent? rider = victor.DuelingAgent;
            Agent? horse = defeated.MountAgent;
            if (!AdimiToolsMountSafety.IsLiving(Mission.Current, rider)
                || !ReferenceEquals(victor.MissionPeer.ControlledAgent, rider)
                || rider!.Character.DefaultFormationClass is not (FormationClass.Infantry or FormationClass.HeavyInfantry)
                || !defeated.IsCavalry
                || AdimiToolsMountSafety.IsLiving(Mission.Current, defeated.MissionPeer.ControlledAgent)
                || AdimiToolsMountSafety.IsLiving(Mission.Current, defeated.DuelingAgent)
                || !AdimiToolsMountSafety.IsLiving(Mission.Current, horse)
                || !ReferenceEquals(rider.MountAgent, horse)
                || !AdimiToolsMountSafety.TryGetCurrentRider(Mission.Current, horse, out Agent? currentRider)
                || !ReferenceEquals(currentRider, rider))
            {
                return false;
            }

            winner = rider;
            mount = horse;
            return true;
        }

        public bool IsDuelStillValid(bool doNotCheckAgent = false)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_challengers[i].MissionPeer.GetNetworkPeer()?.IsConnectionActive != true
                    || (!doNotCheckAgent && !AdimiToolsMountSafety.IsLiving(Mission.Current, _challengers[i].MissionPeer.ControlledAgent)))
                {
                    return false;
                }
            }

            return true;
        }

        public void CancelRestart()
        {
            ChallengeEnded = true;
            Started = false;
            _winnerChallengerType = ChallengerType.None;
        }

        public bool IsPeerInThisDuel(MissionPeer peer)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_challengers[i].MissionPeer == peer)
                {
                    return true;
                }
            }

            return false;
        }

        public void UpdateDuelAreaIndex(KeyValuePair<int, TroopType> duelAreaPair)
        {
            DuelAreaIndex = duelAreaPair.Key;
            DuelAreaTroopType = duelAreaPair.Value;
        }

        private void DecideRoundWinner()
        {
            bool isConnectionActive = _challengers[0].MissionPeer.GetNetworkPeer()?.IsConnectionActive == true;
            bool isConnectionActive2 = _challengers[1].MissionPeer.GetNetworkPeer()?.IsConnectionActive == true;
            if (!Started)
            {
                if (isConnectionActive == isConnectionActive2)
                {
                    ChallengeEnded = true;
                }
                else
                {
                    _winnerChallengerType = (!isConnectionActive) ? ChallengerType.Requestee : ChallengerType.Requester;
                }
            }
            else
            {
                Agent? duelingAgent = _challengers[0].DuelingAgent;
                Agent? duelingAgent2 = _challengers[1].DuelingAgent;
                if (AdimiToolsMountSafety.IsLiving(Mission.Current, duelingAgent))
                {
                    _winnerChallengerType = ChallengerType.Requester;
                }
                else if (AdimiToolsMountSafety.IsLiving(Mission.Current, duelingAgent2))
                {
                    _winnerChallengerType = ChallengerType.Requestee;
                }
                else
                {
                    if (isConnectionActive && !isConnectionActive2)
                    {
                        _winnerChallengerType = ChallengerType.Requester;
                    }
                    else if (!isConnectionActive && isConnectionActive2)
                    {
                        _winnerChallengerType = ChallengerType.Requestee;
                    }
                    else // if (!isConnectionActive && !isConnectionActive2) // Not necessary in this case.
                    {
                        _winnerChallengerType = ChallengerType.None;
                    }

                    ChallengeEnded = true;
                }
            }

            if (_winnerChallengerType != ChallengerType.None)
            {
                _challengers[(int)_winnerChallengerType].IncreaseWinCount();
                GameNetwork.BeginBroadcastModuleEvent();
                GameNetwork.WriteMessage(new DuelRoundEnded(_challengers[(int)_winnerChallengerType].NetworkPeer));
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);

                if (_challengers[(int)_winnerChallengerType].KillCountInDuel >= FirstToLimit || !isConnectionActive || !isConnectionActive2) // In Warband there is always just one duel.
                {
                    ChallengeEnded = true;
                }
            }
        }
    }

    public const float DuelEndInSeconds = 0f; // End Duel immediately on next tick.
    public const float DuelRequestTimeOutInSeconds = 10f;
    public static readonly Dictionary<PlayerId, DuelConfig> PlayersDuelConfig = new();
    public static readonly FactionColor AdminColor = new() { PrimaryColor = 127, IconColor = 116 };
    public List<FactionColor> PlayerColors { get; private set; }
    private const float DuelRequestTimeOutServerToleranceInSeconds = 0.5f;
    private const float CorpseFadeOutTimeInSeconds = 10f;
    private readonly List<DuelInfo> _duelRequests;
    private readonly List<DuelInfo> _activeDuels;
    private sealed class DuelBoundaryHold
    {
        public DuelBoundaryHold(AdimiToolsSharedArena boundary, MatrixFrame? respawnFrame)
        {
            Boundary = boundary;
            RespawnFrame = respawnFrame;
        }

        public AdimiToolsSharedArena Boundary { get; }
        public MatrixFrame? RespawnFrame { get; }
        public bool Lowering { get; set; }
        public bool RecoveryPending { get; set; }
        public double RetryAt { get; set; }
    }

    private readonly Dictionary<DuelInfo, DuelBoundaryHold> _duelBoundaries = new();
    private readonly List<DuelInfo> _endingDuels;
    private readonly List<DuelInfo> _restartingDuels;
    private readonly List<DuelInfo> _restartPreparationDuels;
    private readonly Queue<Team> _deactiveDuelTeams;
    private readonly List<KeyValuePair<MissionPeer, TroopType>> _peersAndSelections;
    private readonly SpawnComponent _spawnComponent;
    private readonly Dictionary<UsableMissionObject, GameEntity?> _teleportDoorTargets = new();
    private double _nextDuelBoundaryCheck;
    private MissionTimer? _teleportCheckTimer;
    private MissionTimer? _sheetWeaponCheckTimer;
    private AdimiToolsDuelMarkerSync? _duelMarkerSync;
    private AdimiToolsAiDuelDisplay? _aiDuelDisplay;

    public delegate void OnDuelEndedDelegate(MissionPeer winnerPeer, TroopType troopType);
    public override bool IsGameModeHidingAllAgentVisuals => true;
    public override bool IsGameModeUsingOpposingTeams => false;
    public event OnDuelEndedDelegate? OnDuelEnded;
    public class PlayerDuelData
    {
        public static readonly Dictionary<PlayerId, PlayerDuelData> PlayerDuelList = new();
        public PlayerId Id { get; set; }
        public MissionPeer MissionPeer { get; set; }
        public List<ItemObject> Equipment { get; set; }
        public MatrixFrame? RespawnFrame { get; set; }

        public PlayerDuelData(PlayerId playerId, MissionPeer missionPeer, List<ItemObject> equipment)
        {
            Id = playerId;
            MissionPeer = missionPeer;
            Equipment = equipment;
            RespawnFrame = default!;
            PlayerDuelList.Add(Id, this);
        }
    }

    private class ItemSpawns
    {
        public static readonly List<ItemSpawns> SpawnedList = new();
        public ItemObject ItemObject { get; set; }
        public MatrixFrame SpawnFrame { get; set; }
        public SpawnedItemEntity SpawnItemEntity { get; set; }
        public GameEntity Parent { get; set; }
        public bool Respawn { get; set; }

        public ItemSpawns(ItemObject itemObject, SpawnedItemEntity spawnedItemEntity, MatrixFrame spawnFrame, GameEntity parent, bool respawn)
        {
            ItemObject = itemObject;
            SpawnFrame = spawnFrame;
            SpawnItemEntity = spawnedItemEntity;
            Parent = parent;
            Respawn = respawn;
            SpawnedList.Add(this);
        }
    }

    public AdimiToolsMissionMultiplayerDuel(SpawnComponent spawnComponent)
        : base()
    {
        _duelRequests = new();
        _activeDuels = new();
        _endingDuels = new();
        _restartingDuels = new();
        _restartPreparationDuels = new();
        _deactiveDuelTeams = new();
        _peersAndSelections = new();
        _teleportCheckTimer = null;
        _sheetWeaponCheckTimer = null;
        _spawnComponent = spawnComponent;
        PlayerColors = new();

        // Random faction colors
        var factionColoryellowblack = new FactionColor { PrimaryColor = 143, IconColor = 116 };
        var factionColoryellowred = new FactionColor { PrimaryColor = 131, IconColor = 142 };
        var factionColoryellowblue = new FactionColor { PrimaryColor = 132, IconColor = 12 };

        // red
        var factionColorredblack = new FactionColor { PrimaryColor = 83, IconColor = 116 };
        var factionColorredwhite = new FactionColor { PrimaryColor = 83, IconColor = 127 };
        var factionColorredyellow = new FactionColor { PrimaryColor = 142, IconColor = 39 };

        // green
        var factionColorgreenwhite = new FactionColor { PrimaryColor = 2, IconColor = 31 };
        var factionColorgreenblack = new FactionColor { PrimaryColor = 126, IconColor = 116 };

        // blue
        var factionColorbluewhite = new FactionColor { PrimaryColor = 12, IconColor = 27 };
        var factionColorblueblack = new FactionColor { PrimaryColor = 115, IconColor = 116 };

        // black
        var factionColorblackwhite = new FactionColor { PrimaryColor = 116, IconColor = 128 };
        var factionColorblackred = new FactionColor { PrimaryColor = 116, IconColor = 83 };
        var factionColorblackblue = new FactionColor { PrimaryColor = 116, IconColor = 77 };

        // white
        // var factionColorwhiteblue = new FactionColor { PrimaryColor = 127, IconColor = 77 };
        // var factionColorwhitered = new FactionColor { PrimaryColor = 127, IconColor = 83 };
        // var factionColorwhitegreen = new FactionColor { PrimaryColor = 127, IconColor = 126 };
        // var factionColorwhiteblack = new FactionColor { PrimaryColor = 127, IconColor = 116 };

        // PlayerColors.Add(factionColorwhiteblack);
        // PlayerColors.Add(factionColorwhitered);
        // PlayerColors.Add(factionColorwhiteblue);
        // PlayerColors.Add(factionColorwhitegreen);

        PlayerColors.Add(factionColorblackwhite);
        PlayerColors.Add(factionColorblackred);
        PlayerColors.Add(factionColorblackblue);
        PlayerColors.Add(factionColorbluewhite);
        PlayerColors.Add(factionColorblueblack);
        PlayerColors.Add(factionColorredblack);
        PlayerColors.Add(factionColorredwhite);
        PlayerColors.Add(factionColorredyellow);
        PlayerColors.Add(factionColorgreenwhite);
        PlayerColors.Add(factionColorgreenblack);
        PlayerColors.Add(factionColoryellowblack);
        PlayerColors.Add(factionColoryellowred);
        PlayerColors.Add(factionColoryellowblue);
    }

    public override MultiplayerGameType GetMissionType()
    {
        return MultiplayerGameType.Duel;
    }

    public override void AfterStart()
    {
        base.AfterStart();
        Mission.Current.SetMissionCorpseFadeOutTimeInSeconds(CorpseFadeOutTimeInSeconds);
        BasicCultureObject cultureObject = MBObjectManager.Instance.GetObject<BasicCultureObject>(MultiplayerOptions.OptionType.CultureTeam1.GetStrValue());
        Banner banner = new(cultureObject.Banner, cultureObject.BackgroundColor1, cultureObject.ForegroundColor1);
        Mission.Teams.Add(BattleSideEnum.Attacker, cultureObject.BackgroundColor1, cultureObject.ForegroundColor1, banner, isPlayerGeneral: false);
        _teleportCheckTimer = new MissionTimer(0.3f);
        _sheetWeaponCheckTimer = new MissionTimer(0.5f);
        InitSpawnedWeapons();
        InitTeleportDoors();
        AdimiToolsTeamOrbSync? orbs = Mission.GetMissionBehavior<AdimiToolsTeamOrbSync>();
        _duelMarkerSync = new AdimiToolsDuelMarkerSync(Mission, IsPeerBusyForMarkerUpdates,
            IsPeerInAiChallengeForMarkerUpdates, GetMatchTeamForMarkerUpdates,
            orbs == null ? null : orbs.HasVisibleMarker);
        _aiDuelDisplay = new AdimiToolsAiDuelDisplay(Mission,
            peer => IsPeerBusyForMarkerUpdates(peer, true) || IsPeerInAiChallengeForMarkerUpdates(peer),
            _duelMarkerSync.OnTargetRestored);
    }

    public override void OnClearScene()
    {
        (_spawnComponent.SpawningBehavior as AdimiToolsDuelSpawningBehavior)?.ClearInPlaceDuelRespawns();
        ClearDuelBoundaries();
        _aiDuelDisplay?.Clear();
        base.OnClearScene();
    }

    public override void OnRemoveBehavior()
    {
        ClearDuelBoundaries();
        _aiDuelDisplay?.Clear();
        base.OnRemoveBehavior();
    }

    public override void OnBehaviorInitialize()
    {
        base.OnBehaviorInitialize();
        Mission.OnItemPickUp += OnItemPickUp;
        PlayersDuelConfig.Clear(); // Duel config resetten
        PlayerDuelData.PlayerDuelList.Clear(); // Reset duels on map change
        ItemSpawns.SpawnedList.Clear(); // Reset spawned items on map change
    }

    public override bool CheckIfPlayerCanDespawn(MissionPeer missionPeer)
    {
        if ((_spawnComponent.SpawningBehavior as AdimiToolsDuelSpawningBehavior)?.HasInPlaceDuelRespawn(missionPeer) == true)
        {
            return false;
        }

        if (Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(missionPeer) == true)
        {
            return false;
        }

        if (Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.IsPeerReserved(missionPeer) == true)
        {
            return false;
        }

        return !IsPeerInRealDuelForAiChallenge(missionPeer);
    }

    public void OnPlayerDespawn(MissionPeer missionPeer)
    {
        missionPeer.GetComponent<DuelMissionRepresentative>();
    }

    public void DuelRequestReceived(MissionPeer requesterPeer, MissionPeer requesteePeer)
    {
        if (requesterPeer.Peer.Id == requesteePeer.Peer.Id
            || requesterPeer.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || requesteePeer.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || IsPeerInRealDuelForAiChallenge(requesterPeer) || IsPeerInRealDuelForAiChallenge(requesteePeer))
        {
            return;
        }

        Agent? requesterAgent = requesterPeer.ControlledAgent;
        if (!CanUseDuelAgent(requesterPeer, requesterAgent) || !CanUseDuelAgent(requesteePeer, requesteePeer.ControlledAgent))
        {
            return;
        }

        var teamBattle = Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>();
        if (teamBattle?.IsPeerReserved(requesterPeer) == true || teamBattle?.IsPeerReserved(requesteePeer) == true)
        {
            return;
        }

        AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        if (aiArena?.IsPeerReserved(requesterPeer) == true || aiArena?.IsPeerReserved(requesteePeer) == true)
        {
            return;
        }

        if (!HaveMatchingDuelLimits(requesterPeer, requesteePeer))
        {
            return;
        }

        if (!IsThereARequestBetweenPeers(requesterPeer, requesteePeer))
        {
            DuelInfo duelInfo = new(requesterPeer, requesteePeer, new KeyValuePair<int, TroopType>(0, TroopType.Infantry));
            _duelRequests.Add(duelInfo);
            (requesteePeer.Representative as DuelMissionRepresentative)?.DuelRequested(requesterAgent!, duelInfo.DuelAreaTroopType);
        }
    }

    public void DuelRequestAccepted(Agent requesterAgent, Agent requesteeAgent)
    {
        if (!AdimiToolsMountSafety.IsLiving(Mission, requesterAgent) || !AdimiToolsMountSafety.IsLiving(Mission, requesteeAgent))
        {
            return;
        }

        MissionPeer? requester = requesterAgent.MissionPeer;
        MissionPeer? requestee = requesteeAgent.MissionPeer;
        if (requester == null || requestee == null || requester.Peer.Id == requestee.Peer.Id
            || requester.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || requestee.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || !CanUseDuelAgent(requester, requesterAgent) || !CanUseDuelAgent(requestee, requesteeAgent)
            || IsPeerInRealDuelForAiChallenge(requester) || IsPeerInRealDuelForAiChallenge(requestee))
        {
            return;
        }

        var teamBattle = Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>();
        if (teamBattle?.IsPeerReserved(requester) == true || teamBattle?.IsPeerReserved(requestee) == true)
        {
            return;
        }

        AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        if (aiArena?.IsPeerReserved(requester) == true || aiArena?.IsPeerReserved(requestee) == true)
        {
            return;
        }

        DuelInfo? duelInfo = _duelRequests.FirstOrDefault(di =>
            ReferenceEquals(di.RequesterPeer, requester) && ReferenceEquals(di.RequesteePeer, requestee));
        if (duelInfo != null)
        {
            // Either player may have changed mode while this request was pending.
            if (!HaveMatchingDuelLimits(requester, requestee))
            {
                _duelRequests.Remove(duelInfo);
                return;
            }

            PrepareDuel(duelInfo);
        }
    }

    public override void OnMissionTick(float dt)
    {
        base.OnMissionTick(dt);
        CheckRestartPreparationDuels();
        CheckForRestartingDuels();
        CheckDuelsToStart();
        CheckDuelRequestTimeouts();

        // Doesnt look that fancy but we better call as less funcs as possible in this method.
        if (_sheetWeaponCheckTimer?.Check(true) ?? false)
        {
            CheckForSheatWeapon();
        }

        CheckEndedDuels();
        TickDuelBoundaries();

        if (_teleportCheckTimer?.Check(true) ?? false)
        {
            CheckForTeleport();
        }

        _duelMarkerSync?.Tick(dt);
        _aiDuelDisplay?.Tick(dt);
    }

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
    {
        if (!affectedAgent.IsHuman || affectedAgent.MissionPeer == null)
        {
            return;
        }

        if (PlayersDuelConfig.TryGetValue(affectedAgent.MissionPeer.Peer.Id, out var duelConfig))
        {
            duelConfig.DeathFrame = affectedAgent.Frame;
        }

        if (affectedAgent.MissionPeer.Team?.IsDefender == true)
        {
            if (PlayerDuelData.PlayerDuelList.TryGetValue(affectedAgent.MissionPeer.Peer.Id, out var duelList))
            {
                duelList.RespawnFrame = affectedAgent.Frame;
                if (affectorAgent != null && affectorAgent != affectedAgent && affectorAgent.MissionPeer != null)
                {
                    AdimiToolsNotifier.ServerSendMessageToPlayer(affectedAgent.MissionPeer.GetNetworkPeer(), $"{affectorAgent.MissionPeer.Name} had {affectorAgent.Health}/{affectorAgent.HealthLimit} HP left.");
                }
            }

            DuelInfo? duelInfo = null;
            for (int i = 0; i < _activeDuels.Count; i++)
            {
                if (_activeDuels[i].IsPeerInThisDuel(affectedAgent.MissionPeer))
                {
                    duelInfo = _activeDuels[i];
                }
            }

            if (duelInfo != null && !_endingDuels.Contains(duelInfo))
            {
                if (GetDuelBoundaryRespawnFrame(duelInfo) is MatrixFrame spawn
                    && PlayerDuelData.PlayerDuelList.TryGetValue(affectedAgent.MissionPeer.Peer.Id, out var roundData))
                {
                    try
                    {
                        roundData.RespawnFrame = FindClearDuelRespawn(duelInfo, spawn) ?? roundData.RespawnFrame;
                    }
                    catch (Exception exception)
                    {
                        AdimiToolsConsoleLog.Log($"1v1 arena respawn search failed: {exception.Message}");
                    }
                }

                duelInfo.OnDuelEnding();
                _endingDuels.Add(duelInfo);
            }

            return;
        }

        for (int num = _duelRequests.Count - 1; num >= 0; num--)
        {
            if (_duelRequests[num].IsPeerInThisDuel(affectedAgent.MissionPeer))
            {
                _duelRequests.RemoveAt(num);
            }
        }
    }

    public int GetDuelAreaIndexIfDuelTeam(Team team)
    {
        if (team.IsDefender)
        {
            return _activeDuels.FirstOrDefaultQ((DuelInfo ad) => ad.DuelingTeam == team).DuelAreaIndex;
        }

        return -1;
    }

    public override void OnObjectUsed(Agent userAgent, UsableMissionObject usedObject)
    {
        base.OnObjectUsed(userAgent, usedObject);
        if (!GameNetwork.IsServerOrRecorder || userAgent == null || !userAgent.IsActive() || !userAgent.IsHuman
            || !ReferenceEquals(userAgent.CurrentlyUsedGameObject, usedObject)
            || !_teleportDoorTargets.TryGetValue(usedObject, out GameEntity? target))
        {
            return;
        }

        MissionPeer? peer = userAgent.MissionPeer;
        NetworkCommunicator? networkPeer = peer?.GetNetworkPeer();
        if (networkPeer == null || !networkPeer.IsConnectionActive || !networkPeer.IsSynchronized
            || !ReferenceEquals(peer!.ControlledAgent, userAgent))
        {
            return;
        }

        // Native use validation and StandingPoint.OnUse have already run. Stop
        // before the gate's next tick can treat the user as an opening request.
        userAgent.StopUsingGameObject(isSuccessful: true, StopUsingGameObjectFlags.None);
        if (Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(peer!) == true)
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, "组队对战准备或进行中，暂时不能使用传送门。");
            return;
        }

        if (target == null)
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, "此传送门的标签或落点配置有误，请联系管理员。");
            return;
        }

        AdimiToolsTeleportDoors.TeleportToTarget(userAgent, target);
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        // Just to debug the spawn location. Might be used again in FtX duels
        // GameEntity ge = Mission.Scene.FindEntityWithTag("mp_weaponspawn_test");
        // agent.TeleportToPosition(_spawnedWeapon!.GameEntityWithWorldPosition.WorldPosition.GetGroundVec3());
        // agent.TeleportToPosition(ge.GetGlobalFrame().origin);

        if (!agent.IsHuman || agent.Team == null || agent.MissionPeer == null)
        {
            return;
        }

        if (PlayersDuelConfig.TryGetValue(agent.MissionPeer.Peer.Id, out DuelConfig? duelConfig))
        {
            agent.OnAgentWieldedItemChange += duelConfig.OnAgentWieldedItemChange;
        }

        if (!agent.Team.IsDefender)
        {
            return;
        }

        for (int i = 0; i < _restartPreparationDuels.Count; i++)
        {
            if (_restartPreparationDuels[i].IsPeerInThisDuel(agent.MissionPeer))
            {
                _restartPreparationDuels[i].OnAgentBuild(agent);
                break;
            }
        }
    }

    public override void OnScoreHit(Agent victim, Agent attacker, WeaponComponentData attackerWeapon, bool isBlocked, bool isSiegeEngineHit, in Blow blow, in AttackCollisionData collisionData, float damagedHp, float hitDistance, float shotDifficulty)
    {
        if (isBlocked)
        {
            return;
        }

        if (attacker == null || attacker == victim || !attacker.IsHuman || attacker.MissionPeer == null)
        {
            return;
        }

        int actualDamagedHp = (int)Math.Round(damagedHp);

        // Most likely there is a fancier way to get the duel info here.
        DuelInfo? duelInfo = null;
        for (int i = 0; i < _activeDuels.Count; i++)
        {
            if (_activeDuels[i].IsPeerInThisDuel(attacker.MissionPeer))
            {
                duelInfo = _activeDuels[i];
                if (attacker.MissionPeer == duelInfo.RequesterPeer)
                {
                    duelInfo.RequesterDamageDealt += actualDamagedHp;
                }
                else if (attacker.MissionPeer == duelInfo.RequesteePeer)
                {
                    duelInfo.RequesteeDamageDealt += actualDamagedHp;
                }

                break;
            }
        }
    }

    internal static void RestoreAgentForAiChallenge(Agent agent)
    {
        RestoreAgent(agent);
        // Refill the existing item, including thrown weapons currently using
        // their melee mode. Never replace equipment or reselect wielded slots.
        for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
        {
            MissionWeapon weapon = agent.Equipment[slot];
            if (!weapon.IsEmpty && weapon.IsAnyConsumable() && weapon.Amount < weapon.ModifiedMaxAmount)
            {
                agent.SetWeaponAmountInSlot(slot, weapon.ModifiedMaxAmount, enforcePrimaryItem: true);
            }
        }
    }

    internal bool IsFightingIn(VolumeBox area)
    {
        foreach (DuelInfo duel in _activeDuels)
        {
            if (!duel.Started || duel.ChallengeEnded || _endingDuels.Contains(duel))
            {
                continue;
            }

            if (_duelBoundaries.TryGetValue(duel, out DuelBoundaryHold? hold))
            {
                if (!hold.Lowering && !hold.RecoveryPending && hold.Boundary.IsForArea(area))
                {
                    return true;
                }

                continue;
            }

            Agent? first = duel.RequesterPeer.ControlledAgent;
            Agent? second = duel.RequesteePeer.ControlledAgent;
            if (AdimiToolsMountSafety.IsLiving(Mission, first) && AdimiToolsMountSafety.IsLiving(Mission, second)
                && area.IsPointIn(GetDuelGround(first!)) && area.IsPointIn(GetDuelGround(second!)))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsPeerBusyForAiChallenge(MissionPeer peer)
    {
        // Deliberately excludes AI reservations: the owner can add hits during
        // their countdown. Receiving an unanswered invitation does not reserve
        // its recipient; only their own outgoing request remains busy.
        return IsPeerInRealDuelForAiChallenge(peer)
            || _duelRequests.Any(d => ReferenceEquals(d.RequesterPeer, peer));
    }

    internal bool IsPeerInRealDuelForAiChallenge(MissionPeer peer)
    {
        return _activeDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _endingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartPreparationDuels.Any(d => d.IsPeerInThisDuel(peer));
    }

    internal int CancelIncomingDuelRequestsForAiChallenge(MissionPeer peer)
    {
        // Only authoritative pending invitations are discarded. Native has no
        // request-cancel packet: its client prompt expires normally after 10s.
        // Do not emit DuelEnded for an invitation which never became a duel.
        return _duelRequests.RemoveAll(d => ReferenceEquals(d.RequesteePeer, peer));
    }

    internal int GetFirstToLimitForAiChallenge(MissionPeer peer)
    {
        return PlayersDuelConfig.TryGetValue(peer.Peer.Id, out DuelConfig? config) ? config.FirstToLimit : 1;
    }

    internal void BeginAiChallengeDisplay(int arenaId, MissionPeer owner, Agent template, int totalAiCount, int difficulty, string? troopName = null)
    {
        _aiDuelDisplay?.Begin(arenaId, owner, troopName ?? template.Character.Name.ToString(), totalAiCount, difficulty);
    }

    internal void RecordAiChallengeDisplayRound(int arenaId, bool playerWon)
    {
        _aiDuelDisplay?.RoundEnded(arenaId, playerWon);
    }

    internal void EndAiChallengeDisplay(int arenaId)
    {
        _aiDuelDisplay?.End(arenaId);
    }

    internal int RecordAiChallengeResult(MissionPeer peer, bool playerWon)
    {
        DuelMissionRepresentative representative = peer.GetComponent<DuelMissionRepresentative>()
            ?? throw new InvalidOperationException("AI result requires the player's native duel representative.");
        int scoreBefore = representative.Score;
        if (playerWon)
        {
            ApplyDuelWinReward(representative, AdimiToolsDuelScorePolicy.CalculateAiWinReward(representative.NumberOfWins));
        }
        else
        {
            representative.ResetBountyAndNumberOfWins();
        }

        // Native lobby already records the real kills/deaths. Only publish the
        // native duel score/streak/bounty fields here, without fake duel events.
        PublishDuelPoints(representative);

        return representative.Score - scoreBefore;
    }

    protected override void HandleNewClientAfterSynchronized(NetworkCommunicator networkPeer)
    {
        MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
        missionPeer.Team = Mission.AttackerTeam;
        _peersAndSelections.Add(new KeyValuePair<MissionPeer, TroopType>(missionPeer, TroopType.Invalid));
    }

    protected override void HandleLateNewClientAfterSynchronized(NetworkCommunicator networkPeer)
    {
        if (networkPeer.IsServerPeer)
        {
            return;
        }

        foreach (NetworkCommunicator networkPeer2 in GameNetwork.NetworkPeers)
        {
            DuelMissionRepresentative duelMissionRepresentative = networkPeer2.GetComponent<DuelMissionRepresentative>();
            if (duelMissionRepresentative != null && IsDuelPointsRepresentable(duelMissionRepresentative))
            {
                GameNetwork.BeginModuleEventAsServer(networkPeer);
                GameNetwork.WriteMessage(new DuelPointsUpdateMessage(duelMissionRepresentative));
                GameNetwork.EndModuleEventAsServer();
            }

            if (networkPeer != networkPeer2)
            {
                MissionPeer missionPeer = networkPeer2.GetComponent<MissionPeer>();
                if (missionPeer != null)
                {
                    GameNetwork.BeginModuleEventAsServer(networkPeer);
                    GameNetwork.WriteMessage(new SyncPerksForCurrentlySelectedTroop(networkPeer2, missionPeer.Perks[missionPeer.SelectedTroopIndex]));
                    GameNetwork.EndModuleEventAsServer();
                }
            }
        }

        for (int i = 0; i < _activeDuels.Count; i++)
        {
            GameNetwork.BeginModuleEventAsServer(networkPeer);
            GameNetwork.WriteMessage(new DuelPreparationStartedForTheFirstTime(_activeDuels[i].RequesterPeer.GetNetworkPeer(), _activeDuels[i].RequesteePeer.GetNetworkPeer(), _activeDuels[i].DuelAreaIndex));
            GameNetwork.EndModuleEventAsServer();
        }

        MissionPeer? synchronizedPeer = networkPeer.GetComponent<MissionPeer>();
        if (synchronizedPeer != null)
        {
            _duelMarkerSync?.ResetObserver(synchronizedPeer);
            _aiDuelDisplay?.LateJoin(synchronizedPeer);
        }

        Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.SynchronizeLadders(networkPeer);
    }

    protected override void HandleEarlyPlayerDisconnect(NetworkCommunicator networkPeer)
    {
        MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
        if (missionPeer != null)
        {
            _aiDuelDisplay?.RemovePeer(missionPeer);
            _duelMarkerSync?.RemovePeer(missionPeer);
        }

        for (int i = 0; i < _peersAndSelections.Count; i++)
        {
            if (_peersAndSelections[i].Key == missionPeer)
            {
                _peersAndSelections.RemoveAt(i);
                break;
            }
        }
    }

    protected override void HandlePlayerDisconnect(NetworkCommunicator networkPeer)
    {
        MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
        if (missionPeer != null)
        {
            (_spawnComponent.SpawningBehavior as AdimiToolsDuelSpawningBehavior)?.CancelInPlaceDuelRespawn(missionPeer);
            missionPeer.Team = null;
        }

        PlayerId playerId = networkPeer.VirtualPlayer.Id;
        if (PlayerDuelData.PlayerDuelList.ContainsKey(playerId))
        {
            PlayerDuelData.PlayerDuelList.Remove(playerId);
        }
    }

    protected override void HandleLateNewClientAfterLoadingFinished(NetworkCommunicator networkPeer)
    {
        PlayersDuelConfig.TryAdd(networkPeer.VirtualPlayer.Id, new DuelConfig(networkPeer));
        if (PlayersDuelConfig.TryGetValue(networkPeer.VirtualPlayer.Id, out DuelConfig? duelConfig))
        {
            duelConfig.DeathFrame = null;
            duelConfig.LastTeleportWarningTimer = MissionTime.Now;
        }
    }

    protected override void AddRemoveMessageHandlers(GameNetwork.NetworkMessageHandlerRegistererContainer registerer)
    {
        registerer.RegisterBaseHandler<NetworkMessages.FromClient.DuelRequest>(HandleClientEventDuelRequest);
        registerer.RegisterBaseHandler<DuelResponse>(HandleClientEventDuelRequestAccepted);
        registerer.RegisterBaseHandler<RequestChangePreferredTroopType>(HandleClientEventDuelRequestChangePreferredTroopType);
    }

    protected override void OnEndMission()
    {
        ClearDuelBoundaries();
        _aiDuelDisplay?.Clear();
        base.OnEndMission();
    }

    protected override void HandleEarlyNewClientAfterLoadingFinished(NetworkCommunicator networkPeer)
    {
        networkPeer.AddComponent<DuelMissionRepresentative>();
    }

    private static void RestoreAgent(Agent agent)
    {
        if (!AdimiToolsMountSafety.IsLiving(Mission.Current, agent))
        {
            return;
        }

        Agent? mount = agent.MountAgent;
        if (AdimiToolsMountSafety.IsLiving(Mission.Current, mount))
        {
            mount!.Health = mount.HealthLimit;
        }

        agent.Health = agent.HealthLimit; // Heal player
        agent.RestoreShieldHitPoints(); // Repair shield
        // Restock ammo - funny cause ranged weapons are not even enabled, but whatever..
        for (EquipmentIndex equipmentIndex = EquipmentIndex.WeaponItemBeginSlot; equipmentIndex < EquipmentIndex.NumAllWeaponSlots; equipmentIndex++)
        {
            if (!agent.Equipment[equipmentIndex].IsEmpty && (agent.Equipment[equipmentIndex].CurrentUsageItem.WeaponClass == WeaponClass.Arrow || agent.Equipment[equipmentIndex].CurrentUsageItem.WeaponClass == WeaponClass.Bolt) && agent.Equipment[equipmentIndex].Amount < agent.Equipment[equipmentIndex].ModifiedMaxAmount)
            {
                agent.SetWeaponAmountInSlot(equipmentIndex, agent.Equipment[equipmentIndex].ModifiedMaxAmount, true);
            }
        }
    }

    private static void ApplyDuelWinReward(DuelMissionRepresentative representative, float requestedScore)
    {
        int maximumScore = CompressionMatchmaker.ScoreCompressionInfo.GetMaximumValue();
        int maximumWins = CompressionMatchmaker.KillDeathAssistCountCompressionInfo.GetMaximumValue();
        if (representative.NumberOfWins < maximumWins)
        {
            float boundedScore = AdimiToolsDuelScorePolicy.LimitWinReward(requestedScore, representative.Score, representative.Bounty, maximumScore);
            representative.OnDuelWon(boundedScore);
        }
    }

    private static bool IsDuelPointsRepresentable(DuelMissionRepresentative representative)
    {
        CompressionInfo.Integer score = CompressionMatchmaker.ScoreCompressionInfo;
        CompressionInfo.Integer wins = CompressionMatchmaker.KillDeathAssistCountCompressionInfo;
        return AdimiToolsDuelScorePolicy.IsRepresentable(representative.Score, score.GetMaximumValue())
            && AdimiToolsDuelScorePolicy.IsRepresentable(representative.Bounty, score.GetMaximumValue())
            && AdimiToolsDuelScorePolicy.IsRepresentable(representative.NumberOfWins, wins.GetMaximumValue());
    }

    private static void PublishDuelPoints(DuelMissionRepresentative representative)
    {
        NetworkCommunicator? networkPeer = representative.GetNetworkPeer();
        if (networkPeer == null || !networkPeer.IsConnectionActive || !IsDuelPointsRepresentable(representative))
        {
            return;
        }

        try
        {
            GameNetwork.BeginBroadcastModuleEvent();
            try
            {
                GameNetwork.WriteMessage(new DuelPointsUpdateMessage(representative));
            }
            finally
            {
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
            }
        }
        catch (Exception exception)
        {
            // Stats already changed: packet failure must never repeat the award.
            AdimiToolsConsoleLog.Log($"Duel points broadcast failed: {exception.Message}");
        }
    }

    private bool HaveMatchingDuelLimits(MissionPeer requesterPeer, MissionPeer requesteePeer)
    {
        NetworkCommunicator? requesterNetworkPeer = requesterPeer.GetNetworkPeer();
        NetworkCommunicator? requesteeNetworkPeer = requesteePeer.GetNetworkPeer();
        if (!PlayersDuelConfig.TryGetValue(requesterPeer.Peer.Id, out DuelConfig? requesterConfig)
            || !PlayersDuelConfig.TryGetValue(requesteePeer.Peer.Id, out DuelConfig? requesteeConfig))
        {
            if (requesterNetworkPeer != null)
            {
                AdimiToolsNotifier.ServerSendMessageToPlayer(requesterNetworkPeer, "对局模式尚未准备好，请稍后重试。");
            }

            return false;
        }

        if (AdimiToolsDuelModes.CanDuel(requesterConfig.FirstToLimit, requesteeConfig.FirstToLimit))
        {
            return true;
        }

        string mismatch = AdimiToolsDuelGuidanceText.GetModeMismatch(
            requesterPeer.Name, AdimiToolsDuelModes.GetModeName(requesterConfig.FirstToLimit),
            requesteePeer.Name, AdimiToolsDuelModes.GetModeName(requesteeConfig.FirstToLimit));
        if (requesterNetworkPeer != null)
        {
            AdimiToolsNotifier.ServerSendGuidanceToPlayer(requesterNetworkPeer, mismatch, playSound: true);
        }

        if (requesteeNetworkPeer != null)
        {
            AdimiToolsNotifier.ServerSendGuidanceToPlayer(requesteeNetworkPeer, mismatch, playSound: true);
        }

        return false;
    }

    private bool AgentWeaponIsSheatedForTooLong(MissionPeer missionPeer, Agent? agent)
    {
        if (!PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig))
        {
            return false;
        }

        if (!CanUseDuelAgent(missionPeer, agent) || agent!.Health <= agent.HealthLimit * 0.5f
            || !agent.WieldedWeapon.IsEqualTo(MissionWeapon.Invalid) || agent.GetPrimaryWieldedItemIndex() != EquipmentIndex.None)
        {
            duelConfig.NoWeaponTreshold = 0;
            return false;
        }

        duelConfig.NoWeaponTreshold++;
        return duelConfig.NoWeaponTreshold >= 2;
    }

    private void CheckForSheatWeapon()
    {
        for (int i = _activeDuels.Count - 1; i >= 0; i--)
        {
            DuelInfo duelInfo = _activeDuels[i];
            if (!duelInfo.Started || _endingDuels.Contains(duelInfo))
            {
                continue;
            }

            Agent? requesterAgent = duelInfo.RequesterPeer.ControlledAgent;
            Agent? requesteeAgent = duelInfo.RequesteePeer.ControlledAgent;

            bool requesterStoppedDuel = AgentWeaponIsSheatedForTooLong(duelInfo.RequesterPeer, requesterAgent);
            bool requesteeStoppedDuel = AgentWeaponIsSheatedForTooLong(duelInfo.RequesteePeer, requesteeAgent);

            if (requesterStoppedDuel || requesteeStoppedDuel)
            {
                CancelRestartDuel(duelInfo);

                if (AdimiToolsMountSafety.IsLiving(Mission, requesterAgent))
                {
                    RestoreAgent(requesterAgent!);
                    AdimiToolsNotifier.ServerSendMessageToPlayer(duelInfo.RequesterPeer.GetNetworkPeer(), requesterStoppedDuel ? "You stopped the duel." : "Your enemy cancelled the duel.");
                }

                if (AdimiToolsMountSafety.IsLiving(Mission, requesteeAgent))
                {
                    RestoreAgent(requesteeAgent!);
                    AdimiToolsNotifier.ServerSendMessageToPlayer(duelInfo.RequesteePeer.GetNetworkPeer(), requesteeStoppedDuel ? "You stopped the duel." : "Your enemy cancelled the duel.");
                }

                continue;
            }
        }
    }

    private void TickDuelBoundaries()
    {
        double now = Mission.CurrentTime;
        foreach (var (duel, hold) in _duelBoundaries.ToArray())
        {
            if (now < hold.RetryAt)
            {
                continue;
            }

            try
            {
                if (!hold.Lowering && (hold.RecoveryPending || duel.ChallengeEnded || !duel.IsDuelStillValid(doNotCheckAgent: true)))
                {
                    hold.Boundary.Move(duel, raised: false, immediately: hold.RecoveryPending);
                    hold.Lowering = true;
                }

                hold.Boundary.Tick(duel, now);
                if (hold.Lowering && hold.Boundary.IsAtTarget(duel, raised: false))
                {
                    hold.Boundary.Release(duel);
                    _duelBoundaries.Remove(duel);
                }
            }
            catch (Exception exception)
            {
                hold.RecoveryPending = true;
                hold.Lowering = false;
                hold.RetryAt = now + 1d;
                AdimiToolsConsoleLog.Log($"1v1 boundary recovery pending: {exception.Message}");
            }
        }

        if (_activeDuels.Count == 0 || now < _nextDuelBoundaryCheck)
        {
            return;
        }

        _nextDuelBoundaryCheck = now + .25d;
        foreach (DuelInfo duel in _activeDuels)
        {
            if (!duel.Started || duel.ChallengeEnded || duel.BoundaryAttempted || _endingDuels.Contains(duel)
                || !duel.IsDuelStillValid(doNotCheckAgent: true))
            {
                continue;
            }

            try
            {
                Agent? first = duel.RequesterPeer.ControlledAgent;
                Agent? second = duel.RequesteePeer.ControlledAgent;
                if (!AdimiToolsMountSafety.IsLiving(Mission, first) || !AdimiToolsMountSafety.IsLiving(Mission, second))
                {
                    continue;
                }

                AdimiToolsSharedArena? boundary = AdimiToolsSharedArena.FindFreeDuelArena(Mission, GetDuelGround(first!), GetDuelGround(second!));
                if (boundary == null)
                {
                    continue;
                }

                MatrixFrame? spawn = boundary.DuelSpawn?.GetGlobalFrame();
                if (!boundary.TryAcquire(duel))
                {
                    continue;
                }

                duel.BoundaryAttempted = true;
                var hold = new DuelBoundaryHold(boundary, spawn);
                _duelBoundaries.Add(duel, hold);
                try
                {
                    boundary.Move(duel, raised: true);
                }
                catch
                {
                    hold.RecoveryPending = true;
                    throw;
                }
            }
            catch (Exception exception)
            {
                duel.BoundaryAttempted = true;
                AdimiToolsConsoleLog.Log($"1v1 boundary setup failed: {exception.Message}");
            }
        }
    }

    private Vec3 GetDuelGround(Agent agent)
    {
        Agent? mount = agent.MountAgent;
        return AdimiToolsMountSafety.IsLiving(Mission, mount) ? mount!.Position : agent.Position;
    }

    private MatrixFrame? GetDuelBoundaryRespawnFrame(DuelInfo duel)
    {
        if (duel.Started && !duel.ChallengeEnded && duel.FirstToLimit > 1
            && _duelBoundaries.TryGetValue(duel, out DuelBoundaryHold? hold) && !hold.RecoveryPending && !hold.Lowering)
        {
            return hold.RespawnFrame;
        }

        return null;
    }

    private MatrixFrame? FindClearDuelRespawn(DuelInfo duel, MatrixFrame spawn)
    {
        const float clearance = 3f;
        Vec3[] occupied = Mission.Agents.Where(agent => AdimiToolsMountSafety.IsLiving(Mission, agent))
            .Select(agent => agent.Position).ToArray();
        var offsets = from x in Enumerable.Range(-2, 5)
                      from y in Enumerable.Range(-2, 5)
                      orderby x * x + y * y
                      select new Vec3(x * clearance, y * clearance, 0f);
        foreach (Vec3 offset in offsets)
        {
            WorldPosition world = new(Mission.Scene, spawn.origin + offset);
            if (world.GetNavMesh() == UIntPtr.Zero)
            {
                continue;
            }

            Vec3 ground = world.GetNavMeshVec3();
            if (ground.IsValid && Math.Abs(ground.z - spawn.origin.z) <= 2f
                && _duelBoundaries[duel].Boundary.ContainsPoint(ground)
                && !occupied.Any(position => position.DistanceSquared(ground) < clearance * clearance))
            {
                spawn.origin = ground;
                return spawn;
            }
        }

        return null;
    }

    private void ClearDuelBoundaries()
    {
        foreach (var (duel, hold) in _duelBoundaries)
        {
            try
            {
                hold.Boundary.Move(duel, raised: false, immediately: true);
                hold.Boundary.Release(duel);
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"1v1 boundary teardown failed: {exception.Message}");
            }
        }

        _duelBoundaries.Clear();
        _nextDuelBoundaryCheck = 0d;
    }

    private void CheckForTeleport()
    {
        foreach (Agent agent in Mission.AllAgents)
        {
            if (agent.Team != Mission.Teams.Attacker || !agent.IsActive() || !agent.IsHuman || agent.MissionPeer == null)
            {
                continue;
            }

            Vec3 agentVec3 = agent.Position;

            foreach (DuelInfo activeDuel in _activeDuels)
            {
                if (!activeDuel.IsDuelStillValid() || !activeDuel.Started || activeDuel.RequesteePeer.ControlledAgent == null || activeDuel.RequesterPeer.ControlledAgent == null || !activeDuel.RequesterPeer.ControlledAgent.IsActive() || !activeDuel.RequesterPeer.ControlledAgent.IsActive() || activeDuel.RequesterPeer.ControlledAgent.Health == 0 || activeDuel.RequesteePeer.ControlledAgent.Health == 0)
                {
                    continue;
                }

                float dist1 = activeDuel.RequesteePeer.ControlledAgent.Position.Distance(agentVec3);
                float dist2 = activeDuel.RequesterPeer.ControlledAgent.Position.Distance(agentVec3);
                if (dist1 < AdimiToolsDuelInterference.EvictionDistance || dist2 < AdimiToolsDuelInterference.EvictionDistance)
                {
                    agent.TeleportToPosition(((AdimiToolsDuelSpawnFrame)_spawnComponent.SpawnFrameBehavior).GetRandomSpawnInCurrentSection(agentVec3).origin);
                    AdimiToolsNotifier.AdminAnnouncement(agent.MissionPeer.GetNetworkPeer(), "你距离正在决斗的玩家不足 1 米，已被遣返。", true);
                }
                else if (dist1 < AdimiToolsDuelInterference.WarningDistance || dist2 < AdimiToolsDuelInterference.WarningDistance)
                {
                    MissionPeer? missionPeer = agent.MissionPeer;
                    if (missionPeer != null)
                    {
                        DuelConfig duelConfig = PlayersDuelConfig.GetValueOrDefault(missionPeer.Peer.Id)!;
                        if (duelConfig != null && duelConfig.LastTeleportWarningTimer < MissionTime.Now)
                        {
                            duelConfig.LastTeleportWarningTimer = MissionTime.Now + MissionTime.Seconds(AdimiToolsDuelInterference.WarningIntervalSeconds);
                            AdimiToolsNotifier.AdminAnnouncement(agent.MissionPeer.GetNetworkPeer(), "请与正在决斗的玩家保持距离，靠近 1 米内将被遣返。", true);
                        }
                    }
                }
            }
        }
    }

    private bool HandleClientEventDuelRequestAccepted(NetworkCommunicator peer, GameNetworkMessage baseMessage)
    {
        DuelResponse duelResponse = (DuelResponse)baseMessage;
        if (duelResponse.Accepted && duelResponse.Peer?.GetComponent<MissionPeer>()?.ControlledAgent is Agent requester
            && peer?.GetComponent<MissionPeer>()?.ControlledAgent is Agent requestee)
        {
            DuelRequestAccepted(requester, requestee);
        }

        return true;
    }

    private bool HandleClientEventDuelRequestChangePreferredTroopType(NetworkCommunicator peer, GameNetworkMessage baseMessage)
    {
        RequestChangePreferredTroopType requestChangePreferredTroopType = (RequestChangePreferredTroopType)baseMessage;
        OnPeerSelectedPreferredTroopType(peer.GetComponent<MissionPeer>(), requestChangePreferredTroopType.TroopType);
        return true;
    }

    private bool HandleClientEventDuelRequest(NetworkCommunicator peer, GameNetworkMessage baseMessage)
    {
        NetworkMessages.FromClient.DuelRequest duelRequest = (NetworkMessages.FromClient.DuelRequest)baseMessage;
        MissionPeer? missionPeer = peer?.GetComponent<MissionPeer>();
        if (missionPeer != null)
        {
            Agent agentFromIndex = Mission.MissionNetworkHelper.GetAgentFromIndex(duelRequest.RequestedAgentIndex);
            if (agentFromIndex != null && agentFromIndex.IsActive() && agentFromIndex.MissionPeer != null)
            {
                DuelRequestReceived(missionPeer, agentFromIndex.MissionPeer);
            }
        }

        return true;
    }

    private Team ActivateAndGetDuelTeam()
    {
        if (_deactiveDuelTeams.Count <= 0)
        {
            return Mission.Teams.Add(BattleSideEnum.Defender, uint.MaxValue, uint.MaxValue, null, isPlayerGeneral: true, isPlayerSergeant: false, isSettingRelations: false);
        }

        return _deactiveDuelTeams.Dequeue();
    }

    private void DeactivateDuelTeam(Team team)
    {
        _deactiveDuelTeams.Enqueue(team);
    }

    private bool IsHavingDuel(MissionPeer peer)
    {
        return _activeDuels.AnyQ((DuelInfo d) => d.IsPeerInThisDuel(peer));
    }

    private bool CanUseDuelAgent(MissionPeer peer, Agent? agent)
        => AdimiToolsMountSafety.IsLiving(Mission, agent) && ReferenceEquals(agent!.MissionPeer, peer)
            && ReferenceEquals(peer.ControlledAgent, agent);

    private bool IsPeerBusyForMarkerUpdates(MissionPeer peer, bool includeRequests)
    {
        return _activeDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _endingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartPreparationDuels.Any(d => d.IsPeerInThisDuel(peer))
            || (includeRequests && _duelRequests.Any(d => d.IsPeerInThisDuel(peer)));
    }

    private bool IsPeerInAiChallengeForMarkerUpdates(MissionPeer peer)
    {
        // AI reservations have no genuine participant DuelStarted/DuelEnded
        // flow. Hide them explicitly through the marker policy instead of
        // treating them as real PvP participants whose UI must be left alone.
        return Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.IsPeerReserved(peer) == true
            || Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(peer) == true;
    }

    private Team? GetMatchTeamForMarkerUpdates(MissionPeer peer)
        => Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.GetMarkerTeam(peer)
            ?? Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.GetCoopMarkerTeam(peer);

    private bool IsThereARequestBetweenPeers(MissionPeer requesterAgent, MissionPeer requesteeAgent)
    {
        for (int i = 0; i < _duelRequests.Count; i++)
        {
            if (_duelRequests[i].IsPeerInThisDuel(requesterAgent) && _duelRequests[i].IsPeerInThisDuel(requesteeAgent))
            {
                return true;
            }
        }

        return false;
    }

    private void CheckDuelsToStart()
    {
        for (int num = _activeDuels.Count - 1; num >= 0; num--)
        {
            DuelInfo duelInfo = _activeDuels[num];
            if (!duelInfo.Started && duelInfo.Timer.IsPast && duelInfo.IsDuelStillValid())
            {
                StartDuel(duelInfo);
            }
        }
    }

    private void CheckDuelRequestTimeouts()
    {
        for (int num = _duelRequests.Count - 1; num >= 0; num--)
        {
            DuelInfo duelInfo = _duelRequests[num];
            if (duelInfo.Timer.IsPast)
            {
                _duelRequests.Remove(duelInfo);
            }
        }
    }

    private void CheckForRestartingDuels()
    {
        for (int num = _restartingDuels.Count - 1; num >= 0; num--)
        {
            DuelInfo duel = _restartingDuels[num];
            if (!duel.IsDuelStillValid(doNotCheckAgent: true))
            {
                CancelRestartDuel(duel);
                continue;
            }

            if (!AdimiToolsMountSafety.IsLiving(Mission, duel.RequesterPeer.ControlledAgent)
                || !AdimiToolsMountSafety.IsLiving(Mission, duel.RequesteePeer.ControlledAgent))
            {
                _restartingDuels.RemoveAt(num);
                if (!_restartPreparationDuels.Contains(duel))
                {
                    _restartPreparationDuels.Add(duel);
                }

                continue;
            }

            _duelRequests.Add(duel);
            PrepareDuel(duel);
            _restartingDuels.RemoveAt(num);
        }
    }

    private void CheckEndedDuels()
    {
        for (int num = _endingDuels.Count - 1; num >= 0; num--)
        {
            DuelInfo duelInfo = _endingDuels[num];
            if (duelInfo.Timer.IsPast)
            {
                EndDuel(duelInfo);
                _endingDuels.RemoveAt(num);
                if (!duelInfo.ChallengeEnded)
                {
                    _restartPreparationDuels.Add(duelInfo);
                }
            }
        }
    }

    private void CheckRestartPreparationDuels()
    {
        for (int num = _restartPreparationDuels.Count - 1; num >= 0; num--)
        {
            DuelInfo duelInfo = _restartPreparationDuels[num];
            if (!duelInfo.IsDuelStillValid(doNotCheckAgent: true))
            {
                CancelRestartDuel(duelInfo);
                continue;
            }

            Agent? controlledAgent = duelInfo.RequesterPeer.ControlledAgent;
            Agent? controlledAgent2 = duelInfo.RequesteePeer.ControlledAgent;
            if (AdimiToolsMountSafety.IsLiving(Mission, controlledAgent) && AdimiToolsMountSafety.IsLiving(Mission, controlledAgent2))
            {
                _restartPreparationDuels.RemoveAt(num);
                _restartingDuels.Add(duelInfo);
            }
        }
    }

    private void CancelRestartDuel(DuelInfo duel)
    {
        // Release the old series without deciding another round or touching
        // cached dead/deleted actors through the normal EndDuel path.
        _duelRequests.Remove(duel);
        _activeDuels.Remove(duel);
        _endingDuels.Remove(duel);
        _restartingDuels.Remove(duel);
        _restartPreparationDuels.Remove(duel);
        duel.CancelRestart();
        if (Mission.IsFinalized)
        {
            return;
        }

        Team? team = duel.DuelingTeam;
        bool IsReservedElsewhere(MissionPeer peer) => _activeDuels.Concat(_endingDuels).Concat(_restartingDuels).Concat(_restartPreparationDuels)
            .Any(other => other.RequesterPeer.Peer.Id == peer.Peer.Id || other.RequesteePeer.Peer.Id == peer.Peer.Id);
        foreach (MissionPeer peer in new[] { duel.RequesterPeer, duel.RequesteePeer })
        {
            if (IsReservedElsewhere(peer))
            {
                continue;
            }

            if (peer.GetNetworkPeer()?.IsConnectionActive == true && team != null && ReferenceEquals(peer.Team, team))
            {
                peer.Team = Mission.AttackerTeam;
                Agent? agent = peer.ControlledAgent;
                if (AdimiToolsMountSafety.IsLiving(Mission, agent) && ReferenceEquals(agent!.MissionPeer, peer)
                    && ReferenceEquals(agent.Team, team))
                {
                    agent.SetTeam(Mission.AttackerTeam, true);
                }
            }

            if (PlayerDuelData.PlayerDuelList.TryGetValue(peer.Peer.Id, out PlayerDuelData? saved)
                && ReferenceEquals(saved.MissionPeer, peer))
            {
                PlayerDuelData.PlayerDuelList.Remove(peer.Peer.Id);
                if (PlayersDuelConfig.TryGetValue(peer.Peer.Id, out DuelConfig? config)
                    && ReferenceEquals(config.NetworkPeer, peer.GetNetworkPeer()) && !saved.Equipment.IsEmpty())
                {
                    config.RespawnEquip = saved.Equipment;
                }
            }
        }

        if (team != null && !_deactiveDuelTeams.Contains(team)
            && !_activeDuels.Concat(_endingDuels).Concat(_restartingDuels).Concat(_restartPreparationDuels)
                .Any(other => ReferenceEquals(other.DuelingTeam, team)))
        {
            team.SetIsEnemyOf(team, isEnemyOf: false);
            DeactivateDuelTeam(team);
        }

        NetworkCommunicator? recipient = new[] { duel.RequesterPeer, duel.RequesteePeer }
            .Where(peer => !IsReservedElsewhere(peer)).Select(peer => peer.GetNetworkPeer())
            .FirstOrDefault(network => network?.IsConnectionActive == true);
        if (recipient != null)
        {
            try
            {
                GameNetwork.BeginBroadcastModuleEvent();
                try
                {
                    GameNetwork.WriteMessage(new DuelEnded(recipient));
                }
                finally
                {
                    GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
                }
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"1v1 cancellation notification failed: {exception.Message}");
            }

            _duelMarkerSync?.OnRealDuelEnded(duel.RequesterPeer, duel.RequesteePeer);
        }
    }

    private void PrepareDuel(DuelInfo duel)
    {
        _duelRequests.Remove(duel);
        if (!IsHavingDuel(duel.RequesteePeer) && !IsHavingDuel(duel.RequesterPeer))
        {
            _activeDuels.Add(duel);
            Team duelTeam = duel.Started ? duel.DuelingTeam! : ActivateAndGetDuelTeam();
            duel.OnDuelPreparation(duelTeam);
        }
        else
        {
            Debug.FailedAssert("IsHavingDuel(duel.RequesteePeer) || IsHavingDuel(duel.RequesterPeer)", "C:\\Develop\\MB3\\Source\\Bannerlord\\TaleWorlds.MountAndBlade\\Missions\\Multiplayer\\MissionNetworkLogics\\MultiplayerGameModeLogics\\ServerGameModeLogics\\MissionMultiplayerDuel.cs", "PrepareDuel", 707);
        }
    }

    private void StartDuel(DuelInfo duel)
    {
        duel.OnDuelStarted();
    }

    private void EndDuel(DuelInfo duel)
    {
        _activeDuels.Remove(duel);
        duel.OnDuelEnded();
        if (duel.ChallengeEnded)
        {
            TroopType troopType = TroopType.Invalid;
            MissionPeer challengeWinnerPeer = duel.ChallengeWinnerPeer!;
            if (challengeWinnerPeer?.ControlledAgent != null)
            {
                troopType = GetAgentTroopType(challengeWinnerPeer.ControlledAgent);
            }

            OnDuelEnded?.Invoke(challengeWinnerPeer!, troopType);
            DeactivateDuelTeam(duel.DuelingTeam!);
            HandleEndedChallenge(duel);
        }

        if (duel.TryGetCapturedMountReset(out Agent? winner, out Agent? mount)
            && duel.ChallengeLoserPeer is MissionPeer loser
            && PlayersDuelConfig.TryGetValue(loser.Peer.Id, out DuelConfig? loserConfig)
            && loserConfig.DeathFrame is MatrixFrame deathFrame)
        {
            (_spawnComponent.SpawningBehavior as AdimiToolsDuelSpawningBehavior)?.QueueCapturedMountReset(winner!, loser, mount!, deathFrame);
        }
    }

    private TroopType GetAgentTroopType(Agent requesterAgent)
    {
        TroopType result = TroopType.Invalid;
        switch (requesterAgent.Character.DefaultFormationClass)
        {
            case FormationClass.Infantry:
            case FormationClass.HeavyInfantry:
                result = TroopType.Infantry;
                break;
            case FormationClass.Ranged:
                result = TroopType.Ranged;
                break;
            case FormationClass.Cavalry:
            case FormationClass.HorseArcher:
            case FormationClass.LightCavalry:
            case FormationClass.HeavyCavalry:
                result = TroopType.Cavalry;
                break;
        }

        return result;
    }

    private void HandleEndedChallenge(DuelInfo duel)
    {
        MissionPeer? challengeWinnerPeer = duel.ChallengeWinnerPeer;
        MissionPeer? challengeLoserPeer = duel.ChallengeLoserPeer;
        if (challengeWinnerPeer != null && challengeLoserPeer != null)
        {
            DuelMissionRepresentative component = challengeWinnerPeer.GetComponent<DuelMissionRepresentative>();
            DuelMissionRepresentative component2 = challengeLoserPeer.GetComponent<DuelMissionRepresentative>();
            MultiplayerClassDivisions.MPHeroClass mPHeroClassForPeer = MultiplayerClassDivisions.GetMPHeroClassForPeer(challengeWinnerPeer, skipTeamCheck: true);
            MultiplayerClassDivisions.MPHeroClass mPHeroClassForPeer2 = MultiplayerClassDivisions.GetMPHeroClassForPeer(challengeLoserPeer, skipTeamCheck: true);

            float gainedScore = AdimiToolsDuelScorePolicy.CalculateWinReward(component2.Bounty, mPHeroClassForPeer.TroopCasualCost, mPHeroClassForPeer2.TroopCasualCost, component.NumberOfWins);
            ApplyDuelWinReward(component, gainedScore);
            PublishDuelPoints(component);

            component2.ResetBountyAndNumberOfWins();
            PublishDuelPoints(component2);
        }

        MissionPeer peerComponent = challengeWinnerPeer ?? duel.RequesterPeer;
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new DuelEnded(peerComponent.GetNetworkPeer()));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
        _duelMarkerSync?.OnRealDuelEnded(duel.RequesterPeer, duel.RequesteePeer);
    }

    private void OnPeerSelectedPreferredTroopType(MissionPeer missionPeer, TroopType troopType)
    {
        for (int i = 0; i < _peersAndSelections.Count; i++)
        {
            if (_peersAndSelections[i].Key == missionPeer)
            {
                _peersAndSelections[i] = new KeyValuePair<MissionPeer, TroopType>(missionPeer, troopType);
                break;
            }
        }
    }

    private void OnItemPickUp(Agent agent, SpawnedItemEntity spawnedItemEntity)
    {
        // AdimiToolsConsoleLog.Log("Picked up: " + spawnedItemEntity.WeaponName + " | " + spawnedItemEntity.WeaponCopy.Item.StringId);
        foreach (ItemSpawns spawnedItem in ItemSpawns.SpawnedList)
        {
            if (spawnedItem.SpawnItemEntity == spawnedItemEntity)
            {
                ItemSpawns.SpawnedList.Remove(spawnedItem);
                if (spawnedItem.Respawn == false)
                {
                    break;
                }

                // Yes, we replace the picked up item immediately, otherwise sometimes invisible items occure. Thanks TaleWorlds!
                // Only refresh the actual picked-up entity if it is already wielded.
                // Native pickup has already selected/merged the ammunition slot.
                // Re-equipping the wielded slot here would replace the bow with ammo.
                bool isAmmunition = spawnedItem.ItemObject.ItemType is ItemObject.ItemTypeEnum.Arrows or ItemObject.ItemTypeEnum.Bolts;
                HandIndex targetHandIndex = spawnedItem.ItemObject.ItemType == ItemObject.ItemTypeEnum.Shield ? HandIndex.OffHand : HandIndex.MainHand;
                EquipmentIndex wieldedItemIndex = targetHandIndex == HandIndex.MainHand
                    ? agent.GetPrimaryWieldedItemIndex()
                    : agent.GetOffhandWieldedItemIndex();
                if (!isAmmunition && wieldedItemIndex != EquipmentIndex.None
                    && agent.GetWeaponEntityFromEquipmentSlot(wieldedItemIndex) == spawnedItemEntity.GameEntity)
                {
                    // Yes, there are tons of simpler approaches, unfortunately there is a bug with Bannerlord atm which leads to invisible weapons for some players occosionally.
                    // This approach eliminates this issue the most of the times. I know it makes no sense.
                    Banner? shieldBanner = spawnedItem.ItemObject.ItemType == ItemObject.ItemTypeEnum.Shield
                        ? AdimiToolsDuelAppearance.CreateBanner(agent.MissionPeer?.Culture)
                        : null;
                    MissionWeapon missionWeapon = new(spawnedItem.ItemObject, null, shieldBanner);
                    agent.EquipWeaponWithNewEntity(wieldedItemIndex, ref missionWeapon);
                    // agent.WieldNextWeapon(HandIndex.MainHand, WeaponWieldActionType.InstantAfterPickUp);
                    agent.TryToWieldWeaponInSlot(wieldedItemIndex, WeaponWieldActionType.InstantAfterPickUp, false);
                }

                SpawnSpecificWeapon(spawnedItem.ItemObject, spawnedItem.SpawnFrame, spawnedItem.Parent, false);
                break;
            }
        }
    }

    private void InitSpawnedWeapons()
    {
        IEnumerable<GameEntity> gameEntities = Mission.Scene.FindEntitiesWithTag("adimi_spawnable_item");
        int entries = 1;
        foreach (GameEntity gameEntity in gameEntities)
        {
            if (!gameEntity.HasTag("adimi_spawnable_item"))
            {
                continue;
            }

            AdimiToolsConsoleLog.Log($"Checking entry {entries++}/{gameEntities.Count()}");
            GameEntity? parent = gameEntity.Parent;
            if (parent == null || !parent.HasTag("adimi_weaponspawn"))
            {
                AdimiToolsConsoleLog.Log(gameEntity.Name + " - Parent is null or is missing a tag.");
                continue;
            }

            ItemObject itemObject = MBObjectManager.Instance.GetObject<ItemObject>(gameEntity.Name);
            if (itemObject != null)
            {
                SpawnSpecificWeapon(itemObject, gameEntity.GetFrame(), gameEntity.Parent, false);
            }
            else
            {
                AdimiToolsConsoleLog.Log($"Weapon rack item not found: {gameEntity.Name} (rack: {parent.Name}).");
            }
        }

        foreach (int limit in AdimiToolsDuelModes.AnvilLimits)
        {
            foreach (GameEntity anvilEntity in AdimiToolsDuelModes.GetAnvilTags(limit).SelectMany(tag => Mission.Scene.FindEntitiesWithTag(tag)).Distinct())
            {
                if (AdimiToolsDuelModes.GetAnvilLimit(anvilEntity.Tags) == 0)
                {
                    AdimiToolsConsoleLog.Log($"Skipped anvil {anvilEntity.Name}: select exactly one best-of mode.");
                    continue;
                }

                if (anvilEntity.GetFirstScriptOfType<AdimiToolsDuelFt7Anvil>() == null)
                {
                    anvilEntity.CreateAndAddScriptComponent("AdimiToolsDuelFt7Anvil", callScriptCallbacks: true);
                    AdimiToolsConsoleLog.Log($"Added {AdimiToolsDuelModes.GetModeName(limit)} anvil");
                }
            }
        }

        foreach (GameEntity selector in Mission.Scene.FindEntitiesWithTag(AdimiToolsDuelModes.CycleTag))
        {
            if (AdimiToolsDuelModes.GetInteractionLimit(selector.Tags, 1) == 0)
            {
                AdimiToolsConsoleLog.Log($"Skipped mode selector {selector.Name}: do not mix cycle and fixed best-of tags.");
                continue;
            }

            if (selector.GetFirstScriptOfType<AdimiToolsDuelFt7Anvil>() == null)
            {
                selector.CreateAndAddScriptComponent("AdimiToolsDuelFt7Anvil", callScriptCallbacks: true);
                AdimiToolsConsoleLog.Log($"Added cycling best-of selector: {selector.Name}");
            }
        }
    }

    private void InitTeleportDoors()
    {
        _teleportDoorTargets.Clear();
        List<GameEntity> doors = Mission.Scene.FindEntitiesWithTag("teleport_door").ToList();
        AdimiToolsConsoleLog.Log($"InitTeleportDoors: found {doors.Count} doors; preserving native CastleGate and StandingPoint scripts.");
        foreach (GameEntity door in doors)
        {
            CastleGate? gate = door.GetFirstScriptOfType<CastleGate>();
            string[] routeTags = door.Tags.Where(tag => tag.StartsWith("teleport_door_", StringComparison.Ordinal)).Distinct().ToArray();
            int standingPointCount = gate?.StandingPoints.Count ?? 0;
            string context = $"Teleport door '{door.Name}' at {door.GlobalPosition}, routes [{string.Join(", ", routeTags)}], standing points {standingPointCount}";
            if (gate == null || gate.Id.Id < 0 || standingPointCount == 0)
            {
                AdimiToolsConsoleLog.Log(context + ": skipped; missing native CastleGate or standing points.");
                continue;
            }

            foreach (StandingPoint point in gate.StandingPoints)
            {
                _teleportDoorTargets[point] = null;
            }

            if (routeTags.Length != 1 || routeTags[0].Length == "teleport_door_".Length)
            {
                AdimiToolsConsoleLog.Log(context + ": skipped; keep exactly one numbered route tag.");
                continue;
            }

            string targetTag = routeTags[0];
            List<GameEntity> targets = Mission.Scene.FindEntitiesWithTag(targetTag)
                .Where(targetLocationEntity => !targetLocationEntity.HasTag("teleport_door") && targetLocationEntity.HasTag("teleport_target"))
                .ToList();
            if (targets.Count != 1 || targets[0].Tags.Count(tag => tag.StartsWith("teleport_door_", StringComparison.Ordinal)) != 1)
            {
                AdimiToolsConsoleLog.Log(context + $": skipped; expected one unambiguous teleport_target, found {targets.Count}.");
                continue;
            }

            GameEntity target = targets[0];
            foreach (StandingPoint point in gate.StandingPoints)
            {
                _teleportDoorTargets[point] = target;
            }

            AdimiToolsConsoleLog.Log(context + $": ready -> '{target.Name}' at {target.GlobalPosition}.");
            Vec3 scale = door.GetGlobalFrame().rotation.GetScaleVector();
            if (System.MathF.Abs(scale.x - scale.y) > 0.01f || System.MathF.Abs(scale.x - scale.z) > 0.01f)
            {
                AdimiToolsConsoleLog.Log(context + $": non-uniform scale {scale}; native physical focus may fail. Use uniform scale for the interactive gate root.");
            }
        }
    }

    private void SpawnSpecificWeapon(ItemObject itemObject, MatrixFrame frame, GameEntity parent, bool lifeTime, bool respawn = true)
    {
        MissionObject missionObject = parent.GetFirstScriptOfType<SynchedMissionObject>();
        // The native spawn API treats the frame as local only when a synced
        // parent is supplied. Plain scene racks need a world frame instead.
        // Keep the original local frame for subsequent weapon respawns.
        MatrixFrame spawnFrame = frame;
        if (missionObject == null)
        {
            spawnFrame = parent.GetGlobalFrame().TransformToParent(in frame);
            if (!spawnFrame.rotation.IsOrthonormal())
            {
                spawnFrame.rotation.Orthonormalize();
            }
        }

        // The stock constructor fills consumables to their stack capacity and
        // shields to their durability. A literal data value of 1 made single arrows.
        MissionWeapon missionWeapon = new(itemObject, null, null);
        bool isAmmunition = itemObject.ItemType is ItemObject.ItemTypeEnum.Arrows or ItemObject.ItemTypeEnum.Bolts;
        Mission.WeaponSpawnFlags flags = Mission.WeaponSpawnFlags.WithStaticPhysics;
        if (isAmmunition)
        {
            flags |= Mission.WeaponSpawnFlags.WithHolster;
        }
        else
        {
            flags |= Mission.WeaponSpawnFlags.WithoutHolster;
        }

        GameEntity placedEntity = Mission.Current.SpawnWeaponWithNewEntityAux(missionWeapon, flags, spawnFrame, -1, missionObject, lifeTime);
        _ = new ItemSpawns(itemObject, placedEntity.GetFirstScriptOfType<SpawnedItemEntity>(), frame, parent, respawn);
    }
}
