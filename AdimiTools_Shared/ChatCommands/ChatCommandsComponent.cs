using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using PlayerMessageAll = NetworkMessages.FromClient.PlayerMessageAll;
using PlayerMessageTeam = NetworkMessages.FromClient.PlayerMessageTeam;

namespace AdimiToolsShared.ChatCommands;

public class ChatCommandsComponent : GameHandler
{
    public const string CommandPrefix = "!";

    private readonly ChatCommand[] _commands;
    private readonly FirstToDuelCommand _firstToCommand;

    public ChatCommandsComponent()
    {
        _firstToCommand = new FirstToDuelCommand();
        _commands = new ChatCommand[]
        {
            _firstToCommand,
        };
    }

    public override void OnAfterSave()
    {
    }

    public override void OnBeforeSave()
    {
    }

    protected override void OnGameNetworkBegin()
    {
        base.OnGameNetworkBegin();
        AdimiToolsConsoleLog.Log("Registering Chat handlers...");
        AddRemoveMessageHandlers(GameNetwork.NetworkMessageHandlerRegisterer.RegisterMode.Add);
    }

    protected override void OnGameNetworkEnd()
    {
        base.OnGameNetworkEnd();
        AdimiToolsConsoleLog.Log("Unregistering Chat handlers...");
        AddRemoveMessageHandlers(GameNetwork.NetworkMessageHandlerRegisterer.RegisterMode.Remove);
    }

    private void AddRemoveMessageHandlers(GameNetwork.NetworkMessageHandlerRegisterer.RegisterMode mode)
    {
        if (!GameNetwork.IsServer)
        {
            return;
        }

        GameNetwork.NetworkMessageHandlerRegisterer handlerRegisterer = new(mode);
        handlerRegisterer.Register<PlayerMessageAll>(HandleClientEventPlayerMessageAll);
        handlerRegisterer.Register<PlayerMessageTeam>(HandleClientEventPlayerMessageTeam);
    }

    private bool HandleClientEventPlayerMessageAll(NetworkCommunicator peer, PlayerMessageAll message)
    {
        return HandleChatMessage(peer, message.Message);
    }

    private bool HandleClientEventPlayerMessageTeam(NetworkCommunicator peer, PlayerMessageTeam message)
    {
        return HandleChatMessage(peer, message.Message, true);
    }

    private bool HandleChatMessage(NetworkCommunicator sender, string message, bool teamMessage = false)
    {
        AdimiToolsConsoleLog.Log("HandleChatMessage");
        if (_firstToCommand.TryExecuteShortcut(sender, message))
        {
            _ = AdimiToolsLogManager.Instance.Log($"[CMD] {sender.UserName}: {message}");
            return false;
        }

        if (message.StartsWith("!") && HandleCommand(sender, message))
        {
            return false;
        }

        if (teamMessage)
        {
            var mp = sender.GetComponent<MissionPeer>();
            if (mp != null)
            {
                _ = AdimiToolsLogManager.Instance.Log($"[TEAMCHAT {mp.Team.Side}]|{sender.UserName}: {message}");
            }
        }
        else
        {
            _ = AdimiToolsLogManager.Instance.Log($"[CHAT] {sender.UserName}: {message}");
        }

        return true;
    }

    private bool HandleCommand(NetworkCommunicator sender, string message)
    {
        string[] tokens = message[1..].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        string name = tokens[0].ToLowerInvariant();
        var command = _commands.FirstOrDefault(c => c.Name == name);
        if (command == null)
        {
            foreach (var cmd in _commands)
            {
                if (cmd.Alias.Contains(name))
                {
                    command = cmd;
                    break;
                }
            }

            if (command == null)
            {
                return false;
            }
        }

        command.Execute(sender, tokens.Skip(1).ToArray());
        _ = AdimiToolsLogManager.Instance.Log($"[CMD] {sender.UserName}: {message}");
        return true;
    }
}
