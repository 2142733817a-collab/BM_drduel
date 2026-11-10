using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdimiDuel_Training
{
    // These are detached snapshots. Fields remain untransformed game values;
    // in particular input-coordinate interpretation is not yet established.
    internal sealed class TrainingPolicyActor
    {
        [JsonProperty("agent_id")] public int AgentId { get; set; }
        [JsonProperty("side")] public int Side { get; set; }
        [JsonProperty("is_player")] public bool IsPlayer { get; set; }
        // Null means the producer failed to provide the observation; it must
        // not be silently interpreted as an unmounted actor.
        [JsonProperty("is_mounted")] public bool? IsMounted { get; set; }
        [JsonProperty("x")] public float X { get; set; }
        [JsonProperty("y")] public float Y { get; set; }
        [JsonProperty("z")] public float Z { get; set; }
        [JsonProperty("look_x")] public float LookX { get; set; }
        [JsonProperty("look_y")] public float LookY { get; set; }
        [JsonProperty("velocity_x")] public float VelocityX { get; set; }
        [JsonProperty("velocity_y")] public float VelocityY { get; set; }
        [JsonProperty("health")] public float Health { get; set; }
        [JsonProperty("health_limit")] public float HealthLimit { get; set; }
        [JsonProperty("action_type")] public int ActionType { get; set; }
        [JsonProperty("action_stage")] public int ActionStage { get; set; }
        [JsonProperty("action_direction")] public int ActionDirection { get; set; }
        [JsonProperty("action_progress")] public float ActionProgress { get; set; }
        [JsonProperty("weapon_class")] public string WeaponClass { get; set; }
    }

    internal sealed class TrainingPolicyDecision
    {
        internal TrainingPolicyDecision(long validUntilTimestamp) { ValidUntilTimestamp = validUntilTimestamp; }
        public int AgentId { get; set; }
        public string SessionId { get; set; }
        public string EpisodeId { get; set; }
        public long Sequence { get; set; }
        public string ModelId { get; set; }
        public string ModelSha256 { get; set; }
        public string Attack { get; set; }
        public float? MoveX { get; set; }
        public float? MoveY { get; set; }
        // Absolute Stopwatch timestamp, derived from the observation's first
        // publication time; polling the same file cannot extend this deadline.
        public long ValidUntilTimestamp { get; private set; }
    }

    // Optional mailbox only: it never changes a game Agent, movement input,
    // kick/defence flags or engine state. No trainer is bundled or assumed.
    internal sealed class TrainingPolicyBridge : IDisposable
    {
        internal const string Protocol = "adimi-local-policy-v1";
        private const int MaxActors = 32;
        private const int MaxJsonBytes = 64 * 1024;
        private const int MaxIssued = 8;
        private const int MaxTerminals = 8;
        private const int MaxEndedEpisodes = 100000;
        private const long MaxSequenceLag = 6;
        private readonly object _gate = new object();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly string _directory;
        private readonly string _sessionId;
        private readonly bool _enabled;
        private readonly int _maxAgeMs;
        private readonly Dictionary<long, PublishedObservation> _issued = new Dictionary<long, PublishedObservation>();
        private readonly Dictionary<int, DecisionEntry> _decisions = new Dictionary<int, DecisionEntry>();
        private readonly Queue<PublishedObservation> _terminals = new Queue<PublishedObservation>();
        private readonly HashSet<string> _endedEpisodes = new HashSet<string>(StringComparer.Ordinal);
        private PublishedObservation _pending;
        private PublishedObservation _current;
        private bool _disposed;
        private long _invalidReplies;
        private long _errors;
        private string _lastError;
        private long _epoch;

        private sealed class WireObservation
        {
            [JsonProperty("protocol")] public string ProtocolName { get; set; }
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("session_id")] public string SessionId { get; set; }
            [JsonProperty("episode_id")] public string EpisodeId { get; set; }
            [JsonProperty("sequence")] public long Sequence { get; set; }
            [JsonProperty("game_seconds")] public double GameSeconds { get; set; }
            [JsonProperty("input_coordinate_system")] public string InputCoordinateSystem { get; set; }
            [JsonProperty("actors")] public TrainingPolicyActor[] Actors { get; set; }
            [JsonProperty("winning_side")] public int? WinningSide { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
            [JsonProperty("terminated")] public bool Terminated { get; set; }
            [JsonProperty("truncated")] public bool Truncated { get; set; }
        }

        private sealed class PublishedObservation
        {
            public WireObservation Observation;
            public long Timestamp;
            public long Epoch;
        }

        private sealed class DecisionEntry
        {
            public TrainingPolicyDecision Decision;
            public long Timestamp;
        }

        internal TrainingPolicyBridge(string localRoot, bool enabled, int maxAgeMs = 250)
        {
            if (maxAgeMs < 20 || maxAgeMs > 2000) throw new ArgumentOutOfRangeException("maxAgeMs");
            _sessionId = Guid.NewGuid().ToString("N");
            _enabled = enabled;
            _maxAgeMs = maxAgeMs;
            if (!enabled) return;
            try
            {
                string root = string.IsNullOrWhiteSpace(localRoot)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdimiDuelTraining", "PolicyBridge")
                    : localRoot;
                _directory = Path.Combine(Path.GetFullPath(root), _sessionId);
                var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Adimi local policy mailbox" };
                worker.Start();
            }
            catch (Exception exception)
            {
                _errors++;
                _lastError = "local policy mailbox: " + exception.GetType().Name;
            }
        }

        internal string SessionId { get { return _sessionId; } }
        internal string SessionDirectory { get { return _directory; } }
        internal long InvalidReplies { get { lock (_gate) return _invalidReplies; } }
        internal long Errors { get { lock (_gate) return _errors; } }
        internal string LastError { get { lock (_gate) return _lastError; } }

        // Each call publishes only the latest value. All validation and copying
        // are bounded. There is no file access or trainer wait on this path.
        internal bool Publish(string episodeId, long sequence, double gameSeconds, TrainingPolicyActor[] actors)
        {
            if (!_enabled) return false;
            if (!ValidGuid(episodeId) || sequence < 0 || !Finite(gameSeconds) || gameSeconds < 0d
                || actors == null || actors.Length == 0 || actors.Length > MaxActors) return RejectObservation();
            var copied = new TrainingPolicyActor[actors.Length];
            var ids = new HashSet<int>();
            for (int i = 0; i < actors.Length; i++)
            {
                TrainingPolicyActor actor = actors[i];
                if (!ValidActor(actor) || !ids.Add(actor.AgentId)) return RejectObservation();
                copied[i] = CopyActor(actor);
            }
            lock (_gate)
            {
                if (_disposed) return false;
                if (_endedEpisodes.Contains(episodeId)) return false;
                if (_current != null && _current.Observation.EpisodeId == episodeId && _current.Observation.Kind == "terminal") return false;
                if (_current != null && _current.Observation.EpisodeId == episodeId
                    && (sequence <= _current.Observation.Sequence || gameSeconds < _current.Observation.GameSeconds)) return RejectObservationLocked();
                if (_current == null || _current.Observation.EpisodeId != episodeId || copied.Any(a => a.IsMounted != false))
                {
                    _epoch++;
                    _issued.Clear();
                    _decisions.Clear();
                }
                var wire = new WireObservation { ProtocolName = Protocol, Kind = "observation", SessionId = _sessionId, EpisodeId = episodeId,
                    Sequence = sequence, GameSeconds = gameSeconds, InputCoordinateSystem = "unknown", Actors = copied };
                var item = new PublishedObservation { Observation = wire, Timestamp = Stopwatch.GetTimestamp(), Epoch = _epoch };
                _current = item;
                _pending = item;
                return true;
            }
        }

        // Called on a mount event even before another observation is sampled.
        // A later unmounted observation can publish a new epoch, but no reply
        // to a previously issued observation may survive this invalidation.
        internal void InvalidateCandidates()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _epoch++;
                _pending = null;
                _issued.Clear();
                _decisions.Clear();
            }
        }

        // Terminals are retained separately from the coalesced latest snapshot,
        // so an immediately started next round cannot overwrite its ending.
        internal bool PublishTerminal(string episodeId, long sequence, double gameSeconds, int? winningSide,
            string reason, bool terminated, bool truncated)
        {
            if (!_enabled) return false;
            if (!ValidGuid(episodeId) || sequence < 0 || !Finite(gameSeconds) || gameSeconds < 0d
                || string.IsNullOrWhiteSpace(reason) || reason.Length > 64 || terminated == truncated
                || (winningSide.HasValue && (winningSide.Value != 0 && winningSide.Value != 1))
                || (winningSide.HasValue && !terminated)) return RejectObservation();
            lock (_gate)
            {
                if (_disposed || (_current != null && _current.Observation.Kind == "terminal" && _current.Observation.EpisodeId == episodeId)) return false;
                if (_current == null || _current.Observation.EpisodeId != episodeId
                    || sequence <= _current.Observation.Sequence || gameSeconds < _current.Observation.GameSeconds) return RejectObservationLocked();
                if (_endedEpisodes.Count >= MaxEndedEpisodes) { _errors++; _lastError = "local policy episode identity limit"; return RejectObservationLocked(); }
                _epoch++;
                _issued.Clear();
                _decisions.Clear();
                _pending = null;
                var wire = new WireObservation { ProtocolName = Protocol, Kind = "terminal", SessionId = _sessionId,
                    EpisodeId = episodeId, Sequence = sequence, GameSeconds = gameSeconds, InputCoordinateSystem = "unknown",
                    Actors = new TrainingPolicyActor[0], WinningSide = winningSide, Reason = reason,
                    Terminated = terminated, Truncated = truncated };
                var terminal = new PublishedObservation { Observation = wire, Timestamp = Stopwatch.GetTimestamp(), Epoch = _epoch };
                _current = terminal;
                _endedEpisodes.Add(episodeId);
                if (_terminals.Count >= MaxTerminals)
                {
                    _errors++;
                    _lastError = "local policy terminal queue full";
                    return false;
                }
                _terminals.Enqueue(terminal);
                return true;
            }
        }

        // Returns a validated proposal only. False means the caller must keep
        // all original engine input. The returned values are not executed here.
        internal bool TryGetDecision(string episodeId, int agentId, out TrainingPolicyDecision decision)
        {
            decision = null;
            lock (_gate)
            {
                DecisionEntry entry;
                if (!_enabled || _disposed || _current == null || _current.Observation.Kind != "observation"
                    || _current.Epoch != _epoch || _current.Observation.EpisodeId != episodeId
                    || !_decisions.TryGetValue(agentId, out entry) || !Fresh(entry.Timestamp)
                    || entry.Decision.Sequence > _current.Observation.Sequence
                    || _current.Observation.Sequence - entry.Decision.Sequence > MaxSequenceLag
                    || _current.Observation.Actors.Any(a => a.IsMounted != false)
                    || !_current.Observation.Actors.Any(a => a.AgentId == agentId && !a.IsPlayer && a.IsMounted == false && a.Health > 0f)) return false;
                decision = CopyDecision(entry.Decision, entry.Decision.ValidUntilTimestamp);
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _pending = null; _decisions.Clear(); _issued.Clear(); }
            _stop.Set();
            // Deliberately do not Join: this may run on a game thread.
        }

        private void WorkerLoop()
        {
            try
            {
                if (new Uri(_directory).IsUnc) throw new InvalidDataException("policy mailbox requires a local path");
                Directory.CreateDirectory(_directory);
                Directory.CreateDirectory(Path.Combine(_directory, "actions"));
                Directory.CreateDirectory(Path.Combine(_directory, "terminals"));
                while (true)
                {
                    bool stopping = _stop.WaitOne(20);
                    PublishedObservation terminal;
                    lock (_gate) terminal = _terminals.Count == 0 ? null : _terminals.Dequeue();
                    if (terminal != null)
                    {
                        try
                        {
                            byte[] bytes = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(terminal.Observation, Formatting.None));
                            if (bytes.Length > MaxJsonBytes) throw new InvalidDataException("terminal exceeds size limit");
                            WriteAtomic(Path.Combine(_directory, "terminals", terminal.Observation.EpisodeId + ".json"), bytes);
                        }
                        catch (Exception exception)
                        {
                            lock (_gate) { _errors++; _lastError = "local policy terminal: " + exception.GetType().Name; }
                        }
                        continue;
                    }
                    if (stopping) break;
                    PublishedObservation next;
                    lock (_gate) { next = _pending; _pending = null; }
                    if (next != null)
                    {
                        try
                        {
                            byte[] json = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(next.Observation, Formatting.None));
                            if (json.Length > MaxJsonBytes) throw new InvalidDataException("observation exceeds size limit");
                            WriteAtomic(Path.Combine(_directory, "observation.json"), json);
                        }
                        catch (Exception exception)
                        {
                            // For example an external reader may temporarily
                            // deny replace on Windows. Fail closed for this
                            // observation and continue serving later ones.
                            lock (_gate)
                            {
                                _errors++;
                                _lastError = "local policy observation: " + exception.GetType().Name;
                                _issued.Clear();
                                _decisions.Clear();
                            }
                            continue;
                        }
                        lock (_gate)
                        {
                            if (_current != null && next.Epoch == _epoch && _current.Observation.EpisodeId == next.Observation.EpisodeId)
                            {
                                _issued[next.Observation.Sequence] = next;
                                while (_issued.Count > MaxIssued) _issued.Remove(_issued.Keys.Min());
                            }
                        }
                    }
                    PublishedObservation current;
                    lock (_gate) current = _current;
                    if (current != null && current.Observation.Kind == "observation"
                        && current.Observation.Actors.All(a => a.IsMounted == false))
                        foreach (TrainingPolicyActor actor in current.Observation.Actors.Where(a => !a.IsPlayer && a.Health > 0f)) ReadReply(actor.AgentId);
                }
            }
            catch (Exception exception)
            {
                lock (_gate) { _errors++; _lastError = "local policy mailbox: " + exception.GetType().Name; _decisions.Clear(); }
            }
        }

        private void ReadReply(int agentId)
        {
            string path = Path.Combine(_directory, "actions", agentId.ToString(CultureInfo.InvariantCulture) + ".json");
            try
            {
                string json;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (file.Length <= 0 || file.Length > MaxJsonBytes) throw new InvalidDataException("action size");
                    byte[] bytes = new byte[(int)file.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int n = file.Read(bytes, offset, bytes.Length - offset);
                        if (n <= 0) throw new InvalidDataException("incomplete action");
                        offset += n;
                    }
                    if (file.ReadByte() != -1) throw new InvalidDataException("action changed during read");
                    json = new UTF8Encoding(false, true).GetString(bytes);
                }
                JObject value;
                using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 8, DateParseHandling = DateParseHandling.None })
                {
                    value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("trailing JSON");
                }
                TrainingPolicyDecision decision = ParseDecision(value, agentId);
                lock (_gate)
                {
                    PublishedObservation issued;
                    if (_disposed || _current == null || _current.Epoch != _epoch || decision.EpisodeId != _current.Observation.EpisodeId
                        || !_issued.TryGetValue(decision.Sequence, out issued) || !Fresh(issued.Timestamp)
                        || issued.Observation.EpisodeId != decision.EpisodeId
                        || _current.Observation.Actors.Any(a => a.IsMounted != false)
                        || issued.Observation.Actors.Any(a => a.IsMounted != false)
                        || !issued.Observation.Actors.Any(a => a.AgentId == agentId && !a.IsPlayer && a.IsMounted == false && a.Health > 0f)
                        || decision.Sequence > _current.Observation.Sequence
                        || _current.Observation.Sequence - decision.Sequence > MaxSequenceLag)
                    { _decisions.Remove(agentId); return; }
                    // Re-reading the same file never extends its lifetime: use
                    // the observation's original publication timestamp.
                    _decisions[agentId] = new DecisionEntry { Decision = CopyDecision(decision, Deadline(issued.Timestamp)), Timestamp = issued.Timestamp };
                }
            }
            catch (FileNotFoundException) { lock (_gate) _decisions.Remove(agentId); }
            catch (DirectoryNotFoundException) { lock (_gate) _decisions.Remove(agentId); }
            catch (Exception)
            {
                lock (_gate) { _decisions.Remove(agentId); _invalidReplies++; }
            }
        }

        private TrainingPolicyDecision ParseDecision(JObject value, int fileAgentId)
        {
            string[] keys = { "protocol", "session_id", "episode_id", "sequence", "agent_id", "model_id", "model_sha256", "attack", "move_x", "move_y" };
            if (value.Properties().Count() != keys.Length || keys.Any(k => value.Property(k, StringComparison.Ordinal) == null))
                throw new InvalidDataException("unexpected action fields");
            string protocol = StringValue(value["protocol"]), session = StringValue(value["session_id"]), episode = StringValue(value["episode_id"]);
            string model = StringValue(value["model_id"]), hash = StringValue(value["model_sha256"]), attack = StringValue(value["attack"]);
            if (protocol != Protocol || session != _sessionId || !ValidGuid(episode) || !ValidModelId(model) || !ValidHash(hash)
                || (attack != "native" && attack != "up" && attack != "down" && attack != "left" && attack != "right")
                || value["sequence"].Type != JTokenType.Integer || value["agent_id"].Type != JTokenType.Integer)
                throw new InvalidDataException("invalid action");
            long sequence = (long)value["sequence"];
            int agentId = (int)value["agent_id"];
            if (sequence < 0 || agentId != fileAgentId) throw new InvalidDataException("invalid action identity");
            float? moveX = NullableCoordinate(value["move_x"]), moveY = NullableCoordinate(value["move_y"]);
            if (moveX.HasValue != moveY.HasValue) throw new InvalidDataException("partial movement vector");
            return new TrainingPolicyDecision(0) { AgentId = agentId, SessionId = session, EpisodeId = episode, Sequence = sequence, ModelId = model,
                ModelSha256 = hash, Attack = attack, MoveX = moveX, MoveY = moveY };
        }

        private static float? NullableCoordinate(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new InvalidDataException("invalid coordinate");
            double value = (double)token;
            if (!Finite(value) || value < -1d || value > 1d) throw new InvalidDataException("coordinate out of range");
            return (float)value;
        }

        private static string StringValue(JToken token) { return token != null && token.Type == JTokenType.String ? (string)token : null; }
        private static bool ValidGuid(string value) { Guid parsed; return value != null && Guid.TryParseExact(value, "N", out parsed) && parsed.ToString("N") == value; }
        private static bool ValidHash(string value) { return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        private static bool ValidModelId(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= 128
                && value.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.');
        }
        private static bool ValidActor(TrainingPolicyActor a)
        {
            return a != null && a.IsMounted.HasValue && a.AgentId >= 0 && (a.Side == 0 || a.Side == 1) && Finite(a.X) && Finite(a.Y) && Finite(a.Z)
                && Finite(a.LookX) && Finite(a.LookY) && Finite(a.VelocityX) && Finite(a.VelocityY) && Finite(a.Health)
                && Finite(a.HealthLimit) && a.Health >= 0f && a.HealthLimit > 0f && a.Health <= a.HealthLimit
                && Finite(a.ActionProgress) && (a.WeaponClass == null || a.WeaponClass.Length <= 64);
        }
        private bool Fresh(long issued) { double age = (Stopwatch.GetTimestamp() - issued) / (double)Stopwatch.Frequency; return age >= 0d && age <= _maxAgeMs / 1000d; }
        private long Deadline(long issued)
        {
            long duration = (Stopwatch.Frequency / 1000L) * _maxAgeMs + (Stopwatch.Frequency % 1000L) * _maxAgeMs / 1000L;
            return issued > long.MaxValue - duration ? long.MaxValue : issued + duration;
        }
        private bool RejectObservation() { lock (_gate) return RejectObservationLocked(); }
        private bool RejectObservationLocked() { _epoch++; _pending = null; _issued.Clear(); _decisions.Clear(); return false; }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        private static TrainingPolicyActor CopyActor(TrainingPolicyActor a)
        {
            return new TrainingPolicyActor { AgentId = a.AgentId, Side = a.Side, IsPlayer = a.IsPlayer, IsMounted = a.IsMounted, X = a.X, Y = a.Y, Z = a.Z,
                LookX = a.LookX, LookY = a.LookY, VelocityX = a.VelocityX, VelocityY = a.VelocityY, Health = a.Health,
                HealthLimit = a.HealthLimit, ActionType = a.ActionType, ActionStage = a.ActionStage, ActionDirection = a.ActionDirection,
                ActionProgress = a.ActionProgress, WeaponClass = a.WeaponClass ?? string.Empty };
        }
        private static TrainingPolicyDecision CopyDecision(TrainingPolicyDecision d, long validUntil)
        {
            return new TrainingPolicyDecision(validUntil) { AgentId = d.AgentId, SessionId = d.SessionId, EpisodeId = d.EpisodeId, Sequence = d.Sequence,
                ModelId = d.ModelId, ModelSha256 = d.ModelSha256, Attack = d.Attack, MoveX = d.MoveX, MoveY = d.MoveY };
        }
        private static void WriteAtomic(string path, byte[] content)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(content, 0, content.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { } }
        }
    }
}
