using System.Reflection;
using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.MissionRepresentatives;
using TaleWorlds.MountAndBlade.Network.Messages;
using TaleWorlds.PlayerServices;

namespace AdimiDuel_Server.Duel;

/// <summary>Original-client observer rows; display peers never own an AI Agent.</summary>
internal sealed class AdimiToolsAiDuelDisplay
{
    private readonly AdimiToolsAiDuelDisplayState<MissionPeer, AdimiToolsAiDisplayPeer> _state;
    private float _elapsed;

    public AdimiToolsAiDuelDisplay(Mission mission, Func<MissionPeer, bool> isBusyObserver,
        Action<MissionPeer, MissionPeer> onMarkerRestored)
    {
        _state = new(new AdimiToolsAiDuelDisplayTransport(mission, isBusyObserver, onMarkerRestored));
    }

    public bool Begin(int arenaId, MissionPeer owner, string localizedTroopName, int totalAiCount, int difficulty)
    {
        try
        {
            return _state.Begin(arenaId, owner, localizedTroopName, totalAiCount, difficulty);
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"AI duel display begin skipped; combat is unaffected: {exception}");
            return false;
        }
    }

    public void RoundEnded(int arenaId, bool playerWon) => _state.RoundEnded(arenaId, playerWon);
    public void End(int arenaId)
    {
        try
        {
            _state.End(arenaId);
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"AI duel display end deferred; combat is unaffected: {exception}");
        }
    }

    public void LateJoin(MissionPeer observer) => _state.LateJoin(observer);
    public void RemovePeer(MissionPeer peer) => _state.RemovePeer(peer);
    public bool Clear()
    {
        try
        {
            return _state.Clear();
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"AI duel display clear deferred: {exception}");
            return false;
        }
    }

    public void Tick(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0f)
        {
            return;
        }

        _elapsed += dt;
        if (_elapsed < 0.5f)
        {
            return;
        }

        _elapsed = 0f;
        try
        {
            _state.Refresh();
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"AI duel display refresh deferred; combat is unaffected: {exception}");
        }
    }
}

internal sealed class AdimiToolsAiDisplayPeer
{
    public NetworkCommunicator Peer { get; init; } = null!;
    public VirtualPlayer[] NetworkTable { get; init; } = Array.Empty<VirtualPlayer>();
    public bool ReleaseAttempted { get; set; }
}

internal sealed class AdimiToolsAiDuelDisplayTransport : IAdimiToolsAiDuelDisplayTransport<MissionPeer, AdimiToolsAiDisplayPeer>
{
    private readonly Mission _mission;
    private readonly Func<MissionPeer, bool> _isBusyObserver;
    private readonly Action<MissionPeer, MissionPeer> _onMarkerRestored;
    private readonly int _arenaIndex;
    private readonly ConstructorInfo? _peerConstructor;
    private readonly FieldInfo? _networkInterface;
    private readonly MethodInfo? _reserveIndex;
    private readonly MethodInfo? _releaseIndex;
    private ulong _nextIdentity;

    public AdimiToolsAiDuelDisplayTransport(Mission mission, Func<MissionPeer, bool> isBusyObserver,
        Action<MissionPeer, MissionPeer> onMarkerRestored)
    {
        _mission = mission;
        _isBusyObserver = isBusyObserver;
        _onMarkerRestored = onMarkerRestored;
        _arenaIndex = FindArenaIndex(mission);
        _peerConstructor = typeof(NetworkCommunicator).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, new[] { typeof(int), typeof(string), typeof(PlayerId) }, modifiers: null);
        Type? api = typeof(GameNetwork).Assembly.GetType("TaleWorlds.MountAndBlade.MBAPI");
        _networkInterface = api?.GetField("IMBNetwork", BindingFlags.Static | BindingFlags.NonPublic);
        _reserveIndex = _networkInterface?.FieldType.GetMethod("AddNewBotOnServer");
        _releaseIndex = _networkInterface?.FieldType.GetMethod("RemoveBotOnServer");
        _nextIdentity = unchecked((ulong)DateTime.UtcNow.Ticks);
    }

    public IReadOnlyCollection<MissionPeer> ConnectedObservers()
    {
        return GameNetwork.NetworkPeers.Where(p => !p.IsServerPeer && p.IsConnectionActive)
            .Select(p => p.GetComponent<MissionPeer>()).Where(p => p != null).ToArray();
    }

    public bool CanObserve(MissionPeer observer, MissionPeer owner)
    {
        NetworkCommunicator receiver = observer.GetNetworkPeer();
        return receiver.IsSynchronized && observer.Team == _mission.AttackerTeam
            && (observer.ControlledAgent?.IsActive() ?? false) && !_isBusyObserver(observer);
    }

    public object ConnectionIdentity(MissionPeer observer)
    {
        NetworkCommunicator peer = observer.GetNetworkPeer();
        return (object?)peer.PlayerConnectionInfo ?? peer;
    }

    public AdimiToolsAiDisplayPeer Allocate(string name)
    {
        if (!IsNetworkOpen() || _arenaIndex < 0 || _peerConstructor == null
            || _networkInterface == null || _reserveIndex == null || _releaseIndex == null)
        {
            throw new NotSupportedException("Original-client AI display peer capability is unavailable.");
        }

        object native = _networkInterface.GetValue(null) ?? throw new InvalidOperationException("Network interface is not initialized.");
        int index = (int)(_reserveIndex.Invoke(native, null) ?? -1);
        if (index < 0)
        {
            throw new InvalidOperationException("No native peer index is available for an AI display row.");
        }

        if (index >= GameNetwork.VirtualPlayers.Length || GameNetwork.VirtualPlayers[index] != null)
        {
            // An unexpected engine/table mismatch must never overwrite or
            // release an index currently belonging to somebody else.
            throw new InvalidOperationException("Reserved AI display index is occupied; no peer was overwritten or released.");
        }

        NetworkCommunicator? created = null;
        try
        {
            // Test is the stock non-platform ID namespace. Never borrow a real
            // account ID. This peer is not registered in server NetworkPeers,
            // given components, assigned a Team, or attached to any Agent.
            var id = new PlayerId((byte)PlayerIdProvidedTypes.Test, 0x4144494D494149UL, ++_nextIdentity);
            var peer = (NetworkCommunicator)_peerConstructor.Invoke(new object[] { index, name, id });
            created = peer;
            GameNetwork.VirtualPlayers[index] = peer.VirtualPlayer;
            Log($"AI display peer reserved: index={index}, name={name}.");
            return new() { Peer = peer, NetworkTable = GameNetwork.VirtualPlayers };
        }
        catch
        {
            if (created != null && ReferenceEquals(GameNetwork.VirtualPlayers[index], created.VirtualPlayer))
            {
                GameNetwork.VirtualPlayers[index] = null!;
            }

            _releaseIndex.Invoke(native, new object[] { index });
            throw;
        }
    }

    public void Release(AdimiToolsAiDisplayPeer display)
    {
        if (display.ReleaseAttempted)
        {
            return;
        }

        display.ReleaseAttempted = true;
        int index = display.Peer.Index;
        if (!IsNetworkOpen() || !ReferenceEquals(GameNetwork.VirtualPlayers, display.NetworkTable))
        {
            if (index >= 0 && index < display.NetworkTable.Length
                && ReferenceEquals(display.NetworkTable[index], display.Peer.VirtualPlayer))
            {
                display.NetworkTable[index] = null!;
            }

            // HandleMultiplayerEnd removes network components before destroying
            // the native session. Its index pool must not be touched afterwards.
            Log($"AI display index retired after network shutdown: index={index}; no native release.");
            return;
        }

        if (!ReferenceEquals(GameNetwork.VirtualPlayers[index], display.Peer.VirtualPlayer))
        {
            throw new InvalidOperationException("AI display ownership changed; refusing to release another peer's index.");
        }

        GameNetwork.VirtualPlayers[index] = null!;
        object native = _networkInterface!.GetValue(null) ?? throw new InvalidOperationException("Network interface is not initialized.");
        _releaseIndex!.Invoke(native, new object[] { index });
        Log($"AI display peer released: index={index}.");
    }

    public void Create(MissionPeer observer, AdimiToolsAiDisplayPeer display, Action identityKnown)
    {
        NetworkCommunicator receiver = observer.GetNetworkPeer();
        Send(receiver, new CreatePlayer(display.Peer.Index, display.Peer.UserName, -1,
            isNonExistingDisconnectedPeer: false, isReceiverPeer: false));
        identityKnown();
        EnsureComponents(receiver, display);
        Send(receiver, new InitializeLobbyPeer(display.Peer, display.Peer.VirtualPlayer, forcedAvatarIndex: 0));
        // No SetPeerTeam, SynchronizingDone or server-side fake MissionPeer.
    }

    public void Start(MissionPeer observer, MissionPeer owner, AdimiToolsAiDisplayPeer display)
    {
        Send(observer.GetNetworkPeer(), new DuelPreparationStartedForTheFirstTime(owner.GetNetworkPeer(), display.Peer, _arenaIndex));
    }

    public void Round(MissionPeer observer, MissionPeer owner, AdimiToolsAiDisplayPeer display, bool playerWon)
    {
        Send(observer.GetNetworkPeer(), new DuelRoundEnded(playerWon ? owner.GetNetworkPeer() : display.Peer));
    }

    public void End(MissionPeer observer, MissionPeer owner, AdimiToolsAiDisplayPeer display)
    {
        // The private AI key cannot identify another human's real PvP match.
        Send(observer.GetNetworkPeer(), new DuelEnded(display.Peer));
        try
        {
            _onMarkerRestored(observer, owner);
        }
        catch (Exception exception)
        {
            Log($"AI display marker invalidation failed: {exception.Message}");
        }
    }

    public void Delete(MissionPeer observer, AdimiToolsAiDisplayPeer display, bool knownPeer, Action beforeIndexDelete)
    {
        NetworkCommunicator receiver = observer.GetNetworkPeer();
        if (knownPeer)
        {
            // DeletePlayer alone does not remove VirtualPlayer's global peer
            // components. Stock Add is idempotent, whereas Remove dereferences
            // a missing component: ensure first, including partial-send retry.
            EnsureComponents(receiver, display);
            Send(receiver, new RemovePeerComponent(display.Peer, ComponentId(nameof(DuelMissionRepresentative))));
            Send(receiver, new RemovePeerComponent(display.Peer, ComponentId(nameof(MissionPeer))));
        }

        // An ambiguous Delete flush is retried only by integer index, never by
        // dereferencing a display peer which may already be gone on the client.
        beforeIndexDelete();
        Send(receiver, new DeletePlayer(display.Peer.Index, addToDisconnectList: false));
    }

    public void Log(string message) => AdimiToolsConsoleLog.Log(message);

    private static uint ComponentId(string name) => unchecked((uint)TaleWorlds.Library.Common.GetDJB2(name));

    private static bool IsNetworkOpen()
    {
        return GameNetwork.IsServer && GameNetwork.NetworkComponents != null && GameNetwork.NetworkComponents.Count > 0;
    }

    private static void EnsureComponents(NetworkCommunicator receiver, AdimiToolsAiDisplayPeer display)
    {
        Send(receiver, new AddPeerComponent(display.Peer, ComponentId(nameof(MissionPeer))));
        Send(receiver, new AddPeerComponent(display.Peer, ComponentId(nameof(DuelMissionRepresentative))));
    }

    private static void Send(NetworkCommunicator observer, GameNetworkMessage message)
    {
        GameNetwork.BeginModuleEventAsServer(observer);
        try
        {
            GameNetwork.WriteMessage(message);
        }
        finally
        {
            GameNetwork.EndModuleEventAsServer();
        }
    }

    private static int FindArenaIndex(Mission mission)
    {
        foreach (GameEntity entity in mission.Scene.FindEntitiesWithTagExpression("area_flag(_\\d+)*"))
        {
            string? tag = entity.Tags.FirstOrDefault(t => t.StartsWith("area_flag", StringComparison.Ordinal));
            if (tag != null && tag.StartsWith("area_flag_", StringComparison.Ordinal)
                && int.TryParse(tag.Substring("area_flag_".Length), out int number) && number >= 1 && number <= 5)
            {
                return number - 1;
            }
        }

        return -1;
    }
}
