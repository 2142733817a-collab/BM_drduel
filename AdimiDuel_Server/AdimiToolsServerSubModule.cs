using AdimiDuel_Server.Duel;
using AdimiToolsHelper;
using AdimiToolsShared;
using AdimiToolsShared.ChatCommands;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.DedicatedCustomServer;
using TaleWorlds.MountAndBlade.Diamond;
using TaleWorlds.MountAndBlade.ListedServer;

namespace AdimiDuel_Server;

public class AdimiToolsServerSubModule : MBSubModuleBase
{
    public override void OnGameInitializationFinished(Game game)
    {
        base.OnGameInitializationFinished(game);

        if (game.GetGameHandler<ChatCommandsComponent>() == null)
        {
            game.AddGameHandler<ChatCommandsComponent>();
        }

        InitialListedGameServerState.OnActivated += OnActivate;
    }

    public override void OnGameEnd(Game game)
    {
        DedicatedCustomServerSubModule.Instance.DedicatedCustomGameServer.FinishAsIdle(Array.Empty<GameLog>());

        if (game.GetGameHandler<ChatCommandsComponent>() != null)
        {
            game.RemoveGameHandler<ChatCommandsComponent>();
        }

        base.OnGameEnd(game);
    }

    protected override void OnSubModuleLoad()
    {
        AdimiToolsConsoleLog.Log("Initalized Adimi utils");
        Thread.GetDomain().UnhandledException += (sender, eventArgs) => OnExitServer((Exception?)eventArgs?.ExceptionObject);
        AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => OnExitServer(null);
    }

    protected override void OnGameStart(Game game, IGameStarter gameStarter)
    {
        base.OnGameStart(game, gameStarter);
        gameStarter.AddModel(new AdimiToolsAiArenaAgentStatModel());
        gameStarter.AddModel(new AdimiToolsAiArenaStrikeMagnitudeModel());
        gameStarter.AddModel(new AdimiToolsAiArenaApplyDamageModel());
    }

    private async void OnExitServer(Exception? ex)
    {
        if (ex != null)
        {
            AdimiToolsConsoleLog.Log("Server crashed!");
            _ = AdimiToolsLogManager.Instance.Log($"[SERVEREND] Error: {ex.Message}");
            _ = AdimiToolsLogManager.Instance.Log($"[SERVEREND] Stack: {ex.StackTrace}");
            _ = AdimiToolsLogManager.Instance.Log($"[SERVEREND] Stack: {ex.InnerException?.Message}");
            _ = AdimiToolsLogManager.Instance.Log($"[SERVEREND] Stack: {ex.InnerException?.StackTrace}");
        }
        else
        {
            AdimiToolsConsoleLog.Log("Server is shutting down..");
            _ = AdimiToolsLogManager.Instance.Log($"[SERVEREND] Server shutdown...");
        }

        DedicatedCustomServerSubModule.Instance.DedicatedCustomGameServer.FinishAsIdle(Array.Empty<GameLog>());
    }

    // Called as soon as config was read
    private void OnActivate()
    {
        var multiplayerGameModesWithNames = (Dictionary<string, MultiplayerGameMode>?)AdimiHelpers.GetField(Module.CurrentModule, "_multiplayerGameModesWithNames");
        var multiplayerGameTypes = (List<MultiplayerGameTypeInfo>?)AdimiHelpers.GetField(Module.CurrentModule, "_multiplayerGameTypes");
        // Add Warband like Duelsystem
        if (multiplayerGameTypes != null && (multiplayerGameModesWithNames?.Remove("Duel") ?? false))
        {
            foreach (MultiplayerGameTypeInfo gamemode in multiplayerGameTypes)
            {
                if (gamemode.GameType == "Duel")
                {
                    multiplayerGameTypes.Remove(gamemode);
                    break;
                }
            }

            AdimiToolsConsoleLog.Log("Removed: Native Duel");
            Module.CurrentModule.AddMultiplayerGameMode(new AdimiToolsDuelGameMode());
            AdimiToolsConsoleLog.Log("Added Adimi Duel");
        }
    }
}
