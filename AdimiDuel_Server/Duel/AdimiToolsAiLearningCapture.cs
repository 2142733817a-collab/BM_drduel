using TaleWorlds.Core;

namespace AdimiDuel_Server.Duel;

// This recorder owns detached values only. Engine objects never enter the
// asynchronous writer, and a closed recorder ignores late native callbacks.
internal sealed class AdimiToolsAiLearningCapture
{
    internal readonly record struct Participant(int Id, bool IsPlayer, string Token);
    private const int MaxFrames = 8192;
    private const int MaxActors = 16384;
    private const int MaxEvents = 4096;
    private const double MaxSeconds = 600d;
    private readonly object _gate = new();
    private readonly Dictionary<int, Participant> _participants;
    private readonly Dictionary<int, (int Attacker, int AttackerParticipant, double Seconds, AgentState State)> _deaths = new();
    private readonly HashSet<int> _knownMounts = new();
    private readonly HashSet<int> _removedMounts = new();
    private readonly AdimiToolsAiLearningEpisode _episode;
    private readonly double _startedAt;
    private readonly double _sampleInterval;
    private double _nextSample;
    private int _actorCount;
    private bool _invalid;
    private bool _closed;

    public AdimiToolsAiLearningCapture(int arenaId, int arenaKind, int difficulty, double startedAt, IEnumerable<Participant> participants)
    {
        _participants = participants.ToDictionary(p => p.Id);
        _startedAt = startedAt;
        _nextSample = startedAt;
        double baseInterval = _participants.Count <= 4 ? .05d : _participants.Count <= 8 ? .1d : .2d;
        _sampleInterval = Math.Max(baseInterval, _participants.Count / 80d);
        _episode = new AdimiToolsAiLearningEpisode
        {
            SchemaVersion = 2, MatchType = "ai", Id = Guid.NewGuid().ToString("N"), ArenaId = arenaId, ArenaKind = arenaKind, Difficulty = difficulty,
            StartedUtc = DateTime.UtcNow.ToString("O"),
            InitialPlayers = _participants.Values.Count(p => p.IsPlayer),
            InitialBots = _participants.Values.Count(p => !p.IsPlayer)
        };
        if (!double.IsFinite(startedAt) || _episode.InitialPlayers == 0 || _episode.InitialBots == 0)
            _invalid = true;
    }

    public IReadOnlyCollection<int> ActorIds => _participants.Keys;

    public bool TryGetParticipant(int id, out Participant participant) => _participants.TryGetValue(id, out participant);

    public bool IsRemoved(int id)
    {
        lock (_gate) return _deaths.ContainsKey(id);
    }

    public bool HasEnded
    {
        get { lock (_gate) return _closed || IsTerminal(); }
    }

    public void Invalidate()
    {
        lock (_gate) _invalid = true;
    }

    public bool ShouldSample(double now)
    {
        lock (_gate)
        {
            if (_closed || _episode.Truncated || IsTerminal()) return false;
            if (!TrySeconds(now, out double seconds)) return false;
            if (now + .000001d < _nextSample) return false;
            double interval = seconds < 120d ? _sampleInterval : Math.Max(.2d, _participants.Count * .08d);
            _nextSample = now + interval;
            return true;
        }
    }

    public void AddFrame(double now, List<AdimiToolsAiLearningActor> actors)
    {
        lock (_gate)
        {
            if (_closed || _episode.Truncated || IsTerminal() || !TrySeconds(now, out double seconds)) return;
            if (_episode.Frames.Count >= MaxFrames || actors.Count > MaxActors - _actorCount)
            {
                Truncate();
                return;
            }
            var actorIds = new HashSet<int>(actors.Select(a => a.Id));
            if (actors.Count == 0 || (_episode.Frames.Count == 0 && actors.Count != _participants.Count)
                || actorIds.Count != actors.Count
                || _participants.Keys.Any(id => !_deaths.ContainsKey(id) && !actorIds.Contains(id))
                || actors.Any(a => !_participants.TryGetValue(a.Id, out Participant participant)
                || a.IsPlayer != participant.IsPlayer || a.Side != (participant.IsPlayer ? 0 : 1)
                || !float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(a.Z)
                || !float.IsFinite(a.LookX) || !float.IsFinite(a.LookY) || !float.IsFinite(a.LookZ)
                || !float.IsFinite(a.VelocityX) || !float.IsFinite(a.VelocityY)
                || !float.IsFinite(a.MoveInputX) || !float.IsFinite(a.MoveInputY)
                || !float.IsFinite(a.Health) || !float.IsFinite(a.HealthLimit) || !float.IsFinite(a.ActionProgress)
                || !float.IsFinite(a.MountX) || !float.IsFinite(a.MountY) || !float.IsFinite(a.MountZ)
                || !float.IsFinite(a.MountLookX) || !float.IsFinite(a.MountLookY) || !float.IsFinite(a.MountLookZ)
                || !float.IsFinite(a.MountVelocityX) || !float.IsFinite(a.MountVelocityY)
                || !float.IsFinite(a.MountInputX) || !float.IsFinite(a.MountInputY)
                || !float.IsFinite(a.MountHealth) || !float.IsFinite(a.MountHealthLimit) || !float.IsFinite(a.WeaponLength)
                || a.Health < 0 || a.HealthLimit <= 0 || a.WeaponLength < 0
                || (!a.IsMounted && a.MountId != -1)
                || (a.IsMounted && (a.MountId < 0 || a.MountHealth <= 0 || a.MountHealthLimit < a.MountHealth))))
            {
                _invalid = true;
                return;
            }
            _episode.Frames.Add(new AdimiToolsAiLearningFrame { Seconds = seconds, Actors = actors });
            _actorCount += actors.Count;
        }
    }

    public bool RegisterMount(int mountId)
    {
        lock (_gate)
        {
            if (_closed || mountId < 0 || _removedMounts.Contains(mountId)) { _invalid = true; return false; }
            _knownMounts.Add(mountId);
            return true;
        }
    }

    public void RecordHit(double now, int attackerId, int victimId, bool blocked, float damage, int? attackerParticipantId = null,
        bool isMissile = false, int attackDirection = 0, float attackProgress = 0f, int strikeType = 0, bool thrustTipHit = false,
        string attackerWeaponClass = "", bool? isAlternativeAttack = null)
    {
        lock (_gate)
        {
            bool posthumousMissile = isMissile && _deaths.ContainsKey(attackerParticipantId ?? attackerId);
            if (_closed || _episode.Truncated || (IsTerminal() && !posthumousMissile) || !TrySeconds(now, out double seconds)) return;
            if (!float.IsFinite(damage) || damage < 0f || !float.IsFinite(attackProgress) || attackerWeaponClass == null || attackerWeaponClass.Length > 64
                || !_participants.ContainsKey(attackerParticipantId ?? attackerId)
                || !_participants.ContainsKey(victimId))
            {
                _invalid = true;
                return;
            }
            if (_deaths.ContainsKey(attackerParticipantId ?? attackerId)) _invalid = true;
            AddEvent(new AdimiToolsAiLearningEvent { Kind = "hit", Seconds = seconds,
                AttackerId = attackerId, VictimId = victimId, IsBlocked = blocked, Damage = damage,
                IsMissile = isMissile, AttackDirection = attackDirection, AttackProgress = attackProgress, StrikeType = strikeType, ThrustTipHit = thrustTipHit,
                AttackerWeaponClass = attackerWeaponClass, IsAlternativeAttack = isAlternativeAttack });
        }
    }

    public void RecordRemoval(double now, int attackerId, int victimId, AgentState state, int? attackerParticipantId = null)
    {
        lock (_gate)
        {
            if (_closed || !_participants.ContainsKey(victimId)) return;
            int attributedAttacker = attackerParticipantId ?? attackerId;
            if (_deaths.TryGetValue(victimId, out var existing))
            {
                if (existing.Attacker != attackerId || existing.AttackerParticipant != attributedAttacker || existing.State != state) _invalid = true;
                return;
            }
            if (!TrySeconds(now, out double seconds)) return;
            if (state is not (AgentState.Killed or AgentState.Unconscious) || !_participants.ContainsKey(attributedAttacker)
                || attributedAttacker == victimId)
            {
                _invalid = true;
                return;
            }
            if (_deaths.ContainsKey(attributedAttacker)) _invalid = true;
            _deaths.Add(victimId, (attackerId, attributedAttacker, seconds, state));
            if (!_episode.Truncated)
                AddEvent(new AdimiToolsAiLearningEvent { Kind = "removal", Seconds = seconds,
                    AttackerId = attackerId, VictimId = victimId, RemovalState = (int)state });
        }
    }

    public void RecordMountHit(double now, int attackerId, int attackerParticipantId, int mountId, bool blocked, float damage,
        bool isMissile = false, int attackDirection = 0, float attackProgress = 0f, int strikeType = 0, bool thrustTipHit = false,
        string attackerWeaponClass = "", bool? isAlternativeAttack = null)
    {
        lock (_gate)
        {
            bool posthumousMissile = isMissile && _deaths.ContainsKey(attackerParticipantId);
            if (_closed || _episode.Truncated || (IsTerminal() && !posthumousMissile) || !TrySeconds(now, out double seconds)) return;
            if (!_knownMounts.Contains(mountId) || _removedMounts.Contains(mountId)
                || !_participants.ContainsKey(attackerParticipantId) || !float.IsFinite(damage) || damage < 0f || !float.IsFinite(attackProgress)
                || attackerWeaponClass == null || attackerWeaponClass.Length > 64)
            {
                _invalid = true;
                return;
            }
            if (_deaths.ContainsKey(attackerParticipantId)) _invalid = true;
            AddEvent(new AdimiToolsAiLearningEvent { Kind = "mount_hit", Seconds = seconds,
                AttackerId = attackerId, VictimId = mountId, IsBlocked = blocked, Damage = damage,
                IsMissile = isMissile, AttackDirection = attackDirection, AttackProgress = attackProgress, StrikeType = strikeType, ThrustTipHit = thrustTipHit,
                AttackerWeaponClass = attackerWeaponClass, IsAlternativeAttack = isAlternativeAttack });
        }
    }

    public void RecordShot(double now, int shooterId, int weaponSlot, float x, float y, float z, float velocityX, float velocityY, float velocityZ)
    {
        lock (_gate)
        {
            if (_closed || _episode.Truncated || IsTerminal() || !TrySeconds(now, out double seconds)) return;
            if (!_participants.ContainsKey(shooterId) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
                || !float.IsFinite(velocityX) || !float.IsFinite(velocityY) || !float.IsFinite(velocityZ))
            {
                _invalid = true;
                return;
            }
            if (_deaths.ContainsKey(shooterId)) _invalid = true;
            AddEvent(new AdimiToolsAiLearningEvent { Kind = "shot", Seconds = seconds,
                AttackerId = shooterId, VictimId = -1, WeaponSlot = weaponSlot,
                ShotX = x, ShotY = y, ShotZ = z, ShotVelocityX = velocityX, ShotVelocityY = velocityY, ShotVelocityZ = velocityZ });
        }
    }

    public void RecordMountRemoval(double now, int attackerId, int attackerParticipantId, int mountId, AgentState state)
    {
        lock (_gate)
        {
            if (_closed || IsTerminal() || !_knownMounts.Contains(mountId) || _removedMounts.Contains(mountId)) return;
            if (!TrySeconds(now, out double seconds)) return;
            _removedMounts.Add(mountId);
            if (!_participants.ContainsKey(attackerParticipantId) || state is not (AgentState.Killed or AgentState.Unconscious))
                _invalid = true;
            if (_deaths.ContainsKey(attackerParticipantId)) _invalid = true;
            if (!_episode.Truncated)
                AddEvent(new AdimiToolsAiLearningEvent { Kind = "mount_removal", Seconds = seconds,
                    AttackerId = attackerId, VictimId = mountId, RemovalState = (int)state });
        }
    }

    public void RecordMountTransition(double now, int riderId, bool mounted)
    {
        lock (_gate)
        {
            if (_closed || _episode.Truncated || IsTerminal() || !_participants.ContainsKey(riderId)
                || !TrySeconds(now, out double seconds)) return;
            AddEvent(new AdimiToolsAiLearningEvent
            {
                Kind = mounted ? "mount" : "dismount", Seconds = seconds,
                AttackerId = -1, VictimId = riderId,
            });
        }
    }

    public AdimiToolsAiLearningEpisode? Finish(double now, bool? playerWon, bool interrupted)
    {
        lock (_gate)
        {
            if (_closed) return null;
            _closed = true;
            double elapsed = double.IsFinite(now) && double.IsFinite(_startedAt) ? Math.Max(0d, now - _startedAt) : 0d;
            _episode.Outcome = playerWon.HasValue ? (playerWon.Value ? "player_win" : "ai_win") : "aborted";
            _episode.WinningSide = playerWon.HasValue ? (playerWon.Value ? 0 : 1) : -1;
            Participant[] losers = playerWon.HasValue
                ? _participants.Values.Where(p => p.IsPlayer != playerWon.Value).ToArray()
                : Array.Empty<Participant>();
            bool confirmed = losers.Length != 0 && losers.All(p => _deaths.ContainsKey(p.Id));
            bool winnerAlive = playerWon.HasValue && _participants.Values.Any(p => p.IsPlayer == playerWon.Value && !_deaths.ContainsKey(p.Id));
            // The last relevant removal is the actual end; polling can occur later.
            _episode.DurationSeconds = confirmed ? losers.Max(p => _deaths[p.Id].Seconds) : elapsed;
            if (_episode.DurationSeconds > MaxSeconds) Truncate();
            _episode.Valid = playerWon.HasValue && !interrupted && !_invalid && !_episode.Truncated
                && confirmed && winnerAlive && _episode.Frames.Count >= 2;
            if (_episode.Valid && playerWon == true && _episode.InitialPlayers == 1 && _episode.InitialBots == 1)
            {
                Participant player = _participants.Values.Single(p => p.IsPlayer);
                Participant bot = _participants.Values.Single(p => !p.IsPlayer);
                _episode.SoloAttributedWin = _deaths[bot.Id].AttackerParticipant == player.Id;
            }
            return _episode;
        }
    }

    private bool TrySeconds(double now, out double seconds)
    {
        seconds = now - _startedAt;
        if (!double.IsFinite(seconds) || seconds < 0d) { _invalid = true; seconds = 0d; return false; }
        if (seconds > MaxSeconds) { Truncate(); return false; }
        return true;
    }

    private bool IsTerminal()
        => (_episode.InitialPlayers > 0 && _participants.Values.Where(p => p.IsPlayer).All(p => _deaths.ContainsKey(p.Id)))
            || (_episode.InitialBots > 0 && _participants.Values.Where(p => !p.IsPlayer).All(p => _deaths.ContainsKey(p.Id)));

    private void AddEvent(AdimiToolsAiLearningEvent item)
    {
        if (_episode.Events.Count >= MaxEvents) { Truncate(); return; }
        _episode.Events.Add(item);
    }

    private void Truncate() { _episode.Truncated = true; _invalid = true; }
}
