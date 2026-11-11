using TaleWorlds.MountAndBlade;

namespace AdimiToolsShared.ChatCommands;

internal abstract class ChatCommand
{
    public string Name { get; protected set; } = string.Empty;
    public string[] Alias { get; protected set; } = Array.Empty<string>();

    /// <summary>A command can accepts several arguments types, the first one that matches the input is used.</summary>
    protected CommandOverload[] Overloads { get; set; } = Array.Empty<CommandOverload>();
    protected string Description { get; set; } = string.Empty;

    public void Execute(NetworkCommunicator fromPeer, string[] arguments)
    {
        if (!CheckRequirements(fromPeer))
        {
            AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer,
                AdimiDuel_Server.Duel.AdimiToolsMessageLanguage.Select("权限不足。", "Insufficient permissions."));
            return;
        }

        foreach (CommandOverload overload in Overloads)
        {
            if (!TryParseArguments(arguments, overload.ParameterTypes, out object[]? parsedArguments))
            {
                continue;
            }

            overload.Execute(fromPeer, parsedArguments!);
            return;
        }

        string usageMessage = AdimiDuel_Server.Duel.AdimiToolsMessageLanguage.IsEnglish
            ? $"Wrong usage. {Description}"
            : $"用法错误。{Description}";
        AdimiToolsNotifier.ServerSendMessageToPlayer(fromPeer, usageMessage);
    }

    // Used to check for permissions
    protected virtual bool CheckRequirements(NetworkCommunicator fromPeer)
    {
        return true;
    }

    private bool TryParseArguments(string[] arguments, ChatCommandParameterType[] expectedTypes, out object[]? parsedArguments)
    {
        parsedArguments = new object[expectedTypes.Length];
        if (arguments.Length < expectedTypes.Length)
        {
            return false;
        }

        for (int i = 0; i < expectedTypes.Length; i++)
        {
            switch (expectedTypes[i])
            {
                case ChatCommandParameterType.Int32:
                    if (!int.TryParse(arguments[i], out int parsedInt))
                    {
                        return false;
                    }

                    parsedArguments[i] = parsedInt;
                    break;

                case ChatCommandParameterType.Float32:
                    if (!float.TryParse(arguments[i], out float parsedFloat))
                    {
                        return false;
                    }

                    parsedArguments[i] = parsedFloat;
                    break;

                case ChatCommandParameterType.String:
                    parsedArguments[i] = i < expectedTypes.Length - 1
                        ? arguments[i]
                        : string.Join(" ", arguments.Skip(i));
                    break;

                case ChatCommandParameterType.TimeSpan:
                    if (!TryParseTimeSpan(arguments[i], out var timeSpan))
                    {
                        return false;
                    }

                    parsedArguments[i] = timeSpan;
                    break;

                case ChatCommandParameterType.PlayerId:
                    if (!TryParsePlayerId(arguments[i], out var networkPeer))
                    {
                        return false;
                    }

                    parsedArguments[i] = networkPeer!;
                    break;
            }
        }

        return true;
    }

    private bool TryParsePlayerId(string input, out NetworkCommunicator? networkPeer)
    {
        if (!int.TryParse(input, out int id))
        {
            networkPeer = null;
            return false;
        }

        foreach (NetworkCommunicator p in GameNetwork.NetworkPeers)
        {
            var missionPeer = p.GetComponent<MissionPeer>();
            if (p.IsSynchronized && p.Index == id)
            {
                networkPeer = p;
                return true;
            }
        }

        networkPeer = null;
        return false;
    }

    /// <summary>Parses input such as "15m". Unit supported s, m, h, d.</summary>
    private bool TryParseTimeSpan(string input, out TimeSpan timeSpan)
    {
        timeSpan = TimeSpan.Zero;
        if (input == "0")
        {
            return false;
        }

        if (input.Length < 2) // At least one number and a unit.
        {
            return false;
        }

        if (!int.TryParse(input[..^1], out int inputInt))
        {
            return false;
        }

        switch (input[^1])
        {
            case 's':
                timeSpan = TimeSpan.FromSeconds(inputInt);
                break;
            case 'm':
                timeSpan = TimeSpan.FromMinutes(inputInt);
                break;
            case 'h':
                timeSpan = TimeSpan.FromHours(inputInt);
                break;
            case 'd':
                timeSpan = TimeSpan.FromDays(inputInt);
                break;
            default:
                return false;
        }

        return true;
    }

    protected enum ChatCommandParameterType
    {
        Int32,
        Float32,
        String,
        TimeSpan,
        PlayerId,
    }

    protected class CommandOverload
    {
        public ChatCommandParameterType[] ParameterTypes { get; }
        public CommandFunc Execute { get; }

        public CommandOverload(ChatCommandParameterType[] parameterTypes, CommandFunc execute)
        {
            ParameterTypes = parameterTypes;
            Execute = execute;
        }

        public delegate void CommandFunc(NetworkCommunicator networkPeer, object[] parameters);
    }
}
