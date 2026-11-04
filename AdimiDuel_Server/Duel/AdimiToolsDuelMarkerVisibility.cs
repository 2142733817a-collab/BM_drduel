namespace AdimiDuel_Server.Duel;

/// <summary>Distance and AI reservation policy independent of engine, network and client UI objects.</summary>
internal sealed class AdimiToolsDuelMarkerVisibility<TPeer>
    where TPeer : notnull
{
    public const float RestoreDistance = 50f;
    public const float HideDistance = 55f;
    public const int MaximumChangesPerBatch = 8;

    private readonly Dictionary<TPeer, HashSet<TPeer>> _hidden = new();

    public DuelMarkerBatch<TPeer> Plan(TPeer observer, IEnumerable<DuelMarkerTarget<TPeer>> targets, bool canUpdate)
    {
        var hide = new List<TPeer>();
        var forcedHide = new List<TPeer>();
        var restore = new List<TPeer>();
        if (!canUpdate)
        {
            return new(observer, hide, restore);
        }

        _hidden.TryGetValue(observer, out HashSet<TPeer>? hidden);
        var seen = new HashSet<TPeer>();
        foreach (DuelMarkerTarget<TPeer> target in targets)
        {
            if (EqualityComparer<TPeer>.Default.Equals(target.Peer, observer)
                || !seen.Add(target.Peer)
                || !target.IsActive
                || target.IsInDuel)
            {
                continue;
            }

            bool wasHidden = hidden?.Contains(target.Peer) ?? false;
            if (target.MustHide)
            {
                if (!wasHidden)
                {
                    forcedHide.Add(target.Peer);
                }

                continue;
            }

            if (!float.IsFinite(target.DistanceSquared) || target.DistanceSquared < 0f)
            {
                continue;
            }

            if (wasHidden && target.DistanceSquared <= RestoreDistance * RestoreDistance)
            {
                restore.Add(target.Peer);
            }
            else if (!wasHidden && target.DistanceSquared > HideDistance * HideDistance)
            {
                hide.Add(target.Peer);
            }
        }

        // AI participation takes priority over ordinary distance changes.
        forcedHide = forcedHide.Take(MaximumChangesPerBatch).ToList();
        restore = restore.Take(MaximumChangesPerBatch - forcedHide.Count).ToList();
        hide = forcedHide.Concat(hide.Take(MaximumChangesPerBatch - forcedHide.Count - restore.Count)).ToList();
        return new(observer, hide, restore);
    }

    // A plan changes our knowledge only once all native sends completed.
    public void Commit(DuelMarkerBatch<TPeer> batch)
    {
        if (batch.IsEmpty)
        {
            return;
        }

        if (!_hidden.TryGetValue(batch.Observer, out HashSet<TPeer>? hidden))
        {
            hidden = new();
            _hidden.Add(batch.Observer, hidden);
        }

        hidden.ExceptWith(batch.Restore);
        hidden.UnionWith(batch.Hide);
    }

    // The real DuelEnded message restores both participants in every client.
    public void OnRealDuelEnded(TPeer first, TPeer second)
    {
        foreach (HashSet<TPeer> hidden in _hidden.Values)
        {
            hidden.Remove(first);
            hidden.Remove(second);
        }
    }

    // A directed AI display DuelEnded restores this target in one client only.
    public void OnTargetRestored(TPeer observer, TPeer target)
    {
        if (_hidden.TryGetValue(observer, out HashSet<TPeer>? hidden))
        {
            hidden.Remove(target);
        }
    }

    public void ResetObserver(TPeer observer)
    {
        _hidden.Remove(observer);
    }

    public Dictionary<TPeer, TPeer[]> GetHiddenSnapshot()
    {
        return _hidden.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public void RemovePeer(TPeer peer)
    {
        _hidden.Remove(peer);
        foreach (HashSet<TPeer> hidden in _hidden.Values)
        {
            hidden.Remove(peer);
        }
    }
}

internal enum DuelMarkerOperationKind
{
    StartTarget,
    StartSelf,
    EndSelf,
}

internal readonly record struct DuelMarkerTarget<TPeer>(TPeer Peer, float DistanceSquared, bool IsActive, bool IsInDuel, bool MustHide = false)
    where TPeer : notnull;

internal readonly record struct DuelMarkerOperation<TPeer>(DuelMarkerOperationKind Kind, TPeer Peer)
    where TPeer : notnull;

internal sealed class DuelMarkerBatch<TPeer>
    where TPeer : notnull
{
    public TPeer Observer { get; }
    public IReadOnlyList<TPeer> Hide { get; }
    public IReadOnlyList<TPeer> Restore { get; }
    public bool IsEmpty => Hide.Count == 0 && Restore.Count == 0;

    public DuelMarkerBatch(TPeer observer, IReadOnlyList<TPeer> hide, IReadOnlyList<TPeer> restore)
    {
        Observer = observer;
        Hide = hide;
        Restore = restore;
    }

    // Every operation is its own reliable native module event. This is ordered,
    // not an assertion that the native client renders the whole batch atomically.
    public IEnumerable<DuelMarkerOperation<TPeer>> Operations()
    {
        foreach (TPeer peer in Restore)
        {
            yield return new(DuelMarkerOperationKind.StartTarget, peer);
            yield return new(DuelMarkerOperationKind.EndSelf, Observer);
        }

        if (Hide.Count > 0)
        {
            foreach (TPeer peer in Hide)
            {
                yield return new(DuelMarkerOperationKind.StartTarget, peer);
            }

            yield return new(DuelMarkerOperationKind.StartSelf, Observer);
            yield return new(DuelMarkerOperationKind.EndSelf, Observer);
        }
    }
}
