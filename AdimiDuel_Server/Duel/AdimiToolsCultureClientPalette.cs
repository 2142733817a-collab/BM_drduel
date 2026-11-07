using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Network.Messages;
using TaleWorlds.ObjectSystem;

namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsCultureClientPalette
{
    private static int _applying;

    // Call on the mission thread, before changing this peer's culture and
    // creating its new loadout. Native's client derives preview/UI colors
    // from two local room-culture options rather than the peer's own palette.
    internal static bool ApplyToPlayer(NetworkCommunicator peer, string cultureId)
    {
        if (!GameNetwork.IsServer || peer == null || peer.IsServerPeer
            || !peer.IsConnectionActive || !peer.IsSynchronized
            || Mission.Current == null || Mission.Current.IsFinalized
            || string.IsNullOrWhiteSpace(cultureId)
            || Utilities.GetCurrentThreadId() != Utilities.GetMainThreadId()
            || Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            if (MBObjectManager.Instance.GetObject<BasicCultureObject>(cultureId) == null)
            {
                return false;
            }

            string originalFirst = MultiplayerOptions.OptionType.CultureTeam1.GetStrValue();
            string originalSecond = MultiplayerOptions.OptionType.CultureTeam2.GetStrValue();
            string originalWelcome = MultiplayerOptions.OptionType.WelcomeMessage.GetStrValue();
            string? second = new[] { originalSecond, originalFirst }
                .Concat(MBObjectManager.Instance.GetObjectTypeList<BasicCultureObject>().Select(culture => culture.StringId))
                .FirstOrDefault(id => id != cultureId && !string.IsNullOrEmpty(id)
                    && MBObjectManager.Instance.GetObject<BasicCultureObject>(id) != null);
            if (second == null)
            {
                return false;
            }

            bool paletteSent = false;
            bool initialAttempted = false;
            bool restoreClientWelcome = false;
            try
            {
                if (!string.IsNullOrEmpty(originalWelcome))
                {
                    // Initial's client handler repeats the existing welcome
                    // text. Suppress only this recipient's cached welcome;
                    // Immediate itself does not raise that client event.
                    restoreClientWelcome = true;
                    MultiplayerOptions.OptionType.WelcomeMessage.SetValue(string.Empty);
                    Send(peer, new MultiplayerOptionsImmediate());
                }

                MultiplayerOptions.OptionType.CultureTeam1.SetValue(cultureId);
                // Distinct cultures avoid Native's defender-side color swap.
                MultiplayerOptions.OptionType.CultureTeam2.SetValue(second);
                initialAttempted = true;
                Send(peer, new MultiplayerOptionsInitial());
                paletteSent = true;
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Country palette synchronization failed: {exception.Message}");
            }
            finally
            {
                // WriteMessage serializes immediately. Restore these values
                // within the same mission-thread call, without an await or
                // scheduled tick. Native setters assign their stored values.
                MultiplayerOptions.OptionType.CultureTeam1.SetValue(originalFirst);
                MultiplayerOptions.OptionType.CultureTeam2.SetValue(originalSecond);
                MultiplayerOptions.OptionType.WelcomeMessage.SetValue(originalWelcome);

                if (!paletteSent && initialAttempted)
                {
                    // The caller will retain the old culture on failure. A
                    // failed End may already have delivered the new options.
                    TrySend(peer, () => new MultiplayerOptionsInitial());
                }

                if (restoreClientWelcome)
                {
                    TrySend(peer, () => new MultiplayerOptionsImmediate());
                }
            }

            return paletteSent;
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"Country palette preparation failed: {exception.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }

    private static void Send(NetworkCommunicator peer, GameNetworkMessage message)
    {
        GameNetwork.BeginModuleEventAsServer(peer);
        try
        {
            // Native packets have a fixed option layout. Keep all original
            // entries in each category; passwords are Never-replicated.
            GameNetwork.WriteMessage(message);
        }
        finally
        {
            GameNetwork.EndModuleEventAsServer();
        }
    }

    private static void TrySend(NetworkCommunicator peer, Func<GameNetworkMessage> createMessage)
    {
        try
        {
            Send(peer, createMessage());
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"Country palette recovery synchronization failed: {exception.Message}");
        }
    }
}
