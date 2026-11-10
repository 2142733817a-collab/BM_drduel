using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Newtonsoft.Json;

namespace AdimiDuel_Training
{
    // Pure value DTOs: the background writer never receives live Agent objects.
    internal sealed class TrainingParticipant
    {
        public int AgentId { get; set; }
        public int Side { get; set; }
        public string CharacterId { get; set; }
        public string WeaponId { get; set; }
        public string WeaponClass { get; set; }
        public string ControlSource { get; set; }
        public string ModelId { get; set; }
        public string ModelSha256 { get; set; }
        public bool IsPlayer { get { return ControlSource == "human"; } }
    }

    internal sealed class TrainingActorSnapshot
    {
        public int AgentId { get; set; }
        public int Side { get; set; }
        public bool IsPlayer { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float LookX { get; set; }
        public float LookY { get; set; }
        public float LookZ { get; set; }
        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float Health { get; set; }
        public float HealthLimit { get; set; }
        public float WeaponLength { get; set; }
        public float MoveInputX { get; set; }
        public float MoveInputY { get; set; }
        public float ActionProgress { get; set; }
        public int ActionType { get; set; }
        public int ActionStage { get; set; }
        public int ActionDirection { get; set; }
        public uint MovementFlags { get; set; }
        public uint EventFlags { get; set; }
        public string WeaponId { get; set; }
        public string WeaponClass { get; set; }
        public bool IsMounted { get; set; }

        // Sampled engine flags are observations, not proof of an executed input.
        // No model bridge currently provides an independently verified result.
        public string ExecutedAction { get { return null; } }
    }

    internal sealed class TrainingFrame
    {
        public double Seconds { get; set; }
        public List<TrainingActorSnapshot> Actors { get; set; }
    }

    internal sealed class TrainingEvent
    {
        public string Kind { get; set; }
        public double Seconds { get; set; }
        public int AttackerId { get; set; }
        public int VictimId { get; set; }
        public bool IsBlocked { get; set; }
        public float Damage { get; set; }
        public bool IsMissile { get; set; }
        public string AttackerWeaponClass { get; set; }
        public bool? IsAlternativeAttack { get; set; }
        public int AttackDirection { get; set; }
        public float AttackProgress { get; set; }
        public int StrikeType { get; set; }
        public bool ThrustTipHit { get; set; }
        public bool ConfirmedKilled { get; set; }
        public int RemovalState { get; set; }
        public string Evidence { get; set; }
        public string ControlSource { get; set; }
        public string PolicySessionId { get; set; }
        public string PolicyEpisodeId { get; set; }
        public long? ObservationSequence { get; set; }
        public long? CallbackTimestamp { get; set; }
        public long? CallbackFrequency { get; set; }
        public long? CandidateValidUntilTimestamp { get; set; }
        public long? AttackControlStartedTimestamp { get; set; }
        public bool? ContinuedAttack { get; set; }
        public string ModelId { get; set; }
        public string ModelSha256 { get; set; }
        public uint? NativeEventFlags { get; set; }
        public uint? NativeMovementFlags { get; set; }
        public float? NativeInputX { get; set; }
        public float? NativeInputY { get; set; }
        public uint? ComponentEventFlags { get; set; }
        public uint? ComponentMovementFlags { get; set; }
        public float? ComponentInputX { get; set; }
        public float? ComponentInputY { get; set; }
        public bool? ExperimentalMovement { get; set; }
        public string ExecutedAction { get { return null; } }
    }

    // An acknowledged change made by our component. This is not proof that
    // the engine or a later component finally executed the same input.
    internal sealed class TrainingComponentOutput
    {
        public int AgentId { get; set; }
        public string PolicySessionId { get; set; }
        public string PolicyEpisodeId { get; set; }
        public long ObservationSequence { get; set; }
        public long CallbackTimestamp { get; set; }
        public long CandidateValidUntilTimestamp { get; set; }
        public long AttackControlStartedTimestamp { get; set; }
        public bool ContinuedAttack { get; set; }
        public string ModelId { get; set; }
        public string ModelSha256 { get; set; }
        public uint NativeEventFlags { get; set; }
        public uint NativeMovementFlags { get; set; }
        public float NativeInputX { get; set; }
        public float NativeInputY { get; set; }
        public uint ComponentEventFlags { get; set; }
        public uint ComponentMovementFlags { get; set; }
        public float ComponentInputX { get; set; }
        public float ComponentInputY { get; set; }
        public bool ExperimentalMovement { get; set; }
    }

    internal sealed class TrainingEpisode
    {
        public int SchemaVersion { get; set; }
        public string MatchType { get; set; }
        public string Id { get; set; }
        public string SessionId { get; set; }
        public string StartedUtc { get; set; }
        public string SceneId { get; set; }
        public string SceneManifestSha256 { get; set; }
        public string GameVersion { get; set; }
        public string ModVersion { get; set; }
        public string ObservationMode { get; set; }
        public string InputCoordinateSystem { get; set; }
        public List<TrainingParticipant> Participants { get; set; }
        public List<TrainingFrame> Frames { get; set; }
        public List<TrainingEvent> Events { get; set; }
        public double DurationSeconds { get; set; }
        public int? WinningSide { get; set; }
        public string Outcome { get; set; }
        public string FinishReason { get; set; }
        public bool Terminated { get; set; }
        public bool Truncated { get; set; }
        public bool Valid { get; set; }
        public int DroppedFrames { get; set; }
        public int DroppedEvents { get; set; }
    }

    // One mission owns one recorder. Calls from the mission/callbacks never
    // write files, serialize JSON, compress or wait for a worker. Every round
    // is detached before entering the small bounded background queue.
    internal sealed class TrainingEpisodeRecorder : IDisposable
    {
        private const int DefaultCapacity = 8;
        private const int MaxFrames = 8192;
        private const int MaxActorSnapshots = 16384;
        private const int MaxEvents = 4096;
        private const int MaxRecordBytes = 16 * 1024 * 1024;
        private const double MaxSeconds = 600d;
        private readonly object _gate = new object();
        private readonly object _statusGate = new object();
        private readonly BlockingCollection<TrainingEpisode> _queue;
        private readonly string _sessionId;
        private readonly string _explicitRoot;
        private readonly string _moduleDirectory;
        private readonly Thread _writer;
        private TrainingEpisode _active;
        private Dictionary<int, TrainingParticipant> _participants;
        private readonly HashSet<int> _removed = new HashSet<int>();
        private readonly Dictionary<int, TrainingEvent> _removalEvents = new Dictionary<int, TrainingEvent>();
        private readonly Dictionary<int, long> _removalTimestamps = new Dictionary<int, long>();
        private double _startedAt;
        private double _latestFrameSeconds;
        private double _latestEventSeconds;
        private int _actorSnapshots;
        private bool _invalid;
        private bool _disposed;
        private int _pending;
        private long _written;
        private long _dropped;
        private long _errors;
        private string _lastError;
        private string _resolvedRoot;

        internal TrainingEpisodeRecorder() : this(null, DefaultModuleDirectory(), DefaultCapacity) { }

        internal TrainingEpisodeRecorder(string moduleDirectory) : this(null, moduleDirectory, DefaultCapacity) { }

        // Explicit root and capacity are useful for isolated tests. They are
        // not exposed to the game UI and do not change the player's files.
        internal TrainingEpisodeRecorder(string root, int capacity) : this(root, null, capacity) { }

        private TrainingEpisodeRecorder(string explicitRoot, string moduleDirectory, int capacity)
        {
            if (capacity < 1 || capacity > DefaultCapacity) throw new ArgumentOutOfRangeException("capacity");
            _explicitRoot = explicitRoot;
            _moduleDirectory = moduleDirectory;
            _sessionId = Guid.NewGuid().ToString("N");
            // Directory creation and configuration reads occur only inside WriterLoop.
            if (explicitRoot != null) _resolvedRoot = Path.GetFullPath(explicitRoot);
            _queue = new BlockingCollection<TrainingEpisode>(new ConcurrentQueue<TrainingEpisode>(), capacity);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Adimi self-play journal" };
            _writer.Start();
        }

        internal string SessionDirectory { get { return SessionDirectoryFor("selfplay"); } }
        internal string LocalPlayerSessionDirectory { get { return SessionDirectoryFor("local_player_ai"); } }
        internal string ActiveEpisodeId { get { lock (_gate) return _active == null ? null : _active.Id; } }
        internal string ResolvedRoot { get { lock (_statusGate) return _resolvedRoot; } }
        internal int Pending { get { lock (_statusGate) return _pending; } }
        internal long Written { get { lock (_statusGate) return _written; } }
        internal long Dropped { get { lock (_statusGate) return _dropped; } }
        internal long Errors { get { lock (_statusGate) return _errors; } }
        internal string LastError { get { lock (_statusGate) return _lastError; } }

        internal bool BeginRound(double now, string sceneId, string gameVersion, string modVersion,
            TrainingParticipant sideA, TrainingParticipant sideB)
        {
            return BeginRoundCore("selfplay", now, sceneId, gameVersion, modVersion, sideA, sideB);
        }

        internal bool BeginLocalPlayerRound(double now, string sceneId, string gameVersion, string modVersion,
            TrainingParticipant sideA, TrainingParticipant sideB)
        {
            return BeginRoundCore("local_player_ai", now, sceneId, gameVersion, modVersion, sideA, sideB);
        }

        private bool BeginRoundCore(string matchType, double now, string sceneId, string gameVersion, string modVersion,
            TrainingParticipant sideA, TrainingParticipant sideB)
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed || _active != null || !Finite(now) || now < 0d || !ValidText(sceneId, 128, false)
                        || !ValidText(gameVersion, 64, false) || !ValidText(modVersion, 64, false)
                        || !ValidParticipant(sideA, 0) || !ValidParticipant(sideB, 1) || sideA.AgentId == sideB.AgentId
                        || (matchType == "selfplay" && (sideA.IsPlayer || sideB.IsPlayer))
                        || (matchType == "local_player_ai" && sideA.IsPlayer == sideB.IsPlayer)) return false;
                    TrainingParticipant first = CopyParticipant(sideA), second = CopyParticipant(sideB);
                    _participants = new Dictionary<int, TrainingParticipant> { { first.AgentId, first }, { second.AgentId, second } };
                    _removed.Clear();
                    _removalEvents.Clear();
                    _removalTimestamps.Clear();
                    _startedAt = now;
                    _latestFrameSeconds = -1d;
                    _latestEventSeconds = -1d;
                    _actorSnapshots = 0;
                    _invalid = false;
                    _active = new TrainingEpisode
                    {
                        SchemaVersion = 1, MatchType = matchType, Id = Guid.NewGuid().ToString("N"), SessionId = _sessionId,
                        StartedUtc = DateTime.UtcNow.ToString("O"), SceneId = sceneId, SceneManifestSha256 = null,
                        GameVersion = gameVersion, ModVersion = modVersion, ObservationMode = "sampled_state_only",
                        InputCoordinateSystem = "unknown", Participants = new List<TrainingParticipant> { first, second },
                        Frames = new List<TrainingFrame>(), Events = new List<TrainingEvent>()
                    };
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        internal bool RecordFrame(double now, TrainingActorSnapshot[] actors)
        {
            try
            {
                lock (_gate)
                {
                    double seconds;
                    if (_disposed || _active == null || !TrySeconds(now, out seconds) || _active.Truncated) return false;
                    if (actors == null || actors.Length == 0 || actors.Length > _participants.Count || seconds < _latestFrameSeconds)
                    { _invalid = true; _active.DroppedFrames++; return false; }
                    if (_active.Frames.Count >= MaxFrames || actors.Length > MaxActorSnapshots - _actorSnapshots)
                    { MarkTruncated(); _active.DroppedFrames++; return false; }
                    var copies = new List<TrainingActorSnapshot>(actors.Length);
                    var seen = new HashSet<int>();
                    foreach (TrainingActorSnapshot actor in actors)
                    {
                        TrainingParticipant participant;
                        if (actor == null || !_participants.TryGetValue(actor.AgentId, out participant) || !seen.Add(actor.AgentId)
                            || !ValidActor(actor))
                        { _invalid = true; _active.DroppedFrames++; return false; }
                        copies.Add(CopyActor(actor, participant));
                    }
                    if (_active.Frames.Count == 0 && copies.Count != _participants.Count)
                    { _invalid = true; _active.DroppedFrames++; return false; }
                    _active.Frames.Add(new TrainingFrame { Seconds = seconds, Actors = copies });
                    _latestFrameSeconds = seconds;
                    _actorSnapshots += copies.Count;
                    return true;
                }
            }
            catch (Exception) { Invalidate(); return false; }
        }

        internal void RecordHit(double now, int attackerId, int victimId, bool blocked, float damage, bool isMissile,
            string weaponClass, bool? isAlternativeAttack, int attackDirection, float attackProgress, int strikeType, bool thrustTipHit)
        {
            try
            {
                lock (_gate)
                {
                    double seconds;
                    if (_disposed || _active == null || !TrySeconds(now, out seconds) || _active.Truncated) return;
                    if (!Finite(damage) || damage < 0f || !Finite(attackProgress) || !ValidText(weaponClass, 64, true))
                    { _invalid = true; _active.DroppedEvents++; return; }
                    TrainingParticipant attacker, victim;
                    if (!_participants.TryGetValue(attackerId, out attacker) || !_participants.TryGetValue(victimId, out victim)
                        || attackerId == victimId || _removed.Contains(attackerId) || _removed.Contains(victimId)) _invalid = true;
                    AddEvent(new TrainingEvent { Kind = "hit", Seconds = seconds, AttackerId = attackerId, VictimId = victimId,
                        IsBlocked = blocked, Damage = damage, IsMissile = isMissile, AttackerWeaponClass = weaponClass ?? string.Empty,
                        IsAlternativeAttack = isAlternativeAttack, AttackDirection = attackDirection, AttackProgress = attackProgress,
                        StrikeType = strikeType, ThrustTipHit = thrustTipHit });
                }
            }
            catch (Exception) { Invalidate(); }
        }

        // Called on the mission thread after draining acknowledged component
        // outputs. Invalid or missing provenance must not become training data.
        internal bool RecordComponentOutput(double now, TrainingComponentOutput trace)
        {
            try
            {
                long receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    double seconds;
                    if (_disposed || _active == null || !TrySeconds(now, out seconds) || _active.Truncated) return false;
                    TrainingParticipant participant;
                    long removedAt;
                    if (!ValidTrace(trace) || !_participants.TryGetValue(trace.AgentId, out participant)
                        || participant.IsPlayer || trace.PolicyEpisodeId != _active.Id || trace.CallbackTimestamp > receivedAt
                        || (_removed.Contains(trace.AgentId) && (!_removalTimestamps.TryGetValue(trace.AgentId, out removedAt)
                            || trace.CallbackTimestamp >= removedAt)))
                    { _invalid = true; _active.DroppedEvents++; return false; }
                    if (participant.ControlSource == "native") participant.ControlSource = "mixed";
                    int beforeCount = _active.Events.Count;
                    AddEvent(new TrainingEvent { Kind = "component_output", Seconds = seconds, AttackerId = trace.AgentId, VictimId = -1,
                        Evidence = "component_output", ControlSource = "model", PolicySessionId = trace.PolicySessionId,
                        PolicyEpisodeId = trace.PolicyEpisodeId, ObservationSequence = trace.ObservationSequence,
                        CallbackTimestamp = trace.CallbackTimestamp, CallbackFrequency = System.Diagnostics.Stopwatch.Frequency,
                        CandidateValidUntilTimestamp = trace.CandidateValidUntilTimestamp,
                        AttackControlStartedTimestamp = trace.AttackControlStartedTimestamp == 0 ? (long?)null : trace.AttackControlStartedTimestamp,
                        ContinuedAttack = trace.ContinuedAttack,
                        ModelId = trace.ModelId, ModelSha256 = trace.ModelSha256,
                        NativeEventFlags = trace.NativeEventFlags, NativeMovementFlags = trace.NativeMovementFlags,
                        NativeInputX = trace.NativeInputX, NativeInputY = trace.NativeInputY,
                        ComponentEventFlags = trace.ComponentEventFlags, ComponentMovementFlags = trace.ComponentMovementFlags,
                        ComponentInputX = trace.ComponentInputX, ComponentInputY = trace.ComponentInputY,
                        ExperimentalMovement = trace.ExperimentalMovement });
                    return _active.Events.Count == beforeCount + 1;
                }
            }
            catch (Exception) { Invalidate(); return false; }
        }

        internal void MarkPolicyTraceIncomplete()
        {
            lock (_gate)
            {
                if (_active == null) return;
                _invalid = true;
                _active.DroppedEvents++;
            }
        }

        internal void RecordRemoval(double now, int attackerId, int victimId, bool confirmedKilled, int removalState)
        {
            RecordRemoval(now, attackerId, victimId, confirmedKilled, removalState, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        // Capture callbackTimestamp as early as possible in OnAgentRemoved.
        // This permits a previously generated component trace to be drained
        // later without admitting traces made at or after the removal callback.
        internal void RecordRemoval(double now, int attackerId, int victimId, bool confirmedKilled, int removalState, long callbackTimestamp)
        {
            try
            {
                long receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    double seconds;
                    if (_disposed || _active == null || !TrySeconds(now, out seconds)) return;
                    if (callbackTimestamp <= 0 || callbackTimestamp > receivedAt) { _invalid = true; return; }
                    TrainingParticipant attacker, victim;
                    TrainingEvent prior;
                    if (_removalEvents.TryGetValue(victimId, out prior))
                    {
                        if (prior.AttackerId != attackerId || prior.ConfirmedKilled != confirmedKilled || prior.RemovalState != removalState)
                            _invalid = true;
                        long priorTimestamp;
                        if (!_removalTimestamps.TryGetValue(victimId, out priorTimestamp) || callbackTimestamp < priorTimestamp)
                            _invalid = true; // unexpected callback reordering: do not silently trust the episode
                        return;
                    }
                    if (!_participants.TryGetValue(victimId, out victim) || !confirmedKilled)
                    { _invalid = true; return; }
                    if (!_participants.TryGetValue(attackerId, out attacker) || attackerId == victimId || attacker.Side == victim.Side
                        || _removed.Contains(attackerId)) _invalid = true;
                    _removed.Add(victimId);
                    var removal = new TrainingEvent { Kind = "removal", Seconds = seconds, AttackerId = attackerId, VictimId = victimId,
                        ConfirmedKilled = confirmedKilled, RemovalState = removalState, CallbackTimestamp = callbackTimestamp,
                        CallbackFrequency = System.Diagnostics.Stopwatch.Frequency };
                    _removalEvents.Add(victimId, removal);
                    _removalTimestamps.Add(victimId, callbackTimestamp);
                    if (!_active.Truncated)
                        AddEvent(removal);
                }
            }
            catch (Exception) { Invalidate(); }
        }

        internal void EndRound(double now, int? winningSide, string reason, bool interrupted)
        {
            TrainingEpisode completed = null;
            try
            {
                lock (_gate)
                {
                    if (_disposed || _active == null) return;
                    double seconds;
                    if (!TrySeconds(now, out seconds))
                        seconds = Finite(now) && now >= _startedAt ? Math.Min(MaxSeconds, now - _startedAt) : Math.Max(0d, _latestFrameSeconds);
                    bool hasWinner = winningSide == 0 || winningSide == 1;
                    bool winnerAlive = false, loserDead = true;
                    foreach (TrainingParticipant p in _participants.Values)
                    {
                        if (hasWinner && p.Side == winningSide.Value && !_removed.Contains(p.AgentId)) winnerAlive = true;
                        if (hasWinner && p.Side != winningSide.Value && !_removed.Contains(p.AgentId)) loserDead = false;
                    }
                    if (interrupted || !hasWinner) _active.Truncated = true;
                    if (!ValidText(reason, 64, false)) { _invalid = true; reason = "unknown"; }
                    _active.DurationSeconds = seconds;
                    _active.WinningSide = hasWinner ? winningSide : null;
                    _active.Outcome = hasWinner ? (winningSide.Value == 0 ? "side_a_win" : "side_b_win") : "aborted";
                    _active.FinishReason = reason;
                    _active.Terminated = hasWinner && !interrupted && loserDead && winnerAlive;
                    _active.Valid = _active.Terminated && !_active.Truncated && !_invalid && _active.Frames.Count >= 2;
                    completed = _active;
                    _active = null;
                    _participants = null;
                    _removed.Clear();
                    _removalEvents.Clear();
                    _removalTimestamps.Clear();
                }
                lock (_statusGate) _pending++;
                bool accepted = false;
                try { accepted = _queue.TryAdd(completed); }
                catch (InvalidOperationException) { }
                if (!accepted)
                {
                    lock (_statusGate) { _pending--; _dropped++; _lastError = "self-play writer queue full or stopped"; }
                }
            }
            catch (Exception)
            {
                lock (_statusGate) { _dropped++; _errors++; _lastError = "self-play round could not be queued"; }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_active != null)
                {
                    _active = null;
                    _participants = null;
                    lock (_statusGate) { _dropped++; _lastError = "unfinished self-play round discarded"; }
                }
                _queue.CompleteAdding();
            }
            // Do not Join here: disposal may occur on the game thread.
        }

        private void WriterLoop()
        {
            bool rootReady = ResolveAndPublishRoot();
            foreach (TrainingEpisode episode in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (!rootReady)
                    {
                        rootReady = ResolveAndPublishRoot();
                        if (!rootReady)
                        {
                            lock (_statusGate) { _pending--; _dropped++; }
                            continue;
                        }
                    }
                    byte[] row = Encode(episode);
                    string directory = SessionDirectoryFor(episode.MatchType);
                    if (directory == null) throw new InvalidOperationException("training data directory is not ready");
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "episodes.jsonl");
                    using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        long start = file.Seek(0, SeekOrigin.End);
                        try
                        {
                            file.Write(row, 0, row.Length);
                            file.WriteByte((byte)'\n');
                            file.Flush(true);
                        }
                        catch
                        {
                            try { file.SetLength(start); file.Flush(true); } catch (Exception) { }
                            throw;
                        }
                    }
                    lock (_statusGate) { _pending--; _written++; }
                }
                catch (Exception exception)
                {
                    lock (_statusGate) { _pending--; _dropped++; _errors++; _lastError = "self-play journal: " + exception.GetType().Name; }
                }
            }
        }

        private bool ResolveAndPublishRoot()
        {
            try
            {
                string resolved = ResolveDataRoot();
                lock (_statusGate) { _resolvedRoot = resolved; }
                return true;
            }
            catch (Exception exception)
            {
                lock (_statusGate)
                {
                    _errors++;
                    _lastError = "training data configuration: " + exception.GetType().Name + ": " + exception.Message;
                }
                return false;
            }
        }

        private string ResolveDataRoot()
        {
            if (_explicitRoot != null) return Path.GetFullPath(_explicitRoot);
            string fromEnvironment = Environment.GetEnvironmentVariable("ADIMI_LOCAL_TRAINING_PATH");
            if (fromEnvironment != null)
            {
                if (string.IsNullOrWhiteSpace(fromEnvironment)) throw new InvalidDataException("environment data directory is empty");
                return Path.GetFullPath(fromEnvironment);
            }
            if (!string.IsNullOrWhiteSpace(_moduleDirectory))
            {
                string configPath = Path.Combine(_moduleDirectory, "TrainingSettings.xml");
                try
                {
                    using (var file = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (file.Length > 64 * 1024) throw new InvalidDataException("training settings exceed size limit");
                        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                            MaxCharactersInDocument = 64 * 1024 };
                        var doc = new XmlDocument { XmlResolver = null };
                        using (XmlReader reader = XmlReader.Create(file, settings)) doc.Load(reader);
                        if (doc.DocumentElement == null || doc.DocumentElement.Name != "TrainingSettings")
                            throw new InvalidDataException("training settings root must be TrainingSettings");
                        XmlNodeList nodes = doc.DocumentElement.SelectNodes("DataDirectory");
                        if (nodes == null || nodes.Count != 1) throw new InvalidDataException("training settings require exactly one DataDirectory");
                        var element = nodes[0] as XmlElement;
                        if (element == null || !element.HasAttribute("value")) throw new InvalidDataException("DataDirectory requires a value attribute");
                        string value = element.GetAttribute("value");
                        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("DataDirectory cannot be empty");
                        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(_moduleDirectory, value));
                    }
                }
                catch (FileNotFoundException) { /* No config is an allowed fallback. */ }
                catch (DirectoryNotFoundException) { /* No module config is an allowed fallback. */ }
            }
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) throw new InvalidOperationException("LOCALAPPDATA is unavailable");
            return Path.GetFullPath(Path.Combine(localAppData, "AdimiDuelTraining"));
        }

        private string SessionDirectoryFor(string matchType)
        {
            lock (_statusGate)
                return _resolvedRoot == null ? null : Path.Combine(_resolvedRoot, matchType, _sessionId);
        }

        private static string DefaultModuleDirectory()
        {
            string location = typeof(TrainingEpisodeRecorder).Assembly.Location;
            if (string.IsNullOrWhiteSpace(location)) return null;
            string bin = Path.GetDirectoryName(location);
            return string.IsNullOrWhiteSpace(bin) ? null : Path.GetFullPath(Path.Combine(bin, "..", ".."));
        }

        private static byte[] Encode(TrainingEpisode episode)
        {
            byte[] raw = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(episode, Newtonsoft.Json.Formatting.None));
            if (raw.Length == 0 || raw.Length > MaxRecordBytes) throw new InvalidDataException("self-play episode exceeds limit");
            byte[] gzipBytes;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(raw, 0, raw.Length);
                gzipBytes = output.ToArray();
            }
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant();
            var envelope = new Dictionary<string, object> {
                { "Format", "adimi-gzip-json-v1" }, { "RawBytes", raw.Length }, { "Sha256", hash },
                { "Payload", Convert.ToBase64String(gzipBytes) }
            };
            byte[] encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope, Newtonsoft.Json.Formatting.None));
            return encoded.Length >= raw.Length || encoded.Length > MaxRecordBytes ? raw : encoded;
        }

        private bool TrySeconds(double now, out double seconds)
        {
            seconds = now - _startedAt;
            if (!Finite(seconds) || seconds < 0d) { _invalid = true; seconds = 0d; return false; }
            if (seconds > MaxSeconds) { MarkTruncated(); return false; }
            return true;
        }

        private void AddEvent(TrainingEvent item)
        {
            if (_active.Events.Count >= MaxEvents) { MarkTruncated(); _active.DroppedEvents++; return; }
            if (item.Seconds < _latestEventSeconds) _invalid = true;
            _latestEventSeconds = Math.Max(_latestEventSeconds, item.Seconds);
            _active.Events.Add(item);
        }

        private void MarkTruncated() { _active.Truncated = true; _invalid = true; }

        private void Invalidate() { lock (_gate) if (_active != null) _invalid = true; }

        private static bool ValidParticipant(TrainingParticipant p, int side)
        {
            if (p == null || p.AgentId < 0 || p.Side != side || !ValidText(p.CharacterId, 128, true)
                || !ValidText(p.WeaponId, 128, true) || !ValidText(p.WeaponClass, 64, true)) return false;
            if (p.ControlSource == "native" || p.ControlSource == "human") return p.ModelId == null && p.ModelSha256 == null;
            if (p.ControlSource != "model" || !ValidText(p.ModelId, 128, false) || p.ModelSha256 == null || p.ModelSha256.Length != 64) return false;
            foreach (char c in p.ModelSha256) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool ValidTrace(TrainingComponentOutput t)
        {
            const uint attackMask = 0x3c0u, defendMaskWithBlock = 0xfc00u, specialEventFlags = 0x840fu, actionFlag = 0x10000u;
            Guid id;
            if (t == null || t.AgentId < 0 || t.ObservationSequence < 0 || t.CallbackTimestamp <= 0 || t.CandidateValidUntilTimestamp <= 0
                || !Guid.TryParseExact(t.PolicySessionId, "N", out id) || id.ToString("N") != t.PolicySessionId
                || !Guid.TryParseExact(t.PolicyEpisodeId, "N", out id) || id.ToString("N") != t.PolicyEpisodeId
                || !ValidText(t.ModelId, 128, false) || t.ModelSha256 == null || t.ModelSha256.Length != 64
                || !Finite(t.NativeInputX) || !Finite(t.NativeInputY) || !Finite(t.ComponentInputX) || !Finite(t.ComponentInputY)
                || t.NativeEventFlags != t.ComponentEventFlags || (t.NativeEventFlags & specialEventFlags) != 0
                || (t.NativeMovementFlags & (defendMaskWithBlock | actionFlag)) != 0
                || (t.NativeMovementFlags & ~attackMask) != (t.ComponentMovementFlags & ~attackMask)) return false;
            foreach (char c in t.ModelSha256) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            uint oldAttack = t.NativeMovementFlags & attackMask, newAttack = t.ComponentMovementFlags & attackMask;
            if (oldAttack != newAttack && (oldAttack == 0 || newAttack == 0 || (oldAttack & (oldAttack - 1u)) != 0
                || (newAttack & (newAttack - 1u)) != 0)) return false;
            if (t.ContinuedAttack)
            {
                long limit = System.Diagnostics.Stopwatch.Frequency + System.Diagnostics.Stopwatch.Frequency / 2L;
                if (oldAttack == newAttack || t.AttackControlStartedTimestamp <= 0
                    || t.AttackControlStartedTimestamp > t.CandidateValidUntilTimestamp
                    || t.CallbackTimestamp < t.AttackControlStartedTimestamp
                    || t.CallbackTimestamp - t.AttackControlStartedTimestamp > limit) return false;
            }
            else if (t.CallbackTimestamp > t.CandidateValidUntilTimestamp
                || (oldAttack != newAttack && t.AttackControlStartedTimestamp != t.CallbackTimestamp)) return false;
            bool changedMovement = t.NativeInputX != t.ComponentInputX || t.NativeInputY != t.ComponentInputY;
            if (changedMovement && (!t.ExperimentalMovement || Math.Abs(t.ComponentInputX) > 1f || Math.Abs(t.ComponentInputY) > 1f)) return false;
            return changedMovement || oldAttack != newAttack;
        }

        private static bool ValidActor(TrainingActorSnapshot a)
        {
            return Finite(a.X) && Finite(a.Y) && Finite(a.Z) && Finite(a.LookX) && Finite(a.LookY) && Finite(a.LookZ)
                && Finite(a.VelocityX) && Finite(a.VelocityY) && Finite(a.Health) && Finite(a.HealthLimit)
                && a.Health >= 0f && a.HealthLimit > 0f && a.Health <= a.HealthLimit
                && Finite(a.WeaponLength) && a.WeaponLength >= 0f && Finite(a.MoveInputX) && Finite(a.MoveInputY)
                && Finite(a.ActionProgress) && ValidText(a.WeaponId, 128, true) && ValidText(a.WeaponClass, 64, true);
        }

        private static TrainingParticipant CopyParticipant(TrainingParticipant p)
        {
            return new TrainingParticipant { AgentId = p.AgentId, Side = p.Side, CharacterId = p.CharacterId ?? string.Empty,
                WeaponId = p.WeaponId ?? string.Empty, WeaponClass = p.WeaponClass ?? string.Empty, ControlSource = p.ControlSource,
                ModelId = p.ModelId, ModelSha256 = p.ModelSha256 };
        }

        private static TrainingActorSnapshot CopyActor(TrainingActorSnapshot a, TrainingParticipant participant)
        {
            return new TrainingActorSnapshot { AgentId = a.AgentId, Side = participant.Side, IsPlayer = participant.IsPlayer, X = a.X, Y = a.Y, Z = a.Z,
                LookX = a.LookX, LookY = a.LookY, LookZ = a.LookZ, VelocityX = a.VelocityX, VelocityY = a.VelocityY,
                Health = a.Health, HealthLimit = a.HealthLimit, WeaponLength = a.WeaponLength,
                MoveInputX = a.MoveInputX, MoveInputY = a.MoveInputY, ActionProgress = a.ActionProgress,
                ActionType = a.ActionType, ActionStage = a.ActionStage, ActionDirection = a.ActionDirection,
                MovementFlags = a.MovementFlags, EventFlags = a.EventFlags,
                WeaponId = a.WeaponId ?? string.Empty, WeaponClass = a.WeaponClass ?? string.Empty, IsMounted = a.IsMounted };
        }

        private static bool ValidText(string s, int max, bool allowEmpty) { return s == null ? allowEmpty : s.Length <= max && (allowEmpty || s.Length != 0); }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
