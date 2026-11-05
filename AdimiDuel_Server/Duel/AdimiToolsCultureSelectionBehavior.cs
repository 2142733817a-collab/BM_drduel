using System.ComponentModel;
using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsCultureSelectionBehavior : MissionLogic
{
    private sealed record PendingSwitch(Agent Agent, BasicCultureObject Culture, double ReadyAt);

    private readonly Dictionary<string, BasicCultureObject> _cultures = new(StringComparer.Ordinal);
    private readonly Dictionary<MissionPeer, PendingSwitch> _pending = new();
    private AdimiToolsMissionMultiplayerDuel? _duelMode;
    private MissionLobbyComponent? _lobby;
    private bool _stopped;
    private bool _removed;
    private bool _resetPending;
    private bool _subscribed;

    public bool CanReceiveHit(Agent? agent)
        => !_stopped && !_removed && GameNetwork.IsServer && agent != null && Mission.AllAgents.Contains(agent);

    public override void AfterStart()
    {
        base.AfterStart();
        if (!GameNetwork.IsServer)
        {
            return;
        }

        Mission.OnMissionReset += OnMissionReset;
        _subscribed = true;
        Initialize();
    }

    public override void OnMissionTick(float dt)
    {
        if (_resetPending && !_removed && GameNetwork.IsServer)
        {
            _resetPending = false;
            Initialize();
        }

        if (_stopped || _removed || !GameNetwork.IsServer || _duelMode == null || _lobby == null)
        {
            return;
        }

        foreach (var (peer, request) in _pending.ToArray())
        {
            NetworkCommunicator? network = peer.GetNetworkPeer();
            if (network?.IsConnectionActive != true || !network.IsSynchronized
                || !ReferenceEquals(peer.ControlledAgent, request.Agent) || !CanReceiveHit(request.Agent)
                || !request.Agent.IsHuman || !request.Agent.IsActive() || !(request.Agent.Health > 0f)
                || request.Agent.IsFadingOut() || !ReferenceEquals(request.Agent.MissionPeer, peer)
                || !peer.TeamInitialPerkInfoReady || IsBusy(peer))
            {
                _pending.Remove(peer);
                if (network?.IsConnectionActive == true)
                {
                    Notify(network, "国家切换已取消：角色状态变化或已进入其他对局。");
                }

                continue;
            }

            if (Mission.CurrentTime < request.ReadyAt)
            {
                continue;
            }

            // Consume before native/network work: a partial failure must not
            // despawn the same peer again on every subsequent tick.
            _pending.Remove(peer);
            try
            {
                Apply(peer, network, request.Culture);
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Country switch failed for {peer.Peer.Id}: {exception}");
            }
        }
    }

    public void OnTriggerHit(string cultureId, Agent agent)
    {
        if (!CanReceiveHit(agent) || _duelMode == null || _lobby == null
            || !agent.IsHuman || !agent.IsActive() || !(agent.Health > 0f) || agent.IsFadingOut()
            || !_cultures.TryGetValue(cultureId, out BasicCultureObject? culture))
        {
            return;
        }

        MissionPeer? peer = agent.MissionPeer;
        NetworkCommunicator? network = peer?.GetNetworkPeer();
        if (peer == null || network == null || !network.IsSynchronized || !network.IsConnectionActive
            || !ReferenceEquals(peer.ControlledAgent, agent))
        {
            return;
        }

        if (IsBusy(peer))
        {
            Notify(network, "请先结束当前对局或 AI 挑战，再敲击国家选择物品。");
            return;
        }

        if (!peer.TeamInitialPerkInfoReady)
        {
            Notify(network, "国家或装备正在切换，请稍后再试。");
            return;
        }

        if (!double.IsFinite(Mission.CurrentTime))
        {
            return;
        }

        _pending[peer] = new PendingSwitch(agent, culture, Mission.CurrentTime + 3d);
        Notify(network, $"已选择{culture.Name}，3 秒后应用；再次敲击国家选择物品可重新选择并计时。");
    }

    public override void OnClearScene()
    {
        Stop();
        base.OnClearScene();
    }

    public override void OnRemoveBehavior()
    {
        _removed = true;
        if (_subscribed)
        {
            Mission.OnMissionReset -= OnMissionReset;
            _subscribed = false;
        }

        Stop();
        base.OnRemoveBehavior();
    }

    private static void Notify(NetworkCommunicator peer, string message)
        => AdimiToolsNotifier.ServerSendMessageToPlayer(peer, message);

    private bool IsBusy(MissionPeer peer)
    {
        var aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        return peer.Team?.IsAttacker != true || _duelMode!.IsPeerBusyForAiChallenge(peer)
            || aiArena?.IsPeerReserved(peer) == true || aiArena?.HasPendingAiRespawn(peer) == true
            || Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.IsPeerReserved(peer) == true;
    }

    private void Apply(MissionPeer peer, NetworkCommunicator network, BasicCultureObject culture)
    {
        // A free player's last completed PvP may still have a one-use weapon
        // restoration. A deliberate class/culture selection starts a new kit.
        if (AdimiToolsMissionMultiplayerDuel.PlayersDuelConfig.TryGetValue(peer.Peer.Id, out var config))
        {
            config.RespawnEquip = null;
            config.NoWeaponTreshold = 0;
        }

        AdimiToolsMissionMultiplayerDuel.PlayerDuelData.PlayerDuelList.Remove(peer.Peer.Id);
        // Follow the native culture-change/respawn handshake. Remove any old
        // preview first so the stock client closes and recreates its loadout.
        // Reset the class before ChangeCulture: the client's perk reply indexes
        // the new culture with its current SelectedTroopIndex.
        peer.HasSpawnedAgentVisuals = false;
        GameNetwork.BeginBroadcastModuleEvent();
        try
        {
            GameNetwork.WriteMessage(new RemoveAgentVisualsForPeer(network));
        }
        finally
        {
            GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
        }

        peer.SelectedTroopIndex = 0;
        peer.NextSelectedTroopIndex = 0;
        GameNetwork.BeginBroadcastModuleEvent();
        try
        {
            GameNetwork.WriteMessage(new UpdateSelectedTroopIndex(network, 0));
        }
        finally
        {
            GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
        }

        peer.Culture = culture;
        _duelMode!.CancelIncomingDuelRequestsForAiChallenge(peer);
        _lobby!.DespawnPlayer(peer);
        Notify(network, $"已切换为{culture.Name}，正在重新打开该国选兵界面；请选择兵种并按界面提示重生。");
    }

    private void Initialize()
    {
        _stopped = false;
        _pending.Clear();
        _cultures.Clear();

        _duelMode = Mission.GetMissionBehavior<AdimiToolsMissionMultiplayerDuel>();
        _lobby = Mission.GetMissionBehavior<MissionLobbyComponent>();
        if (_duelMode == null || _lobby == null)
        {
            return;
        }

        foreach (var heroClass in MultiplayerClassDivisions.GetMPHeroClasses())
        {
            if (heroClass.Culture != null && heroClass.HeroCharacter != null)
            {
                _cultures.TryAdd(heroClass.Culture.StringId, heroClass.Culture);
            }
        }

        foreach (var entity in Mission.Scene.FindEntitiesWithTagExpression("adimi_culture_.*"))
        {
            if (!AdimiToolsCultureSelectionTags.TryGetCultureId(entity.Tags, out string cultureId)
                || !_cultures.ContainsKey(cultureId)
                || entity.Tags.Any(tag => tag.StartsWith("adimi_", StringComparison.Ordinal)
                    && !tag.StartsWith(AdimiToolsCultureSelectionTags.Prefix, StringComparison.Ordinal))
                || entity.Tags.Any(tag => tag is "teleport_door" or "teleport_target"
                    || tag.StartsWith("teleport_door_", StringComparison.Ordinal)))
            {
                AdimiToolsConsoleLog.Log($"Culture selector skipped '{entity.Name}': use one valid adimi_culture_<id> tag without other Adimi interaction tags.");
                continue;
            }

            try
            {
                var trigger = entity.GetFirstScriptOfType<AdimiToolsCultureSelectionTrigger>();
                if (trigger == null)
                {
                    entity.CreateAndAddScriptComponent(nameof(AdimiToolsCultureSelectionTrigger), callScriptCallbacks: true);
                    trigger = entity.GetFirstScriptOfType<AdimiToolsCultureSelectionTrigger>();
                }

                if (trigger == null)
                {
                    throw new InvalidOperationException("Could not attach culture hit observer.");
                }

                trigger.Bind(this, cultureId);
                AdimiToolsConsoleLog.Log($"Culture selector '{entity.Name}' ready: {cultureId}.");
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Culture selector '{entity.Name}' unavailable: {exception}");
            }
        }
    }

    private void OnMissionReset(object? sender, PropertyChangedEventArgs? arguments)
    {
        if (!_removed)
        {
            _stopped = true;
            _pending.Clear();
            _resetPending = true;
        }
    }

    private void Stop()
    {
        _stopped = true;
        _resetPending = false;
        _pending.Clear();
        _cultures.Clear();
    }
}
