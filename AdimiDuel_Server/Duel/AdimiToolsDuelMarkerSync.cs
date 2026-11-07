using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

/// <summary>Original-client marker synchronization; never changes real duel state.</summary>
internal sealed class AdimiToolsDuelMarkerSync
{
    private const float UpdateInterval = 0.5f;
    private readonly Mission _mission;
    private readonly Func<MissionPeer, bool, bool> _isBusy;
    private readonly Func<MissionPeer, bool> _isInAiChallenge;
    private readonly AdimiToolsDuelMarkerVisibility<MissionPeer> _visibility = new();
    private readonly Dictionary<MissionPeer, HashSet<MissionPeer>> _recoveryTargets = new();
    private readonly int _arenaIndex;
    private float _elapsed;
    private bool _enabled;

    public AdimiToolsDuelMarkerSync(Mission mission, Func<MissionPeer, bool, bool> isBusy,
        Func<MissionPeer, bool>? isInAiChallenge = null)
    {
        _mission = mission;
        _isBusy = isBusy;
        _isInAiChallenge = isInAiChallenge ?? (_ => false);
        _arenaIndex = FindArenaIndex(mission);
        _enabled = _arenaIndex >= 0
            && !string.Equals(Environment.GetEnvironmentVariable("ADIMI_DUEL_DISTANCE_MARKERS"), "0", StringComparison.Ordinal);
        AdimiToolsConsoleLog.Log(_enabled
            ? $"Duel distance markers enabled: restore <={AdimiToolsDuelMarkerVisibility<MissionPeer>.RestoreDistance}m, hide >{AdimiToolsDuelMarkerVisibility<MissionPeer>.HideDistance}m, interval {UpdateInterval}s."
            : "Duel distance markers disabled (environment switch or no valid area_flag_N).");
    }

    public void Tick(float dt)
    {
        if ((!_enabled && _recoveryTargets.Count == 0) || !float.IsFinite(dt) || dt <= 0f)
        {
            return;
        }

        _elapsed += dt;
        if (_elapsed < UpdateInterval)
        {
            return;
        }

        _elapsed = 0f;
        var peers = GameNetwork.NetworkPeers
            .Where(p => !p.IsServerPeer && p.IsConnectionActive && p.IsSynchronized)
            .Select(p => p.GetComponent<MissionPeer>())
            .Where(p => p != null && p.Culture != null && p.SelectedTroopIndex >= 0)
            .ToList();
        if (!_enabled)
        {
            RecoverMarkers(peers);
            return;
        }

        // Do not rescan all duel lists for every observer/target pair.
        var inAiChallenge = peers.ToDictionary(p => p, p => _isInAiChallenge(p));
        var targetBusy = peers.ToDictionary(p => p, p => (!inAiChallenge[p] && p.Team != _mission.AttackerTeam) || _isBusy(p, false));
        var observerBusy = peers.ToDictionary(p => p, p => _isBusy(p, true));
        var positions = peers.ToDictionary(p => p, p => p.ControlledAgent?.Position);
        var active = peers.ToDictionary(p => p, p => p.ControlledAgent?.IsActive() ?? false);
        foreach (MissionPeer observer in peers)
        {
            if (!active[observer] || (!inAiChallenge[observer] && observer.Team != _mission.AttackerTeam) || observerBusy[observer])
            {
                continue;
            }

            var targets = peers.Select(target => new DuelMarkerTarget<MissionPeer>(
                target,
                positions[target].HasValue ? positions[observer].GetValueOrDefault().DistanceSquared(positions[target].GetValueOrDefault()) : float.NaN,
                active[target],
                targetBusy[target],
                MustHide: inAiChallenge[observer] || inAiChallenge[target]));
            DuelMarkerBatch<MissionPeer> batch = _visibility.Plan(observer, targets, canUpdate: true);
            if (!batch.IsEmpty)
            {
                SendBatch(observer, batch);
                if (!_enabled)
                {
                    return;
                }
            }
        }
    }

    public void OnRealDuelEnded(MissionPeer first, MissionPeer second)
    {
        _visibility.OnRealDuelEnded(first, second);
    }

    public void OnTargetRestored(MissionPeer observer, MissionPeer target)
    {
        _visibility.OnTargetRestored(observer, target);
        if (_recoveryTargets.TryGetValue(observer, out HashSet<MissionPeer>? targets))
        {
            targets.Remove(target);
        }
    }

    public void ResetObserver(MissionPeer observer)
    {
        _visibility.ResetObserver(observer);
        _recoveryTargets.Remove(observer);
    }

    public void RemovePeer(MissionPeer peer)
    {
        _visibility.RemovePeer(peer);
        _recoveryTargets.Remove(peer);
        foreach (HashSet<MissionPeer> targets in _recoveryTargets.Values)
        {
            targets.Remove(peer);
        }
    }

    private static int FindArenaIndex(Mission mission)
    {
        foreach (GameEntity entity in mission.Scene.FindEntitiesWithTagExpression("area_flag(_\\d+)*"))
        {
            // Match the first area_flag tag read by the original client VM.
            string? tag = entity.Tags.FirstOrDefault(t => t.StartsWith("area_flag", StringComparison.Ordinal));
            if (tag != null && tag.StartsWith("area_flag_", StringComparison.Ordinal)
                && int.TryParse(tag.Substring("area_flag_".Length), out int number) && number >= 1 && number <= 5)
            {
                return number - 1;
            }
        }

        return -1;
    }

    private void SendBatch(MissionPeer observer, DuelMarkerBatch<MissionPeer> batch)
    {
        NetworkCommunicator networkPeer = observer.GetNetworkPeer();
        try
        {
            foreach (DuelMarkerOperation<MissionPeer> operation in batch.Operations())
            {
                SendOperation(networkPeer, operation);
            }

            _visibility.Commit(batch);
        }
        catch (Exception exception)
        {
            // Stop further synthetic state, close the current personal panel,
            // and leave an explicit diagnostic for this unsupported failure.
            _enabled = false;
            AdimiToolsConsoleLog.Log($"Duel distance markers stopped after send failure: {exception}");
            foreach (KeyValuePair<MissionPeer, MissionPeer[]> pair in _visibility.GetHiddenSnapshot())
            {
                _recoveryTargets[pair.Key] = new HashSet<MissionPeer>(pair.Value);
            }

            if (!_recoveryTargets.TryGetValue(observer, out HashSet<MissionPeer>? uncertain))
            {
                uncertain = new();
                _recoveryTargets.Add(observer, uncertain);
            }

            // Either side of a partially sent batch may still be hidden.
            uncertain.UnionWith(batch.Hide);
            uncertain.UnionWith(batch.Restore);
            try
            {
                SendOperation(networkPeer, new(DuelMarkerOperationKind.EndSelf, observer));
            }
            catch (Exception recoveryException)
            {
                AdimiToolsConsoleLog.Log($"Duel marker cleanup failed: {recoveryException.Message}");
            }
        }
    }

    private void RecoverMarkers(IReadOnlyCollection<MissionPeer> connectedPeers)
    {
        foreach (KeyValuePair<MissionPeer, HashSet<MissionPeer>> entry in _recoveryTargets.ToArray())
        {
            MissionPeer observer = entry.Key;
            if (!connectedPeers.Contains(observer) || observer.Team != _mission.AttackerTeam
                || !(observer.ControlledAgent?.IsActive() ?? false) || _isBusy(observer, true) || _isInAiChallenge(observer))
            {
                continue;
            }

            NetworkCommunicator receiver = observer.GetNetworkPeer();
            try
            {
                // Clear a partially applied personal panel without restoring
                // its former opponent, who may now be in a real duel.
                SendOperation(receiver, new(DuelMarkerOperationKind.StartSelf, observer));
                SendOperation(receiver, new(DuelMarkerOperationKind.EndSelf, observer));
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Duel marker panel cleanup deferred: {exception.Message}");
                continue;
            }

            int restored = 0;
            foreach (MissionPeer target in entry.Value.ToArray())
            {
                if (restored >= AdimiToolsDuelMarkerVisibility<MissionPeer>.MaximumChangesPerBatch)
                {
                    break;
                }

                if (!connectedPeers.Contains(target))
                {
                    continue;
                }

                if (_isBusy(target, false))
                {
                    // Real duel participants are restored by their genuine
                    // DuelEnded, not synthetic UI.
                    entry.Value.Remove(target);
                    continue;
                }

                if (_isInAiChallenge(target))
                {
                    // AI participants receive no genuine DuelEnded. Retain the
                    // queued restoration until their complete series ends.
                    continue;
                }

                if (target.Team != _mission.AttackerTeam)
                {
                    // Spectator/null-team transitions do not emit DuelEnded.
                    // Keep the cached target until it returns or disconnects.
                    continue;
                }

                try
                {
                    SendOperation(receiver, new(DuelMarkerOperationKind.StartTarget, target));
                    SendOperation(receiver, new(DuelMarkerOperationKind.EndSelf, observer));
                    entry.Value.Remove(target);
                    restored++;
                }
                catch (Exception exception)
                {
                    AdimiToolsConsoleLog.Log($"Duel marker cleanup deferred: {exception.Message}");
                    break;
                }
            }

            if (entry.Value.Count == 0)
            {
                _visibility.ResetObserver(observer);
                _recoveryTargets.Remove(observer);
            }
        }
    }

    private void SendOperation(NetworkCommunicator observer, DuelMarkerOperation<MissionPeer> operation)
    {
        GameNetwork.BeginModuleEventAsServer(observer);
        try
        {
            if (operation.Kind == DuelMarkerOperationKind.EndSelf)
            {
                GameNetwork.WriteMessage(new DuelEnded(observer));
            }
            else
            {
                GameNetwork.WriteMessage(new DuelPreparationStartedForTheFirstTime(
                    observer, operation.Peer.GetNetworkPeer(), _arenaIndex));
            }
        }
        finally
        {
            GameNetwork.EndModuleEventAsServer();
        }
    }
}
