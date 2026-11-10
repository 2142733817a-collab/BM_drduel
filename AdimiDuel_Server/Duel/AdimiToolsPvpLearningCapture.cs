namespace AdimiDuel_Server.Duel;

// A round recorder with no live engine references. The mission adapter assigns
// each human to a side and passes values only; opponents are not AI actors.
internal sealed class AdimiToolsPvpLearningCapture
{
    internal readonly record struct Participant(int Id, int Side, string Token);

    private const int MaxFrames = 8192;
    private const int MaxActors = 16384;
    private const int MaxEvents = 4096;
    private const double MaxSeconds = 600d;
    private readonly object _gate = new();
    private readonly Dictionary<int, Participant> _participants;
    private readonly Dictionary<int, (int Attacker, int Owner, double Seconds, int State)> _deaths = new();
    private readonly AdimiToolsAiLearningEpisode _episode;
    private readonly double _startedAt;
    private readonly double _earlyInterval;
    private readonly double _lateInterval;
    private double _nextSample;
    private int _actorCount;
    private bool _invalid;
    private bool _closed;
    private bool _combatEnded;

    internal AdimiToolsPvpLearningCapture(string matchType, int arenaId, double startedAt,
        IEnumerable<Participant> participants)
    {
        if (matchType is not ("pvp" or "team_pvp")) throw new ArgumentOutOfRangeException(nameof(matchType));
        _participants = participants.ToDictionary(p => p.Id);
        _startedAt = startedAt;
        _nextSample = startedAt;
        int count = _participants.Count;
        double baseInterval = count <= 4 ? .05d : count <= 8 ? .1d : .2d;
        _earlyInterval = Math.Max(baseInterval, count / 80d);
        _lateInterval = Math.Max(.2d, count * .08d);
        _episode = new AdimiToolsAiLearningEpisode
        {
            SchemaVersion = 2,
            Id = Guid.NewGuid().ToString("N"),
            MatchType = matchType,
            ArenaId = arenaId,
            StartedUtc = DateTime.UtcNow.ToString("O"),
            InitialPlayers = count,
            InitialBots = 0,
        };
        if (!double.IsFinite(startedAt) || count < 2 || _participants.Values.Any(p => p.Side is not (0 or 1))
            || !_participants.Values.Any(p => p.Side == 0) || !_participants.Values.Any(p => p.Side == 1)
            || (matchType == "pvp" && count != 2)) _invalid = true;
    }

    internal IReadOnlyCollection<int> ActorIds => _participants.Keys;

    internal bool TryGetParticipant(int id, out Participant participant) => _participants.TryGetValue(id, out participant);

    internal bool IsRemoved(int id) { lock (_gate) return _deaths.ContainsKey(id); }

    internal bool IsTerminal { get { lock (_gate) return _combatEnded || _closed; } }

    internal void Invalidate() { lock (_gate) _invalid = true; }

    internal bool ShouldSample(double now)
    {
        lock (_gate)
        {
            if (_closed || _combatEnded || _episode.Truncated || !TrySeconds(now, out double seconds) || now + .000001d < _nextSample) return false;
            _nextSample = now + (seconds < 120d ? _earlyInterval : _lateInterval);
            return true;
        }
    }

    internal void AddFrame(double now, List<AdimiToolsAiLearningActor> actors)
    {
        lock (_gate)
        {
            if (_closed || _combatEnded || _episode.Truncated || !TrySeconds(now, out double seconds)) return;
            if (_episode.Frames.Count >= MaxFrames || actors.Count > MaxActors - _actorCount)
            {
                Truncate();
                return;
            }
            if (actors.Count == 0 || (_episode.Frames.Count == 0 && actors.Count != _participants.Count)
                || actors.Select(a => a.Id).Distinct().Count() != actors.Count
                || _participants.Keys.Any(id => !_deaths.ContainsKey(id) && !actors.Any(a => a.Id == id))
                || actors.Any(a => !ActorValid(a)))
            {
                _invalid = true;
                return;
            }
            _episode.Frames.Add(new AdimiToolsAiLearningFrame { Seconds = seconds, Actors = actors });
            _actorCount += actors.Count;
        }
    }

    internal void RecordHit(double now, int attackerId, int victimId, bool blocked, float damage,
        bool missile, int attackDirection, float attackProgress, int strikeType, bool thrustTipHit, int? attackerOwner = null,
        bool allowLateMissile = false, string attackerWeaponClass = "", bool? isAlternativeAttack = null)
    {
        lock (_gate)
        {
            if (_closed || (_combatEnded && !(allowLateMissile && missile)) || _episode.Truncated || !TrySeconds(now, out double seconds)) return;
            if (_combatEnded) _invalid = true;
            if (!_participants.ContainsKey(attackerOwner ?? attackerId) || !_participants.ContainsKey(victimId)
                || !float.IsFinite(damage) || damage < 0f || !float.IsFinite(attackProgress)
                || attackerWeaponClass == null || attackerWeaponClass.Length > 64)
            {
                _invalid = true;
                return;
            }
            AddEvent(new AdimiToolsAiLearningEvent { Kind = "hit", Seconds = seconds, AttackerId = attackerId,
                VictimId = victimId, IsBlocked = blocked, Damage = damage, IsMissile = missile,
                AttackDirection = attackDirection, AttackProgress = attackProgress, StrikeType = strikeType,
                ThrustTipHit = thrustTipHit, AttackerWeaponClass = attackerWeaponClass, IsAlternativeAttack = isAlternativeAttack });
        }
    }

    // The adapter resolves each mount to a participant before calling this;
    // the actual mount index is retained for replay and never counts as a death.
    internal void RecordMountHit(double now, int attackerId, int victimMountId, bool blocked, float damage,
        bool missile, int attackDirection, float attackProgress, int strikeType, bool thrustTipHit, int? attackerOwner = null,
        bool allowLateMissile = false, string attackerWeaponClass = "", bool? isAlternativeAttack = null)
    {
        lock (_gate)
        {
            if (_closed || (_combatEnded && !(allowLateMissile && missile)) || _episode.Truncated || !TrySeconds(now, out double seconds)) return;
            if (_combatEnded) _invalid = true;
            if (!_participants.ContainsKey(attackerOwner ?? attackerId) || victimMountId < 0 || !float.IsFinite(damage)
                || damage < 0f || !float.IsFinite(attackProgress) || attackerWeaponClass == null || attackerWeaponClass.Length > 64) { _invalid = true; return; }
            AddEvent(new AdimiToolsAiLearningEvent { Kind = "mount_hit", Seconds = seconds, AttackerId = attackerId,
                VictimId = victimMountId, IsBlocked = blocked, Damage = damage, IsMissile = missile,
                AttackDirection = attackDirection, AttackProgress = attackProgress, StrikeType = strikeType,
                ThrustTipHit = thrustTipHit, AttackerWeaponClass = attackerWeaponClass, IsAlternativeAttack = isAlternativeAttack });
        }
    }

    internal void RecordRemoval(double now, int attackerId, int victimId, bool died, int removalState, int? attackerOwner = null)
    {
        lock (_gate)
        {
            if (_closed || !_participants.TryGetValue(victimId, out Participant victim)) return;
            if (_deaths.TryGetValue(victimId, out var previous))
            {
                if (previous.Attacker != attackerId || previous.Owner != (attackerOwner ?? attackerId) || previous.State != removalState) _invalid = true;
                return;
            }
            if (!TrySeconds(now, out double seconds)) return;
            if (!died || !_participants.ContainsKey(attackerOwner ?? attackerId) || (attackerOwner ?? attackerId) == victimId)
            {
                _invalid = true;
                return;
            }
            _deaths.Add(victimId, (attackerId, attackerOwner ?? attackerId, seconds, removalState));
            if (!_episode.Truncated) AddEvent(new AdimiToolsAiLearningEvent { Kind = "removal", Seconds = seconds,
                AttackerId = attackerId, VictimId = victimId, RemovalState = removalState });
            if (!_participants.Values.Any(p => p.Side == victim.Side && !_deaths.ContainsKey(p.Id))) _combatEnded = true;
        }
    }

    internal void RecordAuxiliaryEvent(double now, AdimiToolsAiLearningEvent item)
    {
        lock (_gate)
        {
            if (_closed || _combatEnded || _episode.Truncated || !TrySeconds(now, out double seconds)) return;
            if (item == null || item.Kind is not ("mount" or "dismount" or "mount_removal" or "shot")
                || !float.IsFinite(item.ShotX) || !float.IsFinite(item.ShotY) || !float.IsFinite(item.ShotZ)
                || !float.IsFinite(item.ShotVelocityX) || !float.IsFinite(item.ShotVelocityY) || !float.IsFinite(item.ShotVelocityZ))
            {
                _invalid = true;
                return;
            }
            item.Seconds = seconds;
            AddEvent(item);
        }
    }

    internal AdimiToolsAiLearningEpisode? Finish(double now, int? winningSide, bool interrupted)
    {
        lock (_gate)
        {
            if (_closed) return null;
            _closed = true;
            bool sideKnown = winningSide is 0 or 1;
            Participant[] losers = sideKnown ? _participants.Values.Where(p => p.Side != winningSide).ToArray() : Array.Empty<Participant>();
            bool confirmed = losers.Length != 0 && losers.All(p => _deaths.ContainsKey(p.Id));
            bool winnerAlive = sideKnown && _participants.Values.Any(p => p.Side == winningSide && !_deaths.ContainsKey(p.Id));
            double elapsed = double.IsFinite(now) && double.IsFinite(_startedAt) ? Math.Max(0d, now - _startedAt) : 0d;
            _episode.DurationSeconds = confirmed ? losers.Max(p => _deaths[p.Id].Seconds) : elapsed;
            if (_episode.Events.Count != 0) _episode.DurationSeconds = Math.Max(_episode.DurationSeconds, _episode.Events.Max(e => e.Seconds));
            if (_episode.DurationSeconds > MaxSeconds) Truncate();
            _episode.WinningSide = sideKnown ? winningSide!.Value : -1;
            _episode.Outcome = sideKnown ? (winningSide == 0 ? "side_a_win" : "side_b_win") : "aborted";
            _episode.Valid = sideKnown && !interrupted && !_invalid && !_episode.Truncated && confirmed && winnerAlive
                && _episode.Frames.Count >= 2;
            _episode.SoloAttributedWin = false;
            return _episode;
        }
    }

    private bool ActorValid(AdimiToolsAiLearningActor actor)
        => actor != null && _participants.TryGetValue(actor.Id, out Participant p) && actor.IsPlayer && actor.Side == p.Side
            && float.IsFinite(actor.X) && float.IsFinite(actor.Y) && float.IsFinite(actor.Z)
            && float.IsFinite(actor.LookX) && float.IsFinite(actor.LookY) && float.IsFinite(actor.LookZ)
            && float.IsFinite(actor.VelocityX) && float.IsFinite(actor.VelocityY)
            && float.IsFinite(actor.MoveInputX) && float.IsFinite(actor.MoveInputY)
            && float.IsFinite(actor.MountInputX) && float.IsFinite(actor.MountInputY)
            && float.IsFinite(actor.Health) && float.IsFinite(actor.HealthLimit) && actor.Health >= 0f && actor.HealthLimit > 0f
            && float.IsFinite(actor.ActionProgress) && float.IsFinite(actor.WeaponLength) && actor.WeaponLength >= 0f
            && float.IsFinite(actor.MountX) && float.IsFinite(actor.MountY) && float.IsFinite(actor.MountZ)
            && float.IsFinite(actor.MountLookX) && float.IsFinite(actor.MountLookY) && float.IsFinite(actor.MountLookZ)
            && float.IsFinite(actor.MountVelocityX) && float.IsFinite(actor.MountVelocityY)
            && float.IsFinite(actor.MountHealth) && float.IsFinite(actor.MountHealthLimit)
            && (!actor.IsMounted || (actor.MountId >= 0 && actor.MountHealth > 0f && actor.MountHealthLimit >= actor.MountHealth));

    private bool TrySeconds(double now, out double seconds)
    {
        seconds = now - _startedAt;
        if (!double.IsFinite(seconds) || seconds < 0d) { _invalid = true; seconds = 0d; return false; }
        if (seconds > MaxSeconds) { Truncate(); return false; }
        return true;
    }

    private void AddEvent(AdimiToolsAiLearningEvent item)
    {
        if (_episode.Events.Count >= MaxEvents) { Truncate(); return; }
        _episode.Events.Add(item);
    }

    private void Truncate() { _episode.Truncated = true; _invalid = true; }
}
