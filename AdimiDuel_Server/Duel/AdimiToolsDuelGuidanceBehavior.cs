using System.ComponentModel;
using AdimiToolsShared;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsDuelGuidanceBehavior : MissionLogic
{
    private sealed class Region
    {
        public GameEntity Entity { get; init; } = null!;
        public VolumeBox Box { get; init; } = null!;
        public bool Ranged { get; set; }
        public bool Cavalry { get; set; }
        public bool Arena { get; set; }
        public double NextErrorAt { get; set; }
    }

    private sealed class PlayerState
    {
        public Agent Actor { get; init; } = null!;
        public AdimiToolsDuelGuidanceKind SpawnKind { get; init; }
        public bool SpawnPending { get; set; } = true;
        public bool RangedInside { get; set; }
        public bool CavalryInside { get; set; }
        public bool RangedPending { get; set; }
        public bool CavalryPending { get; set; }
        public HashSet<VolumeBox> ArenaInside { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<VolumeBox> ArenaPending { get; } = new(ReferenceEqualityComparer.Instance);
        public double NextErrorAt { get; set; }
    }

    private const double PollSeconds = 0.25d;
    private const string RangedTag = "Sagittarius_Tip";
    private const string CavalryTag = "Horse_Cavalry_Alert";
    private readonly Dictionary<MissionPeer, PlayerState> _players = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MissionPeer> _spawnHintsShown = new(ReferenceEqualityComparer.Instance);
    private readonly List<Region> _regions = new();
    private double _nextPollAt;
    private double _lastTime;
    private bool _stopped = true;
    private bool _removed;
    private bool _resetPending;
    private bool _subscribed;

    public override void AfterStart()
    {
        base.AfterStart();
        if (!GameNetwork.IsServer || _removed)
        {
            return;
        }

        if (!_subscribed)
        {
            Mission.OnMissionReset += OnMissionReset;
            _subscribed = true;
        }

        Initialize();
    }

    public override void OnMissionTick(float dt)
    {
        if (_resetPending && !_removed && GameNetwork.IsServer)
        {
            _resetPending = false;
            Initialize();
        }

        if (_stopped || _removed || !GameNetwork.IsServer || !double.IsFinite(Mission.CurrentTime))
        {
            return;
        }

        double now = Mission.CurrentTime;
        if (now < _lastTime)
        {
            _nextPollAt = now;
        }

        _lastTime = now;
        if (now < _nextPollAt)
        {
            return;
        }

        _nextPollAt = now + PollSeconds;
        var connected = new HashSet<MissionPeer>(ReferenceEqualityComparer.Instance);
        foreach (NetworkCommunicator network in GameNetwork.NetworkPeers.ToArray())
        {
            MissionPeer? peer = null;
            try
            {
                peer = network.GetComponent<MissionPeer>();
                if (peer == null || network.IsServerPeer || !network.IsConnectionActive)
                {
                    continue;
                }

                if (!connected.Add(peer))
                {
                    continue;
                }

                if (!network.IsSynchronized || !peer.TeamInitialPerkInfoReady)
                {
                    continue;
                }

                Agent? actor = peer.ControlledAgent;
                if (peer.Team == null || ReferenceEquals(peer.Team, Mission.SpectatorTeam)
                    || !IsLiving(actor) || !actor!.IsHuman || !ReferenceEquals(actor.MissionPeer, peer))
                {
                    _players.Remove(peer);
                    continue;
                }

                if (!_players.TryGetValue(peer, out PlayerState? state) || !ReferenceEquals(state.Actor, actor))
                {
                    state = new PlayerState { Actor = actor, SpawnKind = Classify(actor.Character.DefaultFormationClass) };
                    _players[peer] = state;
                }

                ObserveAndNotify(peer, network, state, GroundPosition(actor), now);
            }
            catch (Exception exception)
            {
                if (peer != null && _players.TryGetValue(peer, out PlayerState? state))
                {
                    LogPeerError(state, exception, now);
                }
                else
                {
                    AdimiToolsConsoleLog.Log($"Guidance player skipped: {exception.Message}");
                }
            }
        }

        foreach (MissionPeer peer in _players.Keys.Where(peer => !connected.Contains(peer)).ToArray())
        {
            _players.Remove(peer);
        }

        _spawnHintsShown.RemoveWhere(peer => !connected.Contains(peer));
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

    protected override void OnEndMission()
    {
        _removed = true;
        if (_subscribed)
        {
            Mission.OnMissionReset -= OnMissionReset;
            _subscribed = false;
        }

        Stop();
        base.OnEndMission();
    }

    private static AdimiToolsDuelGuidanceKind Classify(FormationClass formation)
        => formation switch
        {
            FormationClass.Ranged => AdimiToolsDuelGuidanceKind.Ranged,
            FormationClass.Cavalry or FormationClass.HorseArcher or FormationClass.LightCavalry or FormationClass.HeavyCavalry
                => AdimiToolsDuelGuidanceKind.Cavalry,
            _ => AdimiToolsDuelGuidanceKind.Infantry,
        };

    private static void LogPeerError(PlayerState state, Exception exception, double now)
    {
        if (now >= state.NextErrorAt)
        {
            AdimiToolsConsoleLog.Log($"Guidance delivery will retry: {exception.Message}");
            state.NextErrorAt = now + 5d;
        }
    }

    private bool IsLiving(Agent? actor)
        => actor != null && Mission.AllAgents.Contains(actor) && actor.State != AgentState.Deleted
            && actor.IsActive() && actor.Health > 0f && !actor.IsFadingOut();

    private Vec3 GroundPosition(Agent actor)
    {
        Agent? mount = actor.MountAgent;
        return IsLiving(mount) ? mount!.Position : actor.Position;
    }

    private void ObserveAndNotify(MissionPeer peer, NetworkCommunicator network, PlayerState state, Vec3 position, double now)
    {
        if (!position.IsValid)
        {
            return;
        }

        bool rangedInside = false;
        bool cavalryInside = false;
        var arenaInside = new HashSet<VolumeBox>(ReferenceEqualityComparer.Instance);
        foreach (Region region in _regions)
        {
            try
            {
                if (!region.Entity.WeakEntity.IsValid || !region.Box.IsPointIn(position))
                {
                    continue;
                }

                rangedInside |= region.Ranged;
                cavalryInside |= region.Cavalry;
                if (region.Arena)
                {
                    arenaInside.Add(region.Box);
                }
            }
            catch (Exception exception)
            {
                if (now >= region.NextErrorAt)
                {
                    AdimiToolsConsoleLog.Log($"Guidance region skipped: {exception.Message}");
                    region.NextErrorAt = now + 5d;
                }
            }
        }

        state.RangedPending = rangedInside && (state.RangedPending || !state.RangedInside);
        state.CavalryPending = cavalryInside && (state.CavalryPending || !state.CavalryInside);
        state.ArenaPending.IntersectWith(arenaInside);
        state.ArenaPending.UnionWith(arenaInside.Where(box => !state.ArenaInside.Contains(box)));
        state.RangedInside = rangedInside;
        state.CavalryInside = cavalryInside;
        state.ArenaInside.Clear();
        state.ArenaInside.UnionWith(arenaInside);

        var messages = new HashSet<AdimiToolsDuelGuidanceKind>();
        if (state.SpawnPending)
        {
            messages.Add(state.SpawnKind);
        }

        if (state.RangedPending)
        {
            messages.Add(AdimiToolsDuelGuidanceKind.Ranged);
        }

        if (state.CavalryPending)
        {
            messages.Add(AdimiToolsDuelGuidanceKind.Cavalry);
        }

        if (state.ArenaPending.Count != 0)
        {
            messages.Add(AdimiToolsDuelGuidanceKind.Arena);
        }

        bool soundUsed = false;
        // Spawn has priority so the first spawn and first arena entry share
        // one cue. A hash-set alone must not determine audible ordering.
        foreach (AdimiToolsDuelGuidanceKind kind in messages.OrderBy(kind => kind == state.SpawnKind ? 0 : 1).ThenBy(kind => kind))
        {
            try
            {
                bool spawn = state.SpawnPending && kind == state.SpawnKind;
                bool firstSpawnHint = spawn && !_spawnHintsShown.Contains(peer);
                bool playSound = !soundUsed && (kind == AdimiToolsDuelGuidanceKind.Arena || firstSpawnHint);
                string message = AdimiToolsDuelGuidanceText.Get(kind);
                if (firstSpawnHint && AdimiToolsMissionMultiplayerDuel.PlayersDuelConfig.TryGetValue(network.VirtualPlayer.Id, out var config))
                {
                    message += "\n" + AdimiToolsDuelGuidanceText.GetCurrentMode(AdimiToolsDuelModes.GetModeName(config.FirstToLimit));
                }

                AdimiToolsNotifier.ServerSendGuidanceToPlayer(network, message, playSound);
                soundUsed |= playSound;
                if (spawn)
                {
                    state.SpawnPending = false;
                    _spawnHintsShown.Add(peer);
                }

                if (kind == AdimiToolsDuelGuidanceKind.Ranged)
                {
                    state.RangedPending = false;
                }
                else if (kind == AdimiToolsDuelGuidanceKind.Cavalry)
                {
                    state.CavalryPending = false;
                }
                else if (kind == AdimiToolsDuelGuidanceKind.Arena)
                {
                    state.ArenaPending.Clear();
                }
            }
            catch (Exception exception)
            {
                LogPeerError(state, exception, now);
            }
        }
    }

    private void Initialize()
    {
        _players.Clear();
        _spawnHintsShown.Clear();
        _regions.Clear();
        _stopped = false;
        _nextPollAt = 0d;
        _lastTime = Mission.CurrentTime;
        var regions = new Dictionary<VolumeBox, Region>(ReferenceEqualityComparer.Instance);
        foreach (string tag in new[] { RangedTag, CavalryTag, "adimi_ai_area", "adimi_team_area" })
        {
            GameEntity[] entities;
            try
            {
                entities = Mission.Scene.FindEntitiesWithTag(tag).ToArray();
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Guidance region discovery skipped '{tag}': {exception.Message}");
                continue;
            }

            foreach (GameEntity entity in entities)
            {
                try
                {
                    if (!entity.WeakEntity.IsValid)
                    {
                        continue;
                    }

                    VolumeBox? box = entity.GetFirstScriptOfType<VolumeBox>();
                    if (box == null)
                    {
                        AdimiToolsConsoleLog.Log($"Guidance box '{entity.Name}' needs its original VolumeBox script.");
                        continue;
                    }

                    if (!regions.TryGetValue(box, out Region? region))
                    {
                        region = new Region { Entity = entity, Box = box };
                        regions.Add(box, region);
                    }

                    region.Ranged |= tag == RangedTag;
                    region.Cavalry |= tag == CavalryTag;
                    region.Arena |= tag is "adimi_ai_area" or "adimi_team_area";
                }
                catch (Exception exception)
                {
                    AdimiToolsConsoleLog.Log($"Guidance region discovery skipped: {exception.Message}");
                }
            }
        }

        _regions.AddRange(regions.Values);
    }

    private void OnMissionReset(object? sender, PropertyChangedEventArgs? arguments)
    {
        if (!_removed)
        {
            Stop();
            _resetPending = true;
        }
    }

    private void Stop()
    {
        _stopped = true;
        _resetPending = false;
        _players.Clear();
        _spawnHintsShown.Clear();
        _regions.Clear();
    }
}
