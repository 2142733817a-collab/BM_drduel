using System.Security.Cryptography;
using AdimiToolsShared;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// PVP telemetry is isolated from the existing duel/team scoring and never
// modifies agents. Engine objects stay on the mission side of the store queue.
internal sealed class AdimiToolsPvpLearningBehavior : MissionLogic
{
    internal readonly record struct Member(MissionPeer Peer, Agent Agent, int Side);

    private sealed class Round
    {
        internal AdimiToolsPvpLearningCapture Capture { get; init; } = null!;
        internal Dictionary<int, Member> Members { get; init; } = null!;
    }

    private sealed class MountBinding
    {
        internal Round Round { get; init; } = null!;
        internal int Rider { get; init; }
        internal int MountId { get; init; }
        internal bool Current { get; set; } = true;
        internal bool Removed { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<object, Round> _rounds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, Round> _actors = new();
    private readonly Dictionary<int, MountBinding> _mounts = new();
    private readonly Dictionary<int, MountBinding> _mountsByRider = new();
    private readonly Dictionary<int, int> _mountVersions = new();
    private readonly HashSet<int> _mountPending = new();
    private readonly Dictionary<MissionPeer, string> _tokens = new();
    private bool _ending;

    internal void Begin(object key, string matchType, int arenaId, IEnumerable<Member> members)
    {
        try
        {
            Finish(key, null, interrupted: true);
            Member[] values = members.ToArray();
            var participants = new List<AdimiToolsPvpLearningCapture.Participant>(values.Length);
            var byId = new Dictionary<int, Member>();
            foreach (Member member in values)
            {
                int id = member.Agent.Index;
                byId.Add(id, member);
                if (!_tokens.TryGetValue(member.Peer, out string? token))
                {
                    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    _tokens.Add(member.Peer, token);
                }
                participants.Add(new AdimiToolsPvpLearningCapture.Participant(id, member.Side, token));
            }
            var round = new Round { Capture = new AdimiToolsPvpLearningCapture(matchType, arenaId, Mission.CurrentTime, participants), Members = byId };
            lock (_gate)
            {
                _rounds.Add(key, round);
                foreach (int id in round.Capture.ActorIds)
                {
                    if (_mounts.TryGetValue(id, out MountBinding? mountCollision))
                    { round.Capture.Invalidate(); mountCollision.Round.Capture.Invalidate(); }
                    if (_actors.TryGetValue(id, out Round? other)) { round.Capture.Invalidate(); other.Capture.Invalidate(); }
                    else _actors.Add(id, round);
                }
            }
        }
        catch (Exception exception)
        {
            Finish(key, null, interrupted: true);
            AdimiToolsConsoleLog.Log($"PvP learning capture could not start: {exception.Message}");
        }
    }

    internal void Finish(object key, int? winningSide, bool interrupted)
    {
        Round? round;
        lock (_gate)
        {
            if (!_rounds.Remove(key, out round)) return;
            foreach (int id in round.Capture.ActorIds)
            {
                if (!_actors.TryGetValue(id, out Round? current) || !ReferenceEquals(current, round)) continue;
                _actors.Remove(id);
                if (_mountsByRider.TryGetValue(id, out MountBinding? associated) && ReferenceEquals(associated.Round, round))
                    _mountsByRider.Remove(id);
                _mountVersions.Remove(id);
                _mountPending.Remove(id);
            }
            foreach (int id in _mounts.Where(x => ReferenceEquals(x.Value.Round, round)).Select(x => x.Key).ToArray())
                _mounts.Remove(id);
        }
        try
        {
            foreach (Member member in round.Members.Values)
                if (member.Peer.GetNetworkPeer()?.IsConnectionActive != true) round.Capture.Invalidate();
            AdimiToolsAiLearningEpisode? episode = round.Capture.Finish(Mission.CurrentTime, winningSide, interrupted);
            if (episode != null && !AdimiToolsAiLearningStore.TryEnqueue(episode))
                AdimiToolsConsoleLog.Log("PvP learning capture was not queued; round was not saved.");
        }
        catch (Exception exception) { AdimiToolsConsoleLog.Log($"PvP learning capture could not finish: {exception.Message}"); }
    }

    public override void OnPreMissionTick(float dt)
    {
        if (_ending) return;
        Round[] rounds;
        lock (_gate) rounds = _rounds.Values.ToArray();
        foreach (Round round in rounds)
        {
            try
            {
                double now = Mission.CurrentTime;
                if (!round.Capture.ShouldSample(now)) continue;
                var actors = new List<AdimiToolsAiLearningActor>();
                foreach (var (id, member) in round.Members)
                {
                    if (round.Capture.IsRemoved(id))
                    {
                        Agent? replacement = member.Peer.ControlledAgent;
                        if (replacement != null && !ReferenceEquals(replacement, member.Agent)
                            && AdimiToolsMountSafety.IsLiving(Mission, replacement)) round.Capture.Invalidate();
                        continue;
                    }
                    Agent agent = member.Agent;
                    if (member.Peer.GetNetworkPeer()?.IsConnectionActive != true || !ReferenceEquals(member.Peer.ControlledAgent, agent)
                        || !AdimiToolsMountSafety.IsLiving(Mission, agent)) { round.Capture.Invalidate(); continue; }
                    actors.Add(ReadActor(round, member, id));
                }
                round.Capture.AddFrame(now, actors);
            }
            catch (Exception) { round.Capture.Invalidate(); }
        }
    }

    private AdimiToolsAiLearningActor ReadActor(Round round, Member member, int id)
    {
        Agent agent = member.Agent;
        if (!round.Capture.TryGetParticipant(id, out var participant)) throw new InvalidOperationException("Unknown participant.");
        int mountVersion;
        lock (_gate) mountVersion = _mountVersions.GetValueOrDefault(id);
        Vec3 position = agent.Position, look = agent.LookDirection, velocity = agent.GetRealGlobalVelocity();
        Vec2 input = agent.MovementInputVector;
        MissionWeapon weapon = agent.WieldedWeapon;
        WeaponComponentData? usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
        var actor = new AdimiToolsAiLearningActor
        {
            Id = id, IsPlayer = true, Side = member.Side, PlayerToken = participant.Token,
            CharacterId = agent.Character?.StringId ?? string.Empty,
            WeaponId = weapon.IsEmpty || weapon.Item is not ItemObject item ? string.Empty : item.StringId ?? string.Empty,
            WeaponClass = usage?.WeaponClass.ToString() ?? string.Empty,
            WeaponLength = usage?.GetRealWeaponLength() ?? 0f,
            X = position.x, Y = position.y, Z = position.z,
            LookX = look.x, LookY = look.y, LookZ = look.z,
            VelocityX = velocity.x, VelocityY = velocity.y, MoveInputX = input.x, MoveInputY = input.y,
            Health = agent.Health, HealthLimit = agent.HealthLimit,
            ActionType = (int)agent.GetCurrentActionType(1), ActionStage = (int)agent.GetCurrentActionStage(1),
            ActionDirection = (int)agent.GetCurrentActionDirection(1), ActionProgress = agent.GetCurrentActionProgress(1),
            MovementFlags = (uint)agent.MovementFlags, EventFlags = (uint)agent.EventControlFlags,
        };
        if (agent.HasMount)
        {
            Agent? mount = agent.MountAgent;
            if (!AdimiToolsMountSafety.IsLiving(Mission, mount) || !ReferenceEquals(mount!.RiderAgent, agent))
                throw new InvalidOperationException("Mounted actor has no matching living mount.");
            Vec3 mountPosition = mount.Position, mountLook = mount.Frame.rotation.f, mountVelocity = mount.GetRealGlobalVelocity();
            Vec2 mountInput = mount.MovementInputVector;
            actor.IsMounted = true; actor.MountId = mount.Index;
            actor.MountX = mountPosition.x; actor.MountY = mountPosition.y; actor.MountZ = mountPosition.z;
            actor.MountLookX = mountLook.x; actor.MountLookY = mountLook.y; actor.MountLookZ = mountLook.z;
            actor.MountVelocityX = mountVelocity.x; actor.MountVelocityY = mountVelocity.y;
            actor.MountInputX = mountInput.x; actor.MountInputY = mountInput.y;
            actor.MountHealth = mount.Health; actor.MountHealthLimit = mount.HealthLimit;
        }
        RegisterMount(round, id, actor.MountId, mountVersion);
        return actor;
    }

    private void RegisterMount(Round round, int riderId, int mountId, int version)
    {
        lock (_gate)
        {
            if (!_actors.TryGetValue(riderId, out Round? owner) || !ReferenceEquals(owner, round) || round.Capture.IsTerminal) return;
            if (_mountVersions.GetValueOrDefault(riderId) != version) { round.Capture.Invalidate(); return; }
            if (_mountsByRider.TryGetValue(riderId, out MountBinding? previous) && (mountId < 0 || previous.MountId != mountId))
            { previous.Current = false; _mountsByRider.Remove(riderId); }
            if (mountId < 0)
            {
                if (_mountPending.Contains(riderId)) round.Capture.Invalidate();
                return;
            }
            if (_actors.TryGetValue(mountId, out Round? human)) { round.Capture.Invalidate(); human.Capture.Invalidate(); return; }
            if (_mounts.TryGetValue(mountId, out MountBinding? existing))
            {
                if (existing.Removed || !ReferenceEquals(existing.Round, round) || (existing.Current && existing.Rider != riderId))
                { round.Capture.Invalidate(); existing.Round.Capture.Invalidate(); return; }
                if (existing.Rider == riderId)
                { existing.Current = true; _mountsByRider[riderId] = existing; _mountPending.Remove(riderId); return; }
            }
            var binding = new MountBinding { Round = round, Rider = riderId, MountId = mountId };
            _mounts[mountId] = binding; _mountsByRider[riderId] = binding; _mountPending.Remove(riderId);
        }
    }

    private (Round? Round, int Rider, MountBinding? Mount) Lookup(int index)
    {
        lock (_gate)
        {
            if (_actors.TryGetValue(index, out Round? actor)) return (actor, index, null);
            if (_mounts.TryGetValue(index, out MountBinding? mount)) return (mount.Round, mount.Rider, mount);
            return (null, -1, null);
        }
    }

    private bool HasPendingMount(Round round) => round.Members.Keys.Any(_mountPending.Contains);

    public override void OnScoreHit(Agent victim, Agent attacker, WeaponComponentData attackerWeapon, bool isBlocked,
        bool isSiegeEngineHit, in Blow blow, in AttackCollisionData collisionData, float damagedHp, float hitDistance, float shotDifficulty)
    {
        Round? first = null, second = null;
        try
        {
            int victimId = victim.Index, attackerId = attacker?.Index ?? -1;
            lock (_gate)
            {
                var target = Lookup(victimId); var source = Lookup(attackerId);
                first = target.Round; second = source.Round;
                if (first == null && second == null) return;
                bool lateMissile = first != null && ReferenceEquals(first, second) && first.Capture.IsTerminal
                    && collisionData.IsMissile && first.Capture.IsRemoved(source.Rider)
                    && (target.Mount == null ? !first.Capture.IsRemoved(target.Rider) : !target.Mount.Removed);
                if ((first ?? second)!.Capture.IsTerminal && (first == null || second == null || ReferenceEquals(first, second)) && !lateMissile) return;
                if (first == null || second == null || !ReferenceEquals(first, second))
                {
                    if (first != null && !first.Capture.IsTerminal) first.Capture.Invalidate();
                    if (second != null && !second.Capture.IsTerminal) second.Capture.Invalidate();
                    return;
                }
                if (HasPendingMount(first) || first.Capture.IsRemoved(source.Rider)
                    || (target.Mount == null && (!ReferenceEquals(victim, first.Members[target.Rider].Agent) || first.Capture.IsRemoved(target.Rider)))
                    || (source.Mount == null && !ReferenceEquals(attacker, first.Members[source.Rider].Agent))
                    || (target.Mount != null && target.Mount.Removed)
                    || (source.Mount != null && (!source.Mount.Current || source.Mount.Removed)))
                    first.Capture.Invalidate();
                if (target.Mount != null)
                    first.Capture.RecordMountHit(Mission.CurrentTime, attackerId, victimId, isBlocked, damagedHp,
                        collisionData.IsMissile, (int)collisionData.AttackDirection, collisionData.AttackProgress, collisionData.StrikeType, collisionData.ThrustTipHit,
                        source.Rider, lateMissile, attackerWeapon?.WeaponClass.ToString() ?? string.Empty, collisionData.IsAlternativeAttack);
                else
                    first.Capture.RecordHit(Mission.CurrentTime, attackerId, victimId, isBlocked, damagedHp,
                        collisionData.IsMissile, (int)collisionData.AttackDirection, collisionData.AttackProgress, collisionData.StrikeType, collisionData.ThrustTipHit,
                        source.Rider, lateMissile, attackerWeapon?.WeaponClass.ToString() ?? string.Empty, collisionData.IsAlternativeAttack);
            }
        }
        catch (Exception) { first?.Capture.Invalidate(); second?.Capture.Invalidate(); }
    }

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
    {
        Round? round = null;
        try
        {
            int victimId = affectedAgent.Index, attackerId = affectorAgent?.Index ?? -1;
            lock (_gate)
            {
                var target = Lookup(victimId); var source = Lookup(attackerId); round = target.Round;
                if (round == null) { source.Round?.Capture.Invalidate(); return; }
                if (source.Round != null && !ReferenceEquals(source.Round, round))
                {
                    if (!round.Capture.IsTerminal) round.Capture.Invalidate();
                    if (!source.Round.Capture.IsTerminal) source.Round.Capture.Invalidate();
                    return;
                }
                if (round.Capture.IsTerminal && target.Mount != null) return;
                if (round.Capture.IsTerminal && agentState is not (AgentState.Killed or AgentState.Unconscious)) return;
                if (target.Mount?.Removed == true) return;
                if (HasPendingMount(round)
                    || (target.Mount == null && !ReferenceEquals(affectedAgent, round.Members[target.Rider].Agent))
                    || (source.Round == round && (round.Capture.IsRemoved(source.Rider)
                        || (source.Mount == null && !ReferenceEquals(affectorAgent, round.Members[source.Rider].Agent))))
                    || (source.Mount != null && (!source.Mount.Current || source.Mount.Removed)))
                    round.Capture.Invalidate();
                if (target.Mount != null)
                {
                    if (source.Round != round || agentState is not (AgentState.Killed or AgentState.Unconscious)) round.Capture.Invalidate();
                    round.Capture.RecordAuxiliaryEvent(Mission.CurrentTime, new AdimiToolsAiLearningEvent
                    { Kind = "mount_removal", AttackerId = attackerId, VictimId = victimId, RemovalState = (int)agentState });
                    target.Mount.Removed = true; target.Mount.Current = false;
                    if (_mountsByRider.TryGetValue(target.Mount.Rider, out MountBinding? current) && ReferenceEquals(current, target.Mount))
                        _mountsByRider.Remove(target.Mount.Rider);
                    return;
                }
                round.Capture.RecordRemoval(Mission.CurrentTime, attackerId, victimId,
                    agentState is AgentState.Killed or AgentState.Unconscious, (int)agentState, source.Round == round ? source.Rider : -1);
            }
        }
        catch (Exception) { round?.Capture.Invalidate(); }
    }

    public override void OnAgentMount(Agent agent) => RecordMountTransition(agent, true);
    public override void OnAgentDismount(Agent agent) => RecordMountTransition(agent, false);

    public override void OnAgentDeleted(Agent affectedAgent)
    {
        try
        {
            lock (_gate)
            {
                int id = affectedAgent.Index;
                if (_actors.TryGetValue(id, out Round? round) && !round.Capture.IsRemoved(id) && !round.Capture.IsTerminal)
                    round.Capture.Invalidate();
                if (_mounts.TryGetValue(id, out MountBinding? mount) && !mount.Removed)
                {
                    if (!mount.Round.Capture.IsTerminal) mount.Round.Capture.Invalidate();
                    mount.Removed = true; mount.Current = false;
                    if (_mountsByRider.TryGetValue(mount.Rider, out MountBinding? current) && ReferenceEquals(current, mount))
                        _mountsByRider.Remove(mount.Rider);
                }
            }
        }
        catch (Exception) { }
    }

    private void RecordMountTransition(Agent agent, bool mounted)
    {
        try
        {
            lock (_gate)
            {
                int id = agent.Index;
                if (!_actors.TryGetValue(id, out Round? round)) return;
                round.Capture.RecordAuxiliaryEvent(Mission.CurrentTime, new AdimiToolsAiLearningEvent
                { Kind = mounted ? "mount" : "dismount", AttackerId = -1, VictimId = id });
                _mountVersions[id] = _mountVersions.GetValueOrDefault(id) + 1;
                if (_mountsByRider.TryGetValue(id, out MountBinding? previous))
                { previous.Current = false; _mountsByRider.Remove(id); }
                if (mounted) _mountPending.Add(id); else _mountPending.Remove(id);
            }
        }
        catch (Exception) { }
    }

    public override void OnAgentShootMissile(Agent shooterAgent, EquipmentIndex weaponIndex, Vec3 position, Vec3 velocity,
        Mat3 orientation, bool hasRigidBody, int forcedMissileIndex)
    {
        Round? round = null;
        try
        {
            lock (_gate)
            {
                var actor = Lookup(shooterAgent.Index); round = actor.Round;
                if (round == null || round.Capture.IsTerminal) return;
                if (HasPendingMount(round) || round.Capture.IsRemoved(actor.Rider)
                    || actor.Mount != null || !ReferenceEquals(shooterAgent, round.Members[actor.Rider].Agent))
                    round.Capture.Invalidate();
                round.Capture.RecordAuxiliaryEvent(Mission.CurrentTime, new AdimiToolsAiLearningEvent
                {
                    Kind = "shot", AttackerId = shooterAgent.Index, VictimId = -1, WeaponSlot = (int)weaponIndex,
                    ShotX = position.x, ShotY = position.y, ShotZ = position.z,
                    ShotVelocityX = velocity.x, ShotVelocityY = velocity.y, ShotVelocityZ = velocity.z,
                });
            }
        }
        catch (Exception) { round?.Capture.Invalidate(); }
    }

    private void AbortAll()
    {
        _ending = true;
        object[] keys;
        lock (_gate) keys = _rounds.Keys.ToArray();
        foreach (object key in keys) Finish(key, null, interrupted: true);
        lock (_gate) { _mounts.Clear(); _mountsByRider.Clear(); _mountVersions.Clear(); _mountPending.Clear(); _actors.Clear(); }
        _tokens.Clear();
    }

    public override void OnClearScene() { AbortAll(); base.OnClearScene(); }
    public override void OnRemoveBehavior() { AbortAll(); base.OnRemoveBehavior(); }
    protected override void OnEndMission() { AbortAll(); base.OnEndMission(); }
}
