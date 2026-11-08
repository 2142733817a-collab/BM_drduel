using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Network.Messages;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsTeamOrbSync : MissionLogic
{
    private const string SlotTag = "adimi_team_orb";
    private const string EnemySlotTag = "adimi_enemy_orb";
    private const float UpdateInterval = 0.15f;
    private const float MoveDuration = 0.12f;
    private const float HeadOffset = 0.28f;
    private const float AllyRange = 35f;
    private const float BoxedEnemyRange = 30f;
    private const float OpenEnemyRange = 50f;
    private const float MinimumMovementSquared = 0.04f * 0.04f;

    private readonly List<SynchedMissionObject> _slots = new();
    private readonly List<SynchedMissionObject> _enemySlots = new();
    private readonly Dictionary<Target, SynchedMissionObject> _assignments = new();
    private readonly Dictionary<MissionPeer, Dictionary<SynchedMissionObject, View>> _views = new();
    private AdimiToolsTeamBattleBehavior? _teamBattle;
    private AdimiToolsAiArenaBehavior? _aiArena;
    private double _nextUpdate;
    private double _nextErrorLog;

    private readonly record struct Target(Agent Agent, bool Enemy);

    private sealed class View
    {
        public View(Agent agent) => Agent = agent;

        public Agent Agent { get; }
        public Vec3 Position { get; set; }
        public bool Positioned { get; set; }
        public double ShowAfter { get; set; }
        public bool Visible { get; set; }
    }

    public override void AfterStart()
    {
        _teamBattle = Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>();
        _aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
        LoadSlots(SlotTag, _slots);
        LoadSlots(EnemySlotTag, _enemySlots);

        if (_slots.Count + _enemySlots.Count != 0)
        {
            AdimiToolsConsoleLog.Log($"Combat orbs: {_slots.Count} blue and {_enemySlots.Count} red hidden scene slots ready.");
        }
    }

    public bool HasVisibleMarker(MissionPeer observer, MissionPeer target)
        => target.ControlledAgent is Agent agent && _assignments.TryGetValue(new Target(agent, false), out var slot)
            && _views.TryGetValue(observer, out var views)
            && views.TryGetValue(slot, out var view) && view.Visible
            && ReferenceEquals(view.Agent, target.ControlledAgent);

    public override void OnMissionTick(float dt)
    {
        double now = Mission.CurrentTime;
        if (_slots.Count + _enemySlots.Count == 0 || now < _nextUpdate)
        {
            return;
        }

        _nextUpdate = now + UpdateInterval;
        MissionPeer[] peers = GameNetwork.NetworkPeers
            .Where(network => !network.IsServerPeer && network.IsConnectionActive && network.IsSynchronized)
            .Select(network => network.GetComponent<MissionPeer>()).OfType<MissionPeer>().ToArray();
        var teams = new Dictionary<MissionPeer, Team>();
        foreach (MissionPeer peer in peers)
        {
            if (_slots.Count != 0 && _teamBattle?.GetMarkerTeam(peer, combatOnly: true) is Team team)
            {
                teams.Add(peer, team);
            }
        }

        var allies = teams.ToLookup(pair => pair.Value, pair => pair.Key.ControlledAgent!);
        var positions = new Dictionary<Target, Vec3>();
        var desired = new Dictionary<MissionPeer, HashSet<Target>>();
        foreach (MissionPeer peer in peers)
        {
            if (peer.ControlledAgent is not { } viewer || !viewer.IsActive() || viewer.Health <= 0f)
            {
                continue;
            }

            var selected = new HashSet<Target>();
            if (teams.TryGetValue(peer, out Team? team))
            {
                foreach (Agent ally in allies[team])
                {
                    AddTarget(viewer, new Target(ally, false), AllyRange, selected, positions);
                }
            }

            if (_enemySlots.Count != 0)
            {
                foreach (var (opponent, hasBox) in GetOpponents(peer))
                {
                    AddTarget(viewer, new Target(opponent, true), hasBox ? BoxedEnemyRange : OpenEnemyRange, selected, positions);
                }
            }

            if (selected.Count != 0)
            {
                desired.Add(peer, selected);
            }
        }

        foreach (Target target in _assignments.Keys.Where(target => !positions.ContainsKey(target)).ToArray())
        {
            _assignments.Remove(target);
        }

        var free = new Queue<SynchedMissionObject>(_slots.Except(_assignments.Values));
        var freeEnemy = new Queue<SynchedMissionObject>(_enemySlots.Except(_assignments.Values));
        foreach (Target target in positions.Keys)
        {
            if (!_assignments.ContainsKey(target) && (target.Enemy ? freeEnemy : free).TryDequeue(out var slot))
            {
                _assignments.Add(target, slot);
            }
        }

        foreach (MissionPeer observer in _views.Keys.Except(peers).ToArray())
        {
            _views.Remove(observer);
        }

        foreach (MissionPeer observer in peers)
        {
            UpdateObserver(observer, desired.GetValueOrDefault(observer), positions, now);
        }
    }

    public override void OnClearScene()
    {
        ClearViews();
        base.OnClearScene();
    }

    public override void OnRemoveBehavior()
    {
        ClearViews();
        base.OnRemoveBehavior();
    }

    private static void AddTarget(Agent viewer, Target target, float range, HashSet<Target> selected, Dictionary<Target, Vec3> positions)
    {
        if (ReferenceEquals(viewer, target.Agent) || !target.Agent.IsActive() || target.Agent.Health <= 0f
            || viewer.Position.DistanceSquared(target.Agent.Position) > range * range)
        {
            return;
        }

        if (!positions.ContainsKey(target))
        {
            Vec3 head = target.Agent.GetEyeGlobalPosition() + new Vec3(0f, 0f, HeadOffset);
            if (!float.IsFinite(head.x) || !float.IsFinite(head.y) || !float.IsFinite(head.z))
            {
                return;
            }

            positions.Add(target, head);
        }

        selected.Add(target);
    }

    private static void Send(NetworkCommunicator receiver, GameNetworkMessage message)
    {
        GameNetwork.BeginModuleEventAsServer(receiver);
        try
        {
            GameNetwork.WriteMessage(message);
        }
        finally
        {
            GameNetwork.EndModuleEventAsServer();
        }
    }

    private void LoadSlots(string tag, List<SynchedMissionObject> destination)
    {
        foreach (var entity in Mission.Scene.FindEntitiesWithTag(tag))
        {
            var script = entity.GetFirstScriptOfType<SynchedMissionObject>();
            if (entity.Parent == null && !entity.GetVisibilityExcludeParents()
                && script != null && script.GetType() == typeof(SynchedMissionObject)
                && script.Id.Id is >= 0 and < 8191 && !script.Id.CreatedAtRuntime
                && !script.IsDisabled && !_slots.Concat(_enemySlots).Any(slot => slot.Id == script.Id))
            {
                destination.Add(script);
            }
        }
    }

    private IEnumerable<(Agent agent, bool hasBox)> GetOpponents(MissionPeer peer)
    {
        IEnumerable<(Agent agent, bool hasBox)> ai = _aiArena?.GetOrbOpponents(peer) ?? Enumerable.Empty<(Agent, bool)>();
        return (_teamBattle?.GetOrbOpponents(peer) ?? Enumerable.Empty<(Agent, bool)>())
            .Concat(ai.Where(opponent => !opponent.hasBox));
    }

    private void UpdateObserver(MissionPeer observer, HashSet<Target>? selected, Dictionary<Target, Vec3> positions, double now)
    {
        if (!_views.TryGetValue(observer, out var views))
        {
            if (selected == null)
            {
                return;
            }

            views = new Dictionary<SynchedMissionObject, View>();
            _views.Add(observer, views);
        }

        NetworkCommunicator network = observer.GetNetworkPeer();
        try
        {
            var desired = new Dictionary<SynchedMissionObject, Target>();
            if (selected != null)
            {
                foreach (Target target in selected)
                {
                    if (_assignments.TryGetValue(target, out var slot))
                    {
                        desired.Add(slot, target);
                    }
                }
            }

            foreach (var (slot, view) in views.ToArray())
            {
                if (!desired.TryGetValue(slot, out var target) || !ReferenceEquals(target.Agent, view.Agent))
                {
                    Send(network, new SetMissionObjectVisibility(slot.Id, false));
                    views.Remove(slot);
                }
            }

            foreach (var (slot, target) in desired)
            {
                if (!views.TryGetValue(slot, out var view))
                {
                    view = new View(target.Agent);
                    views.Add(slot, view);
                }

                Vec3 position = positions[target];
                if (!view.Positioned || view.Position.DistanceSquared(position) >= MinimumMovementSquared)
                {
                    // Scene slots are top-level objects. The local-frame packet
                    // preserves the original frame codec in the stock client.
                    MatrixFrame frame = MatrixFrame.Identity;
                    frame.origin = position;
                    Send(network, new SetMissionObjectFrameOverTime(slot.Id, ref frame, MoveDuration));
                    if (!view.Positioned)
                    {
                        view.ShowAfter = now + UpdateInterval;
                    }

                    view.Positioned = true;
                    view.Position = position;
                }

                if (!view.Visible && now >= view.ShowAfter)
                {
                    Send(network, new SetMissionObjectVisibility(slot.Id, true));
                    view.Visible = true;
                }
            }
        }
        catch (Exception exception)
        {
            if (now >= _nextErrorLog)
            {
                AdimiToolsConsoleLog.Log($"Team orb synchronization will retry: {exception.Message}");
                _nextErrorLog = now + 5d;
            }
        }

        if (views.Count == 0)
        {
            _views.Remove(observer);
        }
    }

    private void ClearViews()
    {
        var noTargets = new Dictionary<Target, Vec3>();
        foreach (MissionPeer observer in _views.Keys.ToArray())
        {
            if (observer.GetNetworkPeer() is { IsConnectionActive: true, IsSynchronized: true })
            {
                UpdateObserver(observer, null, noTargets, Mission.CurrentTime);
            }
        }

        _views.Clear();
        _assignments.Clear();
        _nextUpdate = 0d;
    }
}
