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

            public int KillCountInDuel { get; private set; }

            public Challenger(MissionPeer missionPeer)
            {
                MissionPeer = missionPeer;
                NetworkPeer = MissionPeer.GetNetworkPeer() ?? null;
                DuelingAgent = null;
                MountAgent = null;
                KillCountInDuel = 0;
            }

            public void OnDuelPreparation(Team duelingTeam)
            {
                // MissionPeer.ControlledAgent?.FadeOut(hideInstantly: true, hideMount: true); // We do not want to respawn the player. Just start the duel in place.
                MissionPeer.Team = duelingTeam;
                MissionPeer.ControlledAgent?.SetTeam(duelingTeam, true);
                MissionPeer.HasSpawnedAgentVisuals = true;
            }

            public void OnDuelEnded()
            {
                if (MissionPeer.Peer.Communicator.IsConnectionActive)
                {
                    MissionPeer.Team = Mission.Current.AttackerTeam;
                    MissionPeer.ControlledAgent?.SetTeam(Mission.Current.AttackerTeam, true);

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
                MountAgent = DuelingAgent.MountAgent;
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

                AdimiToolsNotifier.ServerSendMessageToPlayer(_challengers[0].NetworkPeer!, $"You are fighting against {_challengers[1].NetworkPeer!.UserName} in a first to {FirstToLimit}.");
                AdimiToolsNotifier.ServerSendMessageToPlayer(_challengers[1].NetworkPeer!, $"You are fighting against {_challengers[0].NetworkPeer!.UserName} in a first to {FirstToLimit}.");
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

            if (ChallengeEnded)
            {
                if (FirstToLimit > 1)
                {
                    MissionPeer winnerPeer = _winnerChallengerType == ChallengerType.Requester ? _challengers[0].MissionPeer : _challengers[1].MissionPeer;
                    MissionPeer loserPeer = _winnerChallengerType == ChallengerType.Requestee ? _challengers[0].MissionPeer : _challengers[1].MissionPeer;
                    int winnerScore = _winnerChallengerType == ChallengerType.Requester ? _challengers[0].KillCountInDuel : _challengers[1].KillCountInDuel;
                    int loserScore = _winnerChallengerType == ChallengerType.Requestee ? _challengers[0].KillCountInDuel : _challengers[1].KillCountInDuel;
                    int winnerDamage = _winnerChallengerType == ChallengerType.Requester ? RequesterDamageDealt : RequesteeDamageDealt;
                    int loserDamage = _winnerChallengerType == ChallengerType.Requestee ? RequesterDamageDealt : RequesteeDamageDealt;
                    if (winnerScore == FirstToLimit || loserScore == FirstToLimit)
                    {
                        AdimiToolsNotifier.AdminAnnouncement(null, $"{winnerPeer.Name} beat {loserPeer.Name} in a Ft{FirstToLimit}! | {winnerScore} - {loserScore} ({winnerDamage} - {loserDamage})", false);
                    }
                }
            }

            for (int i = 0; i < 2; i++)
            {
                if (ChallengeEnded)
                {
                    _challengers[i].OnDuelEnded();
                    Agent agent = _challengers[i].DuelingAgent ?? _challengers[i].MissionPeer.ControlledAgent;
                    // agent.FadeOut(hideInstantly: true, hideMount: false);
                    // Do not fade out winner. Heal him etc.
                    if (agent != null && agent.IsActive())
                    {
                        RestoreAgent(agent);
                    }
                    else
                    {
                        _challengers[i].MissionPeer.HasSpawnedAgentVisuals = true;
                    }
                }

                // Remove the horse of the loser
                if ((_challengers[i].DuelingAgent == null || !_challengers[i].DuelingAgent!.IsActive()) && _challengers[i].MountAgent != null && _challengers[i].MountAgent!.IsActive() && (ChallengeEnded || _challengers[i].MountAgent!.RiderAgent == null))
                {
                    _challengers[i].MountAgent!.FadeOut(hideInstantly: true, hideMount: false);
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

        public bool IsDuelStillValid(bool doNotCheckAgent = false)
        {
            for (int i = 0; i < 2; i++)
            {
                if (!_challengers[i].MissionPeer.Peer.Communicator.IsConnectionActive || (!doNotCheckAgent && !_challengers[i].MissionPeer.IsControlledAgentActive))
                {
                    return false;
                }
            }

            return true;
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
            bool isConnectionActive = _challengers[0].MissionPeer.Peer.Communicator.IsConnectionActive;
            bool isConnectionActive2 = _challengers[1].MissionPeer.Peer.Communicator.IsConnectionActive;
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
                if (duelingAgent != null && duelingAgent.IsActive())
                {
                    _winnerChallengerType = ChallengerType.Requester;
                }
                else if (duelingAgent2 != null && duelingAgent2.IsActive())
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
    private const int MinBountyGain = 100;
    private const float DuelRequestTimeOutServerToleranceInSeconds = 0.5f;
    private const float CorpseFadeOutTimeInSeconds = 10f;
    private readonly List<DuelInfo> _duelRequests;
    private readonly List<DuelInfo> _activeDuels;
    private readonly List<DuelInfo> _endingDuels;
    private readonly List<DuelInfo> _restartingDuels;
    private readonly List<DuelInfo> _restartPreparationDuels;
    private readonly Queue<Team> _deactiveDuelTeams;
    private readonly List<KeyValuePair<MissionPeer, TroopType>> _peersAndSelections;
    private readonly SpawnComponent _spawnComponent;
    private MissionTimer? _teleportCheckTimer;
    private MissionTimer? _sheetWeaponCheckTimer;
    private AdimiToolsDuelMarkerSync? _duelMarkerSync;

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
        _duelMarkerSync = new AdimiToolsDuelMarkerSync(Mission, IsPeerBusyForMarkerUpdates);
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
        if (Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.IsPeerReserved(missionPeer) == true)
        {
            return false;
        }

        for (int i = 0; i < _activeDuels.Count; i++)
        {
            if (_activeDuels[i].IsPeerInThisDuel(missionPeer))
            {
                return false;
            }
        }

        return true;
    }

    public void OnPlayerDespawn(MissionPeer missionPeer)
    {
        missionPeer.GetComponent<DuelMissionRepresentative>();
    }

    public void DuelRequestReceived(MissionPeer requesterPeer, MissionPeer requesteePeer)
    {
        AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        if (aiArena?.IsPeerReserved(requesterPeer) == true || aiArena?.IsPeerReserved(requesteePeer) == true)
        {
            return;
        }

        if (!HaveMatchingDuelLimits(requesterPeer, requesteePeer))
        {
            return;
        }

        if (!IsThereARequestBetweenPeers(requesterPeer, requesteePeer) && !IsHavingDuel(requesterPeer) && !IsHavingDuel(requesteePeer))
        {
            DuelInfo duelInfo = new(requesterPeer, requesteePeer, new KeyValuePair<int, TroopType>(0, TroopType.Infantry));
            _duelRequests.Add(duelInfo);
            (requesteePeer.Representative as DuelMissionRepresentative)?.DuelRequested(requesterPeer.ControlledAgent, duelInfo.DuelAreaTroopType);
        }
    }

    public void DuelRequestAccepted(Agent requesterAgent, Agent requesteeAgent)
    {
        AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        if (aiArena?.IsPeerReserved(requesterAgent.MissionPeer) == true || aiArena?.IsPeerReserved(requesteeAgent.MissionPeer) == true)
        {
            return;
        }

        DuelInfo? duelInfo = _duelRequests.FirstOrDefault((DuelInfo di) => di.IsPeerInThisDuel(requesterAgent.MissionPeer) && di.IsPeerInThisDuel(requesteeAgent.MissionPeer));
        if (duelInfo != null)
        {
            // Either player may have changed mode while this request was pending.
            if (!HaveMatchingDuelLimits(requesterAgent.MissionPeer, requesteeAgent.MissionPeer))
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

        if (_teleportCheckTimer?.Check(true) ?? false)
        {
            CheckForTeleport();
        }

        _duelMarkerSync?.Tick(dt);
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

        if (affectedAgent.MissionPeer.Team.IsDefender)
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
    }

    internal bool IsPeerBusyForAiChallenge(MissionPeer peer)
    {
        // Deliberately excludes AI reservations: the owner can add hits during
        // their countdown while all real PvP lifecycle stages remain blocked.
        return _activeDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _endingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartPreparationDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _duelRequests.Any(d => d.IsPeerInThisDuel(peer));
    }

    internal int GetFirstToLimitForAiChallenge(MissionPeer peer)
    {
        return PlayersDuelConfig.TryGetValue(peer.Peer.Id, out DuelConfig? config) ? config.FirstToLimit : 1;
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
            if (duelMissionRepresentative != null)
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
        }
    }

    protected override void HandleEarlyPlayerDisconnect(NetworkCommunicator networkPeer)
    {
        MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
        if (missionPeer != null)
        {
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
            AdimiToolsNotifier.ServerSendMessageToPlayer(networkPeer, $"You are in a first to {duelConfig.FirstToLimit} mode. Use !ft 1-10 or hit an anvil to change it.");
        }
    }

    protected override void AddRemoveMessageHandlers(GameNetwork.NetworkMessageHandlerRegistererContainer registerer)
    {
        registerer.RegisterBaseHandler<NetworkMessages.FromClient.DuelRequest>(HandleClientEventDuelRequest);
        registerer.RegisterBaseHandler<DuelResponse>(HandleClientEventDuelRequestAccepted);
        registerer.RegisterBaseHandler<RequestChangePreferredTroopType>(HandleClientEventDuelRequestChangePreferredTroopType);
    }

    protected override void HandleEarlyNewClientAfterLoadingFinished(NetworkCommunicator networkPeer)
    {
        networkPeer.AddComponent<DuelMissionRepresentative>();
    }

    private static void RestoreAgent(Agent agent)
    {
        if (agent.MountAgent != null)
        {
            agent.MountAgent.Health = agent.MountAgent.HealthLimit;
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

    private bool HaveMatchingDuelLimits(MissionPeer requesterPeer, MissionPeer requesteePeer)
    {
        NetworkCommunicator? requesterNetworkPeer = requesterPeer.GetNetworkPeer();
        NetworkCommunicator? requesteeNetworkPeer = requesteePeer.GetNetworkPeer();
        if (!PlayersDuelConfig.TryGetValue(requesterPeer.Peer.Id, out DuelConfig? requesterConfig)
            || !PlayersDuelConfig.TryGetValue(requesteePeer.Peer.Id, out DuelConfig? requesteeConfig))
        {
            if (requesterNetworkPeer != null)
            {
                AdimiToolsNotifier.ServerSendMessageToPlayer(requesterNetworkPeer, "Duel mode is not ready. Please try again.");
            }

            return false;
        }

        if (AdimiToolsDuelModes.CanDuel(requesterConfig.FirstToLimit, requesteeConfig.FirstToLimit))
        {
            return true;
        }

        if (requesterNetworkPeer != null)
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(requesterNetworkPeer, $"{requesteePeer.Name} is in first to {requesteeConfig.FirstToLimit}; you are in first to {requesterConfig.FirstToLimit}. Both players must select the same limit. Use !ft {requesteeConfig.FirstToLimit} or hit the matching anvil.");
        }

        if (requesteeNetworkPeer != null)
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(requesteeNetworkPeer, $"{requesterPeer.Name} is in first to {requesterConfig.FirstToLimit}; you are in first to {requesteeConfig.FirstToLimit}. Both players must select the same limit. Use !ft {requesterConfig.FirstToLimit} or hit the matching anvil.");
        }

        return false;
    }

    private bool AgentWeaponIsSheatedForTooLong(MissionPeer missionPeer, Agent agent)
    {
        if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig))
        {
            EquipmentIndex wieldedItemIndex = agent.GetPrimaryWieldedItemIndex();
            if (wieldedItemIndex == EquipmentIndex.None)
            {
                duelConfig.NoWeaponTreshold++;
                if (duelConfig.NoWeaponTreshold > 10)
                {
                    duelConfig.NoWeaponTreshold = 5;
                }

                return duelConfig.NoWeaponTreshold >= 2;
            }
        }

        return false;
    }

    private void CheckForSheatWeapon()
    {
        for (int i = _activeDuels.Count - 1; i >= 0; i--)
        {
            DuelInfo duelInfo = _activeDuels[i];
            Agent? requesterAgent = duelInfo.RequesterPeer.ControlledAgent;
            Agent? requesteeAgent = duelInfo.RequesteePeer.ControlledAgent;

            bool requesterStoppedDuel = requesterAgent != null && requesterAgent.IsActive() && (requesterAgent.Health / requesterAgent.HealthLimit) > 0.5f && requesterAgent.WieldedWeapon.IsEqualTo(MissionWeapon.Invalid) && AgentWeaponIsSheatedForTooLong(duelInfo.RequesterPeer, requesterAgent);
            bool requesteeStoppedDuel = requesteeAgent != null && requesteeAgent.IsActive() && (requesteeAgent.Health / requesteeAgent.HealthLimit) > 0.5f && requesteeAgent.WieldedWeapon.IsEqualTo(MissionWeapon.Invalid) && AgentWeaponIsSheatedForTooLong(duelInfo.RequesteePeer, requesteeAgent);

            if (requesterStoppedDuel || requesteeStoppedDuel)
            {
                // _endingDuels.Add(duelInfo);
                duelInfo.OnDuelEnding();
                EndDuel(duelInfo, true);

                if (requesterAgent != null)
                {
                    AdimiToolsNotifier.ServerSendMessageToPlayer(requesterAgent.MissionPeer.GetNetworkPeer(), requesterStoppedDuel ? "You stopped the duel." : "Your enemy cancelled the duel.");
                }

                if (requesteeAgent != null)
                {
                    AdimiToolsNotifier.ServerSendMessageToPlayer(requesteeAgent.MissionPeer.GetNetworkPeer(), requesteeStoppedDuel ? "You stopped the duel." : "Your enemy cancelled the duel.");
                }

                continue;
            }
        }
    }

    private void CheckForTeleport()
    {
        foreach (Agent agent in Mission.AllAgents)
        {
            if (agent.Team != Mission.Teams.Attacker || !agent.IsActive())
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
                if (dist1 < 2.5f || dist2 < 2.5f)
                {
                    agent.TeleportToPosition(((AdimiToolsDuelSpawnFrame)_spawnComponent.SpawnFrameBehavior).GetRandomSpawnInCurrentSection(agentVec3).origin);
                    AdimiToolsNotifier.AdminAnnouncement(agent.MissionPeer.GetNetworkPeer(), "Please keep your distance from ongoing duels!", true);
                }
                else if (dist1 < 4f || dist2 < 4f)
                {
                    MissionPeer? missionPeer = agent.MissionPeer;
                    if (missionPeer != null)
                    {
                        DuelConfig duelConfig = PlayersDuelConfig.GetValueOrDefault(missionPeer.Peer.Id)!;
                        if (duelConfig != null && duelConfig.LastTeleportWarningTimer < MissionTime.Now)
                        {
                            duelConfig.LastTeleportWarningTimer = MissionTime.Now + MissionTime.Seconds(5); // Print msg every 5sec if the player is too close.
                            AdimiToolsNotifier.AdminAnnouncement(agent.MissionPeer.GetNetworkPeer(), "Please keep your distance from ongoing duels or you will be teleported!", true);
                        }
                    }
                }
            }
        }
    }

    private bool HandleClientEventDuelRequestAccepted(NetworkCommunicator peer, GameNetworkMessage baseMessage)
    {
        DuelResponse duelResponse = (DuelResponse)baseMessage;
        if (peer?.GetComponent<MissionPeer>() != null && peer.GetComponent<MissionPeer>().ControlledAgent != null && duelResponse.Peer?.GetComponent<MissionPeer>() != null && duelResponse.Peer.GetComponent<MissionPeer>().ControlledAgent != null)
        {
            DuelRequestAccepted(duelResponse.Peer.GetComponent<DuelMissionRepresentative>().ControlledAgent, peer.GetComponent<DuelMissionRepresentative>().ControlledAgent);
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

    private bool IsPeerBusyForMarkerUpdates(MissionPeer peer, bool includeRequests)
    {
        return _activeDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _endingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartingDuels.Any(d => d.IsPeerInThisDuel(peer))
            || _restartPreparationDuels.Any(d => d.IsPeerInThisDuel(peer))
            || (includeRequests && _duelRequests.Any(d => d.IsPeerInThisDuel(peer)))
            || Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.IsPeerReserved(peer) == true;
    }

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
                Debug.Print("!_restartingDuels[i].IsDuelStillValid(true)");
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
            Agent controlledAgent = duelInfo.RequesterPeer.ControlledAgent;
            Agent controlledAgent2 = duelInfo.RequesteePeer.ControlledAgent;
            if ((controlledAgent != null && controlledAgent.IsActive()) && (controlledAgent2 != null && controlledAgent2.IsActive()))
            {
                _restartPreparationDuels.RemoveAt(num);
                _restartingDuels.Add(duelInfo);
            }
            else
            {
                bool isConnectionActive = duelInfo.RequesterPeer?.GetNetworkPeer()?.IsConnectionActive ?? false;
                bool isConnectionActive2 = duelInfo.RequesteePeer?.GetNetworkPeer()?.IsConnectionActive ?? false;
                if (!isConnectionActive || !isConnectionActive2)
                {
                    _restartPreparationDuels.Remove(duelInfo);
                    duelInfo.OnDuelEnding();
                    EndDuel(duelInfo, true);
                }
            }
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

    private void EndDuel(DuelInfo duel, bool forceEnd = false)
    {
        if (forceEnd)
        {
            duel.ChallengeEnded = true;
        }

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
            HandleEndedChallenge(duel, forceEnd);
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

    private void HandleEndedChallenge(DuelInfo duel, bool forceEnd = false)
    {
        MissionPeer? challengeWinnerPeer = duel.ChallengeWinnerPeer;
        MissionPeer? challengeLoserPeer = duel.ChallengeLoserPeer;
        if (challengeWinnerPeer != null && challengeLoserPeer != null)
        {
            DuelMissionRepresentative component = challengeWinnerPeer.GetComponent<DuelMissionRepresentative>();
            DuelMissionRepresentative component2 = challengeLoserPeer.GetComponent<DuelMissionRepresentative>();
            MultiplayerClassDivisions.MPHeroClass mPHeroClassForPeer = MultiplayerClassDivisions.GetMPHeroClassForPeer(challengeWinnerPeer, skipTeamCheck: true);
            MultiplayerClassDivisions.MPHeroClass mPHeroClassForPeer2 = MultiplayerClassDivisions.GetMPHeroClassForPeer(challengeLoserPeer, skipTeamCheck: true);

            if (!forceEnd)
            {
                float gainedScore = TaleWorlds.Library.MathF.Max(MinBountyGain, component2.Bounty) * TaleWorlds.Library.MathF.Max(1f, (float)mPHeroClassForPeer.TroopCasualCost / mPHeroClassForPeer2.TroopCasualCost) * TaleWorlds.Library.MathF.Pow(System.MathF.E, component.NumberOfWins / 10f);
                component.OnDuelWon(gainedScore);
            }

            if (challengeWinnerPeer.Peer.Communicator.IsConnectionActive)
            {
                GameNetwork.BeginBroadcastModuleEvent();
                GameNetwork.WriteMessage(new DuelPointsUpdateMessage(component));
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
            }

            component2.ResetBountyAndNumberOfWins();
            if (challengeLoserPeer.Peer.Communicator.IsConnectionActive)
            {
                GameNetwork.BeginBroadcastModuleEvent();
                GameNetwork.WriteMessage(new DuelPointsUpdateMessage(component2));
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
            }
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

                int maxHp = 1;
                // Yes, we replace the picked up item immediately, otherwise sometimes invisible items occure. Thanks TaleWorlds!
                HandIndex targetHandIndex = spawnedItem.ItemObject.ItemType == ItemObject.ItemTypeEnum.Shield ? HandIndex.OffHand : HandIndex.MainHand;
                EquipmentIndex wieldedItemIndex = targetHandIndex == HandIndex.MainHand
                    ? agent.GetPrimaryWieldedItemIndex()
                    : agent.GetOffhandWieldedItemIndex();
                if (wieldedItemIndex != EquipmentIndex.None)
                {
                    if (spawnedItem.ItemObject.Weapons != null && spawnedItem.ItemObject.ItemType == ItemObject.ItemTypeEnum.Shield)
                    {
                        foreach (WeaponComponentData weapon in spawnedItem.ItemObject.Weapons)
                        {
                            maxHp = weapon.MaxDataValue;
                        }
                    }

                    // Yes, there are tons of simpler approaches, unfortunately there is a bug with Bannerlord atm which leads to invisible weapons for some players occosionally.
                    // This approach eliminates this issue the most of the times. I know it makes no sense.
                    MissionWeapon missionWeapon = new(spawnedItem.ItemObject, null, null, (short)(spawnedItem.ItemObject.ItemType != ItemObject.ItemTypeEnum.Shield ? 1 : maxHp));
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
        }

        foreach (int limit in AdimiToolsDuelModes.AnvilLimits)
        {
            foreach (GameEntity anvilEntity in Mission.Scene.FindEntitiesWithTag(AdimiToolsDuelModes.GetAnvilTag(limit)))
            {
                if (AdimiToolsDuelModes.GetAnvilLimit(anvilEntity.Tags) == 0)
                {
                    AdimiToolsConsoleLog.Log($"Skipped anvil {anvilEntity.Name}: select exactly one FT3, FT5 or FT7 tag.");
                    continue;
                }

                if (anvilEntity.GetFirstScriptOfType<AdimiToolsDuelFt7Anvil>() == null)
                {
                    anvilEntity.CreateAndAddScriptComponent("AdimiToolsDuelFt7Anvil", callScriptCallbacks: true);
                    AdimiToolsConsoleLog.Log($"Added ft{limit} anvil");
                }
            }
        }
    }

    private void InitTeleportDoors()
    {
        AdimiToolsConsoleLog.Log("InitTeleportDoors");
        IEnumerable<GameEntity> gameEntities = Mission.Scene.FindEntitiesWithTag("teleport_door");
        AdimiToolsConsoleLog.Log("Found: " + gameEntities.Count());
        foreach (GameEntity door in gameEntities)
        {
            // Fetch the "CastleGate" script which is used client side to enable us to interact native compatible with the door
            CastleGate gateScript = door.GetFirstScriptOfType<CastleGate>();
            int missionObjectId = gateScript.Id.Id; // Save the mission object id of the castle gate
            gateScript.Id = MissionObjectId.Invalid;
            door.RemoveScriptComponent(gateScript.ScriptComponent.Pointer, 0);

            // Create a new fake usable object
            door.CreateAndAddScriptComponent("AdimiToolsTeleportDoors", callScriptCallbacks: true);
            AdimiToolsTeleportDoors teleportDoorScript = door.GetFirstScriptOfType<AdimiToolsTeleportDoors>();
            teleportDoorScript.Id = new MissionObjectId(missionObjectId); // Set the old original castle gate Mission Object Id as teleport door id -> Client packets will end up in this method instead.

            MBList<StandingPoint> standingPoints = door.CollectScriptComponentsIncludingChildrenRecursive<StandingPoint>();
            foreach (StandingPoint standingPoint in standingPoints)
            {
                WeakGameEntity standingPointGameEntity = standingPoint.GameEntity;
                int standingPointMissionObjectId = standingPoint.Id.Id;
                standingPoint!.Id = MissionObjectId.Invalid;
                standingPointGameEntity!.RemoveScriptComponent(standingPoint.ScriptComponent.Pointer, 0);
                standingPointGameEntity.CreateAndAddScriptComponent("AdimiToolsTeleportDoorStandingPoint", callScriptCallbacks: true);
                AdimiToolsTeleportDoorStandingPoint teleportDoorStandingPoint = standingPointGameEntity.GetFirstScriptOfType<AdimiToolsTeleportDoorStandingPoint>();
                teleportDoorStandingPoint.SetParentDoor(door);
                teleportDoorStandingPoint.Id = new MissionObjectId(standingPointMissionObjectId);
            }

            string? targetTag = null;
            foreach (string tag in door.Tags)
            {
                if (tag.StartsWith("teleport_door_"))
                {
                    targetTag = tag;
                    break;
                }
            }

            if (targetTag == null)
            {
                continue;
            }

            IEnumerable<GameEntity> entities = Mission.Scene.FindEntitiesWithTag(targetTag);
            foreach (GameEntity targetLocationEntity in entities)
            {
                if (targetLocationEntity.HasTag("teleport_door") || !targetLocationEntity.HasTag("teleport_target"))
                {
                    continue;
                }

                teleportDoorScript.SetupDoor(targetLocationEntity);
                break;
            }
        }
    }

    private void SpawnSpecificWeapon(ItemObject itemObject, MatrixFrame frame, GameEntity parent, bool lifeTime, bool respawn = true)
    {
        MissionObject missionObject = parent.GetFirstScriptOfType<SynchedMissionObject>();

        int maxHp = 1;
        if (itemObject.Weapons != null && itemObject.ItemType == ItemObject.ItemTypeEnum.Shield)
        {
            foreach (WeaponComponentData weapon in itemObject.Weapons)
            {
                maxHp = weapon.MaxDataValue;
            }
        }

        MissionWeapon missionWeapon = new(itemObject, null, null, (short)(itemObject.ItemType != ItemObject.ItemTypeEnum.Shield ? 1 : maxHp));
        GameEntity placedEntity = Mission.Current.SpawnWeaponWithNewEntityAux(missionWeapon, Mission.WeaponSpawnFlags.WithStaticPhysics | Mission.WeaponSpawnFlags.WithoutHolster, frame, -1, missionObject, lifeTime);
        _ = new ItemSpawns(itemObject, placedEntity.GetFirstScriptOfType<SpawnedItemEntity>(), frame, parent, respawn);
    }
}
