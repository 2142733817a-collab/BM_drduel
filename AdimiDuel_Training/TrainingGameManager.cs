using System;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using System.Collections.Generic;

namespace AdimiDuel_Training
{
    // CustomGameManager owns the isolated Game and initializes native models;
    // main menu must not call MissionState.OpenNew without a Game owner.
    internal sealed class TrainingGameManager : CustomGameManager
    {
        private bool _opened;
        private readonly bool _playerControlled;

        internal TrainingGameManager(bool playerControlled) { _playerControlled = playerControlled; }

        public override void OnLoadFinished()
        {
            if (_opened) return;
            _opened = true;
            IsLoaded = true;
            Game owned = null;
            try
            {
                if (!ReferenceEquals(MBGameManager.Current, this) || Game.Current == null
                    || !(Game.Current.GameType is CustomGame) || Mission.Current != null || GameNetwork.IsMultiplayer)
                    throw new InvalidOperationException("训练游戏上下文不可用。");
                owned = Game.Current;
                var initializer = new MissionInitializerRecord(SubModule.SceneId)
                {
                    PlayingInCampaignMode = false,
                    EnableSceneRecording = false,
                    DecalAtlasGroup = 2,
                };
                // Keep the native default views, including player input. The
                // bootstrap swaps only the default casualty handler before teams exist.
                MissionState.OpenNew("AdimiStandaloneTraining", initializer, mission => CreateBehaviors(mission), true, true);
                SubModule.Log("Standalone mission opened: " + SubModule.SceneId + "; player=" + _playerControlled);
            }
            catch (Exception error)
            {
                SubModule.Log("Standalone mission failed: " + error);
                SubModule.Notify("单机训练场景加载失败：" + error.Message);
                // Only tear down a game that this manager owns; never disturb a
                // campaign, an unrelated mission, or a partially opened mission.
                if (owned != null && ReferenceEquals(MBGameManager.Current, this)
                    && ReferenceEquals(Game.Current, owned) && Mission.Current == null)
                {
                    if (owned.GameStateManager != null && !owned.GameStateManager.GameStates.Any()) owned.Destroy();
                    else MBGameManager.EndGame();
                }
            }
            finally { SubModule.Loading = false; }
        }

        private MissionBehavior[] CreateBehaviors(Mission mission)
        {
            var power = new TrainingBattlePowerLogic();
            var casualties = new TrainingCasualtyHandler();
            var behaviors = new List<MissionBehavior>
            {
                new TrainingMissionBootstrap(casualties),
                new MissionBoundaryPlacer(),
                new AgentHumanAILogic(),
                power,
                new TrainingMissionLogic(_playerControlled, power, casualties),
                new BasicLeaveMissionLogic(),
                new MissionOptionsComponent(),
                ViewCreator.CreateOptionsUIHandler(),
                ViewCreator.CreateMissionSingleplayerEscapeMenu(false),
                ViewCreator.CreateMissionLeaveView(),
                ViewCreator.CreateMissionSpectatorControlView(mission),
            };
            if (!_playerControlled) behaviors.Add(new TrainingCameraView());
            return behaviors.ToArray();
        }
    }
}
