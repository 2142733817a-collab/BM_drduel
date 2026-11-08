using AdimiTools;
using AdimiToolsShared;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Multiplayer;
using TaleWorlds.MountAndBlade.Source.Missions;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsDuelGameMode : MissionBasedMultiplayerGameMode
{
    public const string GameName = "Duel";

    public AdimiToolsDuelGameMode()
        : base(GameName)
    {
        AdimiToolsConsoleLog.Log("Initalized AdimiToolsDuelGameMode");
    }

    public static void OpenAdimiDuelMission(string scene)
    {
        AdimiToolsConsoleLog.Log("OpenAdimiDuelMission");
        MissionState.OpenNew("MultiplayerDuel", new MissionInitializerRecord(scene), missionController =>
        {
            if (GameNetwork.IsServer)
            {
                var spawnComponent = new SpawnComponent(new AdimiToolsDuelSpawnFrame(), new AdimiToolsDuelSpawningBehavior());
                return new MissionBehavior[]
                {
                    AdimiToolsDuelLobbyComponent.CreateBehavior(),
                    new AdimiToolsMissionMultiplayerDuel(spawnComponent),
                    new AdimiToolsAiArenaBehavior(),
                    new AdimiToolsCultureSelectionBehavior(),
                    new AdimiToolsTeamBattleBehavior(),
                    new AdimiToolsTeamOrbSync(),
                    new AdimiToolsArenaDecalVisibility(),
                    new AdimiToolsDuelGuidanceBehavior(),
                    new MissionMultiplayerGameModeDuelClient(),
                    new MultiplayerTimerComponent(),
                    spawnComponent,
                    new MissionLobbyEquipmentNetworkComponent(),
                    new MissionHardBorderPlacer(),
                    new MissionBoundaryPlacer(),
                    new MissionBoundaryCrossingHandler(),
                    new MultiplayerPollComponent(),
                    new MultiplayerAdminComponent(),
                    new MultiplayerGameNotificationsComponent(),
                    new MissionOptionsComponent(),
                    new MissionScoreboardComponent(new DuelScoreboardData()),
                    new MissionAgentPanicHandler(),
                    new AgentHumanAILogic(),
                    new EquipmentControllerLeaveLogic(),
                    new MultiplayerPreloadHelper(),
                    new NotAllPlayersReadyComponent(),
                };
            }

            return new MissionBehavior[]
            {
                MissionLobbyComponent.CreateBehavior(),
                new MissionMultiplayerGameModeDuelClient(),
                new MultiplayerAchievementComponent(),
                new MultiplayerTimerComponent(),
                new MissionLobbyEquipmentNetworkComponent(),
                new MissionHardBorderPlacer(),
                new MissionBoundaryPlacer(),
                new MissionBoundaryCrossingHandler(),
                new MultiplayerPollComponent(),
                new MultiplayerAdminComponent(),
                new MultiplayerGameNotificationsComponent(),
                new MissionOptionsComponent(),
                new MissionScoreboardComponent(new DuelScoreboardData()),
                MissionMatchHistoryComponent.CreateIfConditionsAreMet(),
                new EquipmentControllerLeaveLogic(),
                new MissionRecentPlayersComponent(),
                new MultiplayerPreloadHelper(),
            };
        }, true, true);
    }

    public override void StartMultiplayerGame(string scene)
    {
        OpenAdimiDuelMission(scene);
    }
}
