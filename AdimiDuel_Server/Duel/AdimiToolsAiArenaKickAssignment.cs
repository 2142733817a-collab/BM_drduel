using System;
using System.Collections.Generic;

namespace AdimiDuel_Server.Duel;

// One mission-thread-owned instance per arena. The caller supplies only living
// on-foot bots targeting this player, with finite centre-distance squared.
// This policy never reads an Agent and never changes the selected bot after
// the kick's rising edge, even if positions or eligibility later change.
internal sealed class AdimiToolsAiArenaKickAssignment
{
    internal const double LockAfterKickSeconds = 1.8d;
    internal const double ShortGapSeconds = 0.12d;
    internal const double PendingRetainSeconds = 0.25d;
    internal const double MaximumLockAfterKickSeconds = 3.4d;

    private sealed class PlayerState
    {
        public bool Kicking;
        public bool HasKick;
        public int BotIndex = -1;
        public double LockedUntil;
        public double KickEndedAt;
    }

    private readonly Dictionary<int, PlayerState> _players = new();
    private readonly HashSet<int> _observed = new();
    private readonly List<int> _unobserved = new();
    private double _now;
    private double _lastTime;
    private bool _hasTime;
    private bool _sampling;

    public void BeginSampling(double now)
    {
        if (!double.IsFinite(now) || now < 0d)
        {
            Clear();
            return;
        }

        // A mission clock reset invalidates every old player and lock, while
        // allowing the current valid sample to start a fresh episode.
        if (_hasTime && now < _lastTime)
        {
            Clear();
        }

        _now = now;
        _lastTime = now;
        _hasTime = true;
        _sampling = true;
        _observed.Clear();
    }

    public void Observe(int playerIndex, bool kicking, IReadOnlyList<(int BotIndex, float DistanceSquared)> candidates)
    {
        if (!_sampling || playerIndex < 0 || !_observed.Add(playerIndex))
        {
            return;
        }

        if (!_players.TryGetValue(playerIndex, out PlayerState? state))
        {
            state = new PlayerState();
            _players.Add(playerIndex, state);
        }

        if (kicking && !state.Kicking)
        {
            // A short gap between Native animation phases still belongs to the
            // same kick. Retain even a no-candidate result through that gap.
            bool sameKick = state.HasKick && _now <= state.KickEndedAt + ShortGapSeconds;
            // A genuinely new kick gets exactly one selection, including the
            // no-candidate outcome. Late arrivals cannot claim an old kick.
            if (!sameKick)
            {
                state.BotIndex = ChooseClosest(playerIndex, candidates);
            }

            state.Kicking = true;
            state.HasKick = true;
            state.LockedUntil = 0d;
            state.KickEndedAt = 0d;
        }
        else if (!kicking && state.Kicking)
        {
            state.Kicking = false;
            state.KickEndedAt = _now;
            state.LockedUntil = _now + LockAfterKickSeconds;
        }
        else if (!kicking && state.HasKick && _now >= state.LockedUntil)
        {
            state.HasKick = false;
            state.BotIndex = -1;
            state.LockedUntil = 0d;
            state.KickEndedAt = 0d;
        }
    }

    public void EndSampling()
    {
        if (!_sampling)
        {
            return;
        }

        // Players not sampled in this arena have died, left, withdrawn, or
        // otherwise ceased to be eligible; stale locks must not survive.
        _unobserved.Clear();
        foreach (int player in _players.Keys)
        {
            if (!_observed.Contains(player))
            {
                _unobserved.Add(player);
            }
        }

        foreach (int player in _unobserved)
        {
            _players.Remove(player);
        }

        _sampling = false;
    }

    // True also when this kick had no eligible candidate, allowing the caller
    // to keep all bots at normal spacing rather than choose a late arrival.
    public bool HasActiveKick(int playerIndex)
    {
        return _players.TryGetValue(playerIndex, out PlayerState? state) && IsActive(state);
    }

    // Treat the short gap as still kicking when feeding a missed-kick tracker;
    // otherwise it could mistake a single animation's gap for a completed miss
    // before a KickContinue/KickHit phase resumes.
    public bool IsKickOngoing(int playerIndex)
    {
        return _players.TryGetValue(playerIndex, out PlayerState? state) && state.HasKick
            && (state.Kicking || _now <= state.KickEndedAt + ShortGapSeconds);
    }

    public bool TryGetAssigned(int playerIndex, out int botIndex)
    {
        botIndex = -1;
        if (!_players.TryGetValue(playerIndex, out PlayerState? state) || !IsActive(state) || state.BotIndex < 0)
        {
            return false;
        }

        botIndex = state.BotIndex;
        return true;
    }

    // Only for the caller's BeginSampling -> Observe interval. At this point a
    // previous lock may have just expired by mission time, but Observe has not
    // yet discarded the saved identity. This does not make it active again.
    public bool TryGetRecordedAssignee(int playerIndex, out int botIndex)
    {
        botIndex = -1;
        if (!_sampling || _observed.Contains(playerIndex)
            || !_players.TryGetValue(playerIndex, out PlayerState? state) || !state.HasKick || state.BotIndex < 0)
        {
            return false;
        }

        botIndex = state.BotIndex;
        return true;
    }

    // Call only while this exact bot still has a pending or active response.
    // Never transfer ownership, shorten an existing lock, extend a kick which
    // is still playing/in the short animation gap, or exceed the hard cap.
    public bool RetainForPending(int playerIndex, int botIndex)
    {
        if (!_sampling || _observed.Contains(playerIndex) || botIndex < 0
            || !_players.TryGetValue(playerIndex, out PlayerState? state) || !state.HasKick
            || state.BotIndex != botIndex || state.Kicking || _now <= state.KickEndedAt + ShortGapSeconds)
        {
            return false;
        }

        double limit = state.KickEndedAt + MaximumLockAfterKickSeconds;
        if (_now >= limit)
        {
            return false;
        }

        double retainedUntil = Math.Min(_now + PendingRetainSeconds, limit);
        if (retainedUntil > state.LockedUntil)
        {
            state.LockedUntil = retainedUntil;
        }

        return true;
    }

    public void Clear()
    {
        _players.Clear();
        _observed.Clear();
        _unobserved.Clear();
        _now = 0d;
        _lastTime = 0d;
        _hasTime = false;
        _sampling = false;
    }

    private bool IsActive(PlayerState state) => state.HasKick && (state.Kicking || _now < state.LockedUntil);

    private static int ChooseClosest(int playerIndex, IReadOnlyList<(int BotIndex, float DistanceSquared)> candidates)
    {
        if (candidates == null)
        {
            return -1;
        }

        int selected = -1;
        float closest = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            (int botIndex, float distanceSquared) = candidates[i];
            if (botIndex < 0 || botIndex == playerIndex || !float.IsFinite(distanceSquared) || distanceSquared < 0f)
            {
                continue;
            }

            if (distanceSquared < closest || (distanceSquared == closest && (selected < 0 || botIndex < selected)))
            {
                selected = botIndex;
                closest = distanceSquared;
            }
        }

        return selected;
    }
}
