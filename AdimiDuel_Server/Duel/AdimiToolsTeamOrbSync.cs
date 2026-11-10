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
    private const double FootUpdateInterval = 0.15d;
    private const double MountedUpdateInterval = 0.10d;
    private const float FootMoveDuration = 0.12f;
    private const float MountedMoveDuration = 0.08f;
    private const double ClockTolerance = 0.00001d;
    private const float MountedLeadSeconds = 0.10f;
    private const float MaxMountedLeadDistance = 0.9f;
    private const float HeadOffset = 0.28f;
    private const float AllyRange = 35f;
    private const float BoxedEnemyRange = 30f;
    private const float OpenEnemyRange = 50f;
    private const float MinimumMovementSquared = 0.04f * 0.04f;
    private const float FacingChangeSquared = 0.10f * 0.10f;

    private readonly List<SynchedMissionObject> _slots = new();
    private readonly List<SynchedMissionObject> _enemySlots = new();
    private readonly Dictionary<Target, SynchedMissionObject> _assignments = new();
    private readonly Dictionary<MissionPeer, Dictionary<SynchedMissionObject, View>> _views = new();
    private AdimiToolsTeamBattleBehavior? _teamBattle;
    private AdimiToolsAiArenaBehavior? _aiArena;
    private double _nextFootUpdate;
    private double _nextMountedUpdate;
    private double _nextErrorLog;

    private readonly record struct Target(Agent Agent, bool Enemy);
    private readonly record struct MarkerPosition(Vec3 Head, bool Mounted);

    private sealed class View
    {
        public View(Agent agent) => Agent = agent;

        public Agent Agent { get; }
        public Vec3 Position { get; set; }
        public Vec3 Facing { get; set; }
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
        bool updateFoot = now + ClockTolerance >= _nextFootUpdate;
        bool updateMounted = now + ClockTolerance >= _nextMountedUpdate;
        if (_slots.Count + _enemySlots.Count == 0 || (!updateFoot && !updateMounted))
        {
            return;
        }

        MissionPeer[] peers = GameNetwork.NetworkPeers
            .Where(network => !network.IsServerPeer && network.IsConnectionActive && network.IsSynchronized)
            .Select(network => network.GetComponent<MissionPeer>()).OfType<MissionPeer>().ToArray();
        var teams = new Dictionary<MissionPeer, Team>();
        foreach (MissionPeer peer in peers)
        {
            if (_slots.Count != 0 && (_teamBattle?.GetMarkerTeam(peer, combatOnly: true)
                ?? _aiArena?.GetCoopMarkerTeam(peer)) is Team team)
            {
                teams.Add(peer, team);
            }
        }

        var allies = teams.ToLookup(pair => pair.Value, pair => pair.Key.ControlledAgent!);
        var positions = new Dictionary<Target, MarkerPosition>();
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
                    if (!hasBox || GetLivingMount(viewer) != null || GetLivingMount(opponent) != null)
                    {
                        AddTarget(viewer, new Target(opponent, true), hasBox ? BoxedEnemyRange : OpenEnemyRange, selected, positions);
                    }
                }
            }

            if (selected.Count != 0)
            {
                desired.Add(peer, selected);
            }
        }

        bool hasMountedTargets = positions.Values.Any(position => position.Mounted);
        bool hasFootTargets = positions.Count == 0 || positions.Values.Any(position => !position.Mounted);
        updateFoot |= hasFootTargets && double.IsPositiveInfinity(_nextFootUpdate);
        updateMounted |= hasMountedTargets && double.IsPositiveInfinity(_nextMountedUpdate);
        _nextFootUpdate = hasFootTargets ? (updateFoot ? now + FootUpdateInterval : _nextFootUpdate) : double.PositiveInfinity;
        _nextMountedUpdate = hasMountedTargets ? (updateMounted ? now + MountedUpdateInterval : _nextMountedUpdate) : double.PositiveInfinity;

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
            UpdateObserver(observer, desired.GetValueOrDefault(observer), positions, now, updateFoot, updateMounted);
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

    private static Agent? GetLivingMount(Agent agent)
        => agent.MountAgent is { } mount && mount.IsActive() && mount.Health > 0f ? mount : null;

    private static void AddTarget(Agent viewer, Target target, float range, HashSet<Target> selected, Dictionary<Target, MarkerPosition> positions)
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

            Agent? mount = GetLivingMount(target.Agent);
            if (mount != null)
            {
                // Predict horizontal motion only. Keep the lead short so a
                // hard turn or stop cannot put the marker far from the rider.
                Vec3 velocity = mount.GetRealGlobalVelocity();
                float speedSquared = velocity.x * velocity.x + velocity.y * velocity.y;
                if (float.IsFinite(speedSquared) && speedSquared > 0f)
                {
                    float lead = Math.Min(MountedLeadSeconds, MaxMountedLeadDistance / System.MathF.Sqrt(speedSquared));
                    head += new Vec3(velocity.x * lead, velocity.y * lead, 0f);
                }
            }

            positions.Add(target, new MarkerPosition(head, mount != null));
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
            .Concat(ai);
    }

    private void UpdateObserver(MissionPeer observer, HashSet<Target>? selected, Dictionary<Target, MarkerPosition> positions, double now,
        bool updateFoot, bool updateMounted)
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

                MarkerPosition sample = positions[target];
                Vec3 position = sample.Head;
                Vec3 viewer = observer.ControlledAgent!.Position;
                float dx = viewer.x - position.x, dy = viewer.y - position.y;
                float lengthSquared = dx * dx + dy * dy;
                Vec3 facing = view.Positioned ? view.Facing : new Vec3(0f, 1f, 0f);
                if (float.IsFinite(lengthSquared) && lengthSquared > 0.0001f)
                {
                    float inverseLength = 1f / System.MathF.Sqrt(lengthSquared);
                    facing = new Vec3(dx * inverseLength, dy * inverseLength, 0f);
                }

                bool canMove = sample.Mounted ? updateMounted : updateFoot;
                if (!view.Positioned || (canMove && (view.Position.DistanceSquared(position) >= MinimumMovementSquared
                    || view.Facing.DistanceSquared(facing) >= FacingChangeSquared)))
                {
                    // Scene slots are top-level objects. The local-frame packet
                    // preserves the original frame codec in the stock client.
                    MatrixFrame frame = MatrixFrame.Identity;
                    frame.origin = position;
                    frame.rotation.s = new Vec3(-facing.y, facing.x, 0f);
                    frame.rotation.f = new Vec3(0f, 0f, 1f);
                    frame.rotation.u = facing;
                    float duration = sample.Mounted ? MountedMoveDuration : FootMoveDuration;
                    Send(network, new SetMissionObjectFrameOverTime(slot.Id, ref frame, duration));
                    if (!view.Positioned)
                    {
                        view.ShowAfter = now + duration;
                    }

                    view.Positioned = true;
                    view.Position = position;
                    view.Facing = facing;
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
        var noTargets = new Dictionary<Target, MarkerPosition>();
        foreach (MissionPeer observer in _views.Keys.ToArray())
        {
            if (observer.GetNetworkPeer() is { IsConnectionActive: true, IsSynchronized: true })
            {
                UpdateObserver(observer, null, noTargets, Mission.CurrentTime, updateFoot: false, updateMounted: false);
            }
        }

        _views.Clear();
        _assignments.Clear();
        _nextFootUpdate = 0d;
        _nextMountedUpdate = 0d;
    }
}
