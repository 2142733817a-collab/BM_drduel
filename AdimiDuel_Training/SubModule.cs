using System;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Localization;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using MBModule = TaleWorlds.MountAndBlade.Module;

namespace AdimiDuel_Training
{
    // This module is deliberately a separate singleplayer module. It does not
    // add behaviors to campaigns, multiplayer missions, or a running server.
    public sealed class SubModule : MBSubModuleBase
    {
        internal const string SceneId = "ai_training_duel_empire_day";
        private const string ObserveMenuId = "adimi_training_observe";
        private const string ChallengeMenuId = "adimi_training_challenge";
        internal static bool Loading;

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            if (MBModule.CurrentModule == null) return;
            if (!MBModule.CurrentModule.GetInitialStateOptions().Any(o => o.Id == ObserveMenuId))
                MBModule.CurrentModule.AddInitialStateOption(new InitialStateOption(ObserveMenuId, new TextObject("AI 训练 · 观看"), 5,
                    () => StartTraining(false), Disabled));
            if (!MBModule.CurrentModule.GetInitialStateOptions().Any(o => o.Id == ChallengeMenuId))
                MBModule.CurrentModule.AddInitialStateOption(new InitialStateOption(ChallengeMenuId, new TextObject("AI 训练 · 亲自挑战"), 6,
                    () => StartTraining(true), Disabled));
        }

        private static ValueTuple<bool, TextObject> Disabled() => new ValueTuple<bool, TextObject>(Game.Current != null || Loading,
            new TextObject("请先返回游戏主菜单。"));

        private static void StartTraining(bool playerControlled)
        {
            if (Game.Current != null || Mission.Current != null || GameNetwork.IsMultiplayer || Loading)
            {
                Notify("请先结束当前游戏，再进入单机训练。");
                return;
            }
            try
            {
                string scenePath;
                if (!Utilities.GetModulesNames().Contains("AdimiDuel_Training", StringComparer.Ordinal)
                    || !Utilities.GetSingleModuleScenesOfModule("AdimiDuel_Training").Contains(SceneId, StringComparer.Ordinal)
                    || !Utilities.TryGetFullFilePathOfScene(SceneId, out scenePath))
                    throw new InvalidOperationException("训练地图未加载；请检查训练模块及其场景文件。");
                Loading = true;
                MBGameManager.StartNewGame(new TrainingGameManager(playerControlled));
            }
            catch (Exception error)
            {
                Loading = false;
                Log("Could not start standalone training: " + error);
                Notify("单机训练启动失败：" + error.Message);
            }
        }

        internal static void Notify(string message) => InformationManager.DisplayMessage(new InformationMessage(message));
        internal static void Log(string message) => TaleWorlds.Library.Debug.Print("[Adimi Training] " + message);
    }
}
