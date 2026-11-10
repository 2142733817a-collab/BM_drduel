using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiSealedSegment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal sealed partial class AdimiToolsAiLearningStoreInstance
{
    private const string ArchiveStateFile = "learning-sealed-state.json";
    private const string ArchiveIntentFile = "learning-seal-intent.json";
    private const long MaxCompactSnapshotBytes = 128L * 1024 * 1024;
    private const int MaxArchiveSegments = 256;
    private const int MaxArchiveMetadataBytes = 128 * 1024;
    private const int MaxArchiveReceiptBytes = 256 * 1024;

    private sealed record WriterItem(AdimiToolsAiLearningEpisode? Episode, TaskCompletionSource<AdimiToolsAiSealedSegment?>? SealRequest, Confirmation? Confirmation);
    private sealed record Confirmation(string Id, long Size, string Sha256, TaskCompletionSource<bool> Completion);
    private readonly HashSet<TaskCompletionSource<bool>> _confirmationRequests = new();
    private sealed class ArchiveCaptureException : Exception { internal ArchiveCaptureException(Exception inner) : base("compact capture failed", inner) { } }

    private sealed class ArchiveState
    {
        public int Version { get; set; }
        public List<AdimiToolsAiSealedSegment> Segments { get; set; } = new();
        public string SampleFile { get; set; } = "";
        public long SampleBytes { get; set; }
        public string SampleSha256 { get; set; } = "";
        public long ExpectedLastSequence { get; set; }
        public string IdentityFile { get; set; } = "";
        public long IdentityCount { get; set; }
        public string IdentitySha256 { get; set; } = "";
        public long ValidRounds { get; set; }
        public long SoloWins { get; set; }
    }

    private sealed class ArchiveIntent
    {
        public int Version { get; set; }
        public string? PreviousStateSha256 { get; set; }
        public ArchiveState? State { get; set; }
    }

    private sealed class ArchiveReceipt
    {
        public int Version { get; set; }
        public AdimiToolsAiSealedSegment? Segment { get; set; }
        public string StateSha256 { get; set; } = "";
        public ArchiveState? State { get; set; }
    }

    internal Task<AdimiToolsAiSealedSegment?> RequestSealAsync()
    {
        var request = new TaskCompletionSource<AdimiToolsAiSealedSegment?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed || !_ready || _sealRequests.Count != 0 || !_queue.Writer.TryWrite(new WriterItem(null, request, null)))
            {
                request.TrySetException(new IOException("recording writer cannot accept a seal request"));
                return request.Task;
            }
            _sealRequests.Add(request);
        }
        return request.Task;
    }

    // Only a trusted integration that has verified a durable client ACK may
    // invoke this method. The values alone are not an authentication token.
    internal Task<bool> ConfirmAndDeleteAsync(string id, long size, string sha256)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed || !_ready || !ValidId(id) || size <= 0 || !ValidSha(sha256)
                || !_queue.Writer.TryWrite(new WriterItem(null, null, new Confirmation(id, size, sha256, completion))))
            {
                completion.TrySetException(new IOException("recording writer cannot accept confirmation"));
                return completion.Task;
            }
            _confirmationRequests.Add(completion);
        }
        return completion.Task;
    }

    private void ProcessConfirmation(Confirmation confirmation)
    {
        try
        {
            ArchiveState state = _archiveState ?? throw new InvalidDataException("no archive state");
            byte[] stateBytes = ReadSmallFile(Path.Combine(_directory, ArchiveStateFile), MaxArchiveMetadataBytes);
            _ = ParseState(stateBytes);
            if (HashBytes(stateBytes) != HashBytes(JsonSerializer.SerializeToUtf8Bytes(state))) throw new InvalidDataException("archive state changed");
            ValidateArchive(state, verifySegments: true);
            AdimiToolsAiSealedSegment? segment = state.Segments.SingleOrDefault(item => item.Id == confirmation.Id);
            if (segment == null || segment.Size != confirmation.Size || segment.Sha256 != confirmation.Sha256)
                throw new InvalidDataException("confirmation does not match a sealed segment");
            string rawPath = Path.Combine(_directory, "episodes.jsonl.rot-" + segment.Id);
            string receiptPath = Path.Combine(_directory, "learning-received-" + segment.Id + ".json");
            if (File.Exists(receiptPath))
            {
                ValidateReceipt(state, segment, receiptPath);
            }
            else
            {
                if (!File.Exists(rawPath) || HashFile(rawPath, segment.Size) != segment.Sha256)
                    throw new InvalidDataException("raw segment is missing or changed before confirmation");
                var receipt = new ArchiveReceipt { Version = 1, Segment = segment, StateSha256 = HashBytes(stateBytes), State = state };
                WriteImmutable(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt));
                _sealFaultHook?.Invoke("receipt");
            }
            // A previous attempt may have created the receipt but failed to
            // sync its directory. Repeat the sync before every deletion.
            _sealSyncDirectory(_directory);
            if (File.Exists(rawPath))
            {
                if (HashFile(rawPath, segment.Size) != segment.Sha256) throw new InvalidDataException("raw segment changed after confirmation");
                File.Delete(rawPath);
                _sealFaultHook?.Invoke("deleted");
                _sealSyncDirectory(_directory);
            }
            confirmation.Completion.TrySetResult(true);
        }
        catch (Exception exception) { confirmation.Completion.TrySetException(exception); }
        finally { lock (_gate) { _confirmationRequests.Remove(confirmation.Completion); } }
    }

    internal IReadOnlyList<AdimiToolsAiSealedSegment> GetSealedSegments()
    {
        ArchiveState? state;
        lock (_gate)
        {
            if (!_ready) throw new IOException("recording state is unavailable");
            state = _archiveState;
        }
        if (state == null) return Array.Empty<AdimiToolsAiSealedSegment>();
        byte[] published = ReadSmallFile(Path.Combine(_directory, ArchiveStateFile), MaxArchiveMetadataBytes);
        _ = ParseState(published);
        if (HashBytes(published) != HashBytes(JsonSerializer.SerializeToUtf8Bytes(state))) throw new InvalidDataException("archive state changed");
        ValidateArchive(state, verifySegments: true);
        lock (_gate)
        {
            if (!_ready || !ReferenceEquals(state, _archiveState)) throw new IOException("archive changed while checking");
        }
        return state.Segments.Where(x => File.Exists(Path.Combine(_directory, "episodes.jsonl.rot-" + x.Id)))
            .Select(x => new AdimiToolsAiSealedSegment { Id = x.Id, Size = x.Size, Sha256 = x.Sha256 }).ToArray();
    }

    private void ProcessSeal(TaskCompletionSource<AdimiToolsAiSealedSegment?> request, HashSet<Guid> writerIds)
    {
        long length = -1;
        string? originalHash = null;
        try
        {
            lock (_gate)
            {
                if (!_ready) throw new IOException("recording is not ready");
                length = _committedLength;
                if (length != 0) { _ready = false; _archiveEpoch++; }
            }
            if (length == 0) { request.TrySetResult(null); return; }
            SignalModel();
            ClearReceipt();
            originalHash = HashFile(_file, length);
            ArchiveState next = PrepareArchive(length, writerIds);
            if (next.Segments[^1].Sha256 != originalHash) throw new InvalidDataException("active journal changed while preparing seal");
            byte[] nextBytes = JsonSerializer.SerializeToUtf8Bytes(next);
            string statePath = Path.Combine(_directory, ArchiveStateFile);
            string? previousHash = File.Exists(statePath) ? HashBytes(ReadSmallFile(statePath, MaxArchiveMetadataBytes)) : null;
            var intent = new ArchiveIntent { Version = 1, PreviousStateSha256 = previousHash, State = next };
            WriteAtomic(Path.Combine(_directory, ArchiveIntentFile), JsonSerializer.SerializeToUtf8Bytes(intent));
            _sealFaultHook?.Invoke("intent");
            FinishArchiveIntent(intent, nextBytes);
            _sealFaultHook?.Invoke("finished");
            HashSet<Guid> archivedIds = ValidateArchive(next, verifySegments: true);
            lock (_gate)
            {
                _archiveState = next;
                _archiveIds = archivedIds;
                _committedLength = 0;
                _archiveEpoch++;
                _ready = true;
            }
            SignalModel();
            var segment = next.Segments[^1];
            request.TrySetResult(new AdimiToolsAiSealedSegment { Id = segment.Id, Size = segment.Size, Sha256 = segment.Sha256 });
        }
        catch (Exception exception)
        {
            request.TrySetException(exception);
            // Before an intent exists, an unchanged active file and unchanged
            // checkpoint allow normal recording to resume. Any ambiguity
            // stops the writer for deterministic recovery on restart.
            bool intact = false;
            try
            {
                string statePath = Path.Combine(_directory, ArchiveStateFile);
                bool sameState = _archiveState == null ? !File.Exists(statePath)
                    : File.Exists(statePath) && HashBytes(ReadSmallFile(statePath, MaxArchiveMetadataBytes))
                        == HashBytes(JsonSerializer.SerializeToUtf8Bytes(_archiveState));
                intact = originalHash != null && !File.Exists(Path.Combine(_directory, ArchiveIntentFile))
                    && sameState && File.Exists(_file) && HashFile(_file, length) == originalHash;
            }
            catch (Exception) { }
            if (intact)
            {
                lock (_gate) { _archiveEpoch++; _ready = true; }
                SignalModel();
                return;
            }
            // An interrupted transaction is recovered during initialization.
            // Do not append to an ambiguous active path in this process.
            throw;
        }
        finally { lock (_gate) { _sealRequests.Remove(request); } }
    }

    private ArchiveState PrepareArchive(long committedLength, HashSet<Guid> writerIds)
    {
        ArchiveState? prior = _archiveState;
        if (prior != null && prior.Segments.Count >= MaxArchiveSegments) throw new InvalidDataException("sealed segment limit reached");
        if (prior != null)
        {
            byte[] priorBytes = ReadSmallFile(Path.Combine(_directory, ArchiveStateFile), MaxArchiveMetadataBytes);
            _ = ParseState(priorBytes);
            if (HashBytes(priorBytes) != HashBytes(JsonSerializer.SerializeToUtf8Bytes(prior))) throw new InvalidDataException("prior archive state changed");
            HashSet<Guid> verified = ValidateArchive(prior, verifySegments: true);
            if (!verified.SetEquals(_archiveIds)) throw new InvalidDataException("prior archive identities changed");
        }
        else if (File.Exists(Path.Combine(_directory, ArchiveStateFile))) throw new InvalidDataException("unexpected archive state appeared");
        if (!HasRecordingSpace(4096, out _)) throw new IOException("insufficient space for sealing");
        string id = Guid.NewGuid().ToString("N");
        string samplesName = "learning-samples-" + id + ".jsonl";
        string idsName = "learning-identities-" + id + ".txt";
        string samplesPath = Path.Combine(_directory, samplesName);
        string idsPath = Path.Combine(_directory, idsName);
        var ids = new HashSet<Guid>(_archiveIds);
        long sequence = prior?.ExpectedLastSequence ?? 0;
        (long valid, long wins, long corrupt, bool ok) scan;
        using (var samples = new FileStream(samplesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            if (prior != null)
            {
                if (!HasRecordingSpace(prior.SampleBytes, out _)) throw new IOException("insufficient space to copy compact snapshot");
                using var old = new FileStream(Path.Combine(_directory, prior.SampleFile), FileMode.Open, FileAccess.Read, FileShare.Read);
                old.CopyTo(samples);
            }
            using var source = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length != committedLength) throw new IOException("active journal changed before seal");
            scan = ScanRows(source, committedLength, ids, null, null, null, episode =>
            {
                if (!AdimiToolsAiLearningTrainer.TryMakeCompactSample(episode, sequence + 1, out var compact)) return;
                byte[] bytes = AdimiToolsAiTrainingSampleCodec.Encode(compact!);
                if (samples.Position + bytes.Length + 1 > MaxCompactSnapshotBytes || !HasRecordingSpace(bytes.Length + 1L, out _))
                    throw new IOException("compact snapshot limit or free-space reserve reached");
                samples.Write(bytes, 0, bytes.Length);
                samples.WriteByte((byte)'\n');
                sequence++;
            });
            if (scan.corrupt != 0 || !scan.ok || !ids.SetEquals(writerIds)) throw new InvalidDataException("journal identities or records do not match");
            if (scan.valid + (prior?.ValidRounds ?? 0) != _validRounds || scan.wins + (prior?.SoloWins ?? 0) != _soloWins)
                throw new InvalidDataException("journal counts do not match");
            samples.Flush(flushToDisk: true);
        }
        _sealSyncDirectory(_directory);
        _sealFaultHook?.Invoke("samples");
        long identityBytes = checked(ids.Count * 33L);
        if (!HasRecordingSpace(identityBytes, out _)) throw new IOException("insufficient space for identities");
        using (var output = new FileStream(idsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            foreach (Guid item in ids.OrderBy(value => value.ToString("N"), StringComparer.Ordinal))
            {
                byte[] row = Encoding.ASCII.GetBytes(item.ToString("N") + "\n");
                output.Write(row, 0, row.Length);
            }
            output.Flush(flushToDisk: true);
        }
        _sealSyncDirectory(_directory);
        _sealFaultHook?.Invoke("identities");
        var entry = new AdimiToolsAiSealedSegment { Id = id, Size = committedLength, Sha256 = HashFile(_file, committedLength) };
        var state = new ArchiveState
        {
            Version = 1, Segments = prior == null ? new() : prior.Segments.Select(x => new AdimiToolsAiSealedSegment { Id = x.Id, Size = x.Size, Sha256 = x.Sha256 }).ToList(),
            SampleFile = samplesName, SampleBytes = new FileInfo(samplesPath).Length, SampleSha256 = HashFile(samplesPath, new FileInfo(samplesPath).Length),
            ExpectedLastSequence = sequence, IdentityFile = idsName, IdentityCount = ids.Count, IdentitySha256 = HashFile(idsPath, identityBytes),
            ValidRounds = scan.valid + (prior?.ValidRounds ?? 0), SoloWins = scan.wins + (prior?.SoloWins ?? 0),
        };
        state.Segments.Add(entry);
        ValidateArchive(state, verifySegments: false);
        return state;
    }

    private void RecoverAndLoadArchive()
    {
        Directory.CreateDirectory(_directory);
        string intentPath = Path.Combine(_directory, ArchiveIntentFile);
        if (File.Exists(intentPath))
        {
            ArchiveIntent intent = ParseIntent(ReadSmallFile(intentPath, MaxArchiveMetadataBytes));
            if (intent.Version != 1 || intent.State == null) throw new InvalidDataException("invalid seal intent");
            FinishArchiveIntent(intent, JsonSerializer.SerializeToUtf8Bytes(intent.State));
        }
        string statePath = Path.Combine(_directory, ArchiveStateFile);
        if (!File.Exists(statePath)) { _archiveState = null; _archiveIds = new(); return; }
        ArchiveState state = ParseState(ReadSmallFile(statePath, MaxArchiveMetadataBytes));
        HashSet<Guid> ids = ValidateArchive(state, verifySegments: true);
        lock (_gate) { _archiveState = state; _archiveIds = ids; _archiveEpoch++; }
    }

    private void FinishArchiveIntent(ArchiveIntent intent, byte[] stateBytes)
    {
        ArchiveState state = intent.State ?? throw new InvalidDataException("missing intended state");
        if (intent.Version != 1 || (intent.PreviousStateSha256 != null && !ValidSha(intent.PreviousStateSha256)))
            throw new InvalidDataException("invalid archive intent");
        HashSet<Guid> nextIds = ValidateArchive(state, verifySegments: false);
        string statePath = Path.Combine(_directory, ArchiveStateFile);
        byte[]? currentBytes = File.Exists(statePath) ? ReadSmallFile(statePath, MaxArchiveMetadataBytes) : null;
        string? currentHash = currentBytes == null ? null : HashBytes(currentBytes);
        string nextHash = HashBytes(stateBytes);
        if (currentHash != nextHash && currentHash != intent.PreviousStateSha256) throw new InvalidDataException("unexpected archive state");
        if (currentBytes != null && currentHash != nextHash)
        {
            ArchiveState previous = ParseState(currentBytes);
            HashSet<Guid> previousIds = ValidateArchive(previous, verifySegments: true);
            if (previous.Segments.Count + 1 != state.Segments.Count || !previousIds.IsSubsetOf(nextIds)
                || previous.ExpectedLastSequence > state.ExpectedLastSequence || previous.ValidRounds > state.ValidRounds
                || previous.SoloWins > state.SoloWins || previous.SampleBytes > state.SampleBytes)
                throw new InvalidDataException("archive state does not extend its predecessor");
            for (int index = 0; index < previous.Segments.Count; index++)
            {
                var old = previous.Segments[index]; var next = state.Segments[index];
                if (old.Id != next.Id || old.Size != next.Size || old.Sha256 != next.Sha256)
                    throw new InvalidDataException("archive segment history changed");
            }
            if (HashFilePrefix(Path.Combine(_directory, state.SampleFile), previous.SampleBytes) != previous.SampleSha256)
                throw new InvalidDataException("compact history prefix changed");
        }
        else if (currentBytes == null && currentHash != nextHash && state.Segments.Count != 1)
            throw new InvalidDataException("initial archive cannot contain earlier segments");
        AdimiToolsAiSealedSegment segment = state.Segments[^1];
        string archive = Path.Combine(_directory, "episodes.jsonl.rot-" + segment.Id);
        if (File.Exists(archive))
        {
            if (HashFile(archive, segment.Size) != segment.Sha256) throw new InvalidDataException("sealed journal mismatch");
            if (File.Exists(_file) && new FileInfo(_file).Length != 0) throw new InvalidDataException("ambiguous active journal");
        }
        else
        {
            if (currentHash == nextHash || !File.Exists(_file) || HashFile(_file, segment.Size) != segment.Sha256)
                throw new InvalidDataException("seal source does not match intent");
            File.Move(_file, archive);
            _sealSyncDirectory(_directory);
            _sealFaultHook?.Invoke("rotated");
        }
        if (currentHash != nextHash) WriteAtomic(statePath, stateBytes);
        _sealFaultHook?.Invoke("state");
        if (!File.Exists(_file))
        {
            using var active = new FileStream(_file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            active.Flush(flushToDisk: true);
            _sealSyncDirectory(_directory);
        }
        _sealFaultHook?.Invoke("active");
        File.Delete(Path.Combine(_directory, ArchiveIntentFile));
        _sealSyncDirectory(_directory);
    }

    private HashSet<Guid> ValidateArchive(ArchiveState state, bool verifySegments)
    {
        if (state.Version != 1 || state.Segments == null || state.Segments.Count < 1 || state.Segments.Count > MaxArchiveSegments
            || state.ExpectedLastSequence < 0 || state.ExpectedLastSequence > AdimiToolsAiTrainingSampleCodec.MaxSamples
            || state.IdentityCount < 0 || state.IdentityCount > MaxJournalIdentities || state.ValidRounds < 0
            || state.SoloWins < 0 || state.SoloWins > state.ValidRounds || state.ValidRounds > state.IdentityCount
            || state.SampleBytes < 0 || state.SampleBytes > MaxCompactSnapshotBytes || !ValidSha(state.SampleSha256) || !ValidSha(state.IdentitySha256))
            throw new InvalidDataException("invalid archive metadata");
        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AdimiToolsAiSealedSegment entry in state.Segments)
        {
            if (entry == null || !ValidId(entry.Id) || entry.Size <= 0 || !ValidSha(entry.Sha256) || !segmentIds.Add(entry.Id))
                throw new InvalidDataException("invalid sealed segment metadata");
        }
        string lastId = state.Segments[^1].Id;
        if (state.SampleFile != "learning-samples-" + lastId + ".jsonl" || state.IdentityFile != "learning-identities-" + lastId + ".txt")
            throw new InvalidDataException("invalid archive file names");
        string samplePath = Path.Combine(_directory, state.SampleFile), idsPath = Path.Combine(_directory, state.IdentityFile);
        if (HashFile(samplePath, state.SampleBytes) != state.SampleSha256
            || HashFile(idsPath, checked(state.IdentityCount * 33)) != state.IdentitySha256) throw new InvalidDataException("archive snapshot hash mismatch");
        var ids = ReadIdentities(idsPath, state.IdentityCount);
        var trainer = AdimiToolsAiTrainingSampleCodec.Rebuild(ReadArchiveSamples(state, ids), state.ExpectedLastSequence);
        _ = trainer.Samples;
        if (verifySegments)
        {
            foreach (AdimiToolsAiSealedSegment entry in state.Segments)
            {
                string path = Path.Combine(_directory, "episodes.jsonl.rot-" + entry.Id);
                if (File.Exists(path))
                {
                    if (HashFile(path, entry.Size) != entry.Sha256) throw new InvalidDataException("sealed segment mismatch");
                }
                else ValidateReceipt(state, entry, Path.Combine(_directory, "learning-received-" + entry.Id + ".json"), ids);
            }
        }
        return ids;
    }

    private void ValidateReceipt(ArchiveState current, AdimiToolsAiSealedSegment entry, string path, HashSet<Guid>? currentIds = null)
    {
        ArchiveReceipt receipt = ParseReceipt(ReadSmallFile(path, MaxArchiveReceiptBytes));
        ArchiveState origin = receipt.State ?? throw new InvalidDataException("receipt checkpoint missing");
        AdimiToolsAiSealedSegment accepted = receipt.Segment ?? throw new InvalidDataException("receipt segment missing");
        if (receipt.Version != 1 || accepted.Id != entry.Id || accepted.Size != entry.Size || accepted.Sha256 != entry.Sha256
            || !ValidSha(receipt.StateSha256) || receipt.StateSha256 != HashBytes(JsonSerializer.SerializeToUtf8Bytes(origin)))
            throw new InvalidDataException("receipt does not match segment and checkpoint");
        HashSet<Guid> oldIds = ValidateArchive(origin, verifySegments: false);
        currentIds ??= ValidateArchive(current, verifySegments: false);
        if (origin.Segments.Count > current.Segments.Count || !oldIds.IsSubsetOf(currentIds)
            || origin.ExpectedLastSequence > current.ExpectedLastSequence || origin.SampleBytes > current.SampleBytes
            || !origin.Segments.Any(item => item.Id == entry.Id && item.Size == entry.Size && item.Sha256 == entry.Sha256))
            throw new InvalidDataException("receipt checkpoint does not belong to current history");
        for (int i = 0; i < origin.Segments.Count; i++)
        {
            var old = origin.Segments[i]; var next = current.Segments[i];
            if (old.Id != next.Id || old.Size != next.Size || old.Sha256 != next.Sha256)
                throw new InvalidDataException("receipt segment history changed");
        }
        if (HashFilePrefix(Path.Combine(_directory, current.SampleFile), origin.SampleBytes) != origin.SampleSha256)
            throw new InvalidDataException("receipt compact history changed");
    }

    private IEnumerable<AdimiToolsAiTrainingSample> ReadArchiveSamples(ArchiveState state, HashSet<Guid> ids)
    {
        using var stream = new FileStream(Path.Combine(_directory, state.SampleFile), FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (byte[]? line in ReadBoundedLinesWithin(stream, state.SampleBytes))
        {
            if (line == null || line.Length > AdimiToolsAiTrainingSampleCodec.MaxSampleBytes) throw new InvalidDataException("invalid compact sample line");
            var sample = AdimiToolsAiTrainingSampleCodec.Decode(line);
            if (!ids.Contains(Guid.ParseExact(sample.EpisodeId, "N"))) throw new InvalidDataException("compact sample identity missing");
            yield return sample;
        }
    }

    private static HashSet<Guid> ReadIdentities(string path, long count)
    {
        var ids = new HashSet<Guid>();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var row = new byte[33];
        for (long i = 0; i < count; i++)
        {
            int offset = 0;
            while (offset < row.Length) { int read = file.Read(row, offset, row.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            string value = Encoding.ASCII.GetString(row, 0, 32);
            if (row[32] != (byte)'\n' || !ValidId(value) || !ids.Add(Guid.ParseExact(value, "N"))) throw new InvalidDataException("invalid archive identities");
        }
        if (file.ReadByte() != -1) throw new InvalidDataException("trailing archive identities");
        return ids;
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        if (bytes.Length > MaxArchiveMetadataBytes || !HasRecordingSpace(bytes.Length, out _)) throw new IOException("archive metadata space unavailable");
        string temporary = Path.Combine(_directory, "learning-state-temp-" + Guid.NewGuid().ToString("N"));
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes, 0, bytes.Length);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        _sealSyncDirectory(_directory);
    }

    private void WriteImmutable(string path, byte[] bytes)
    {
        if (bytes.Length > MaxArchiveReceiptBytes || !HasRecordingSpace(bytes.Length, out _)) throw new IOException("receipt space unavailable");
        string temporary = Path.Combine(_directory, "learning-receipt-temp-" + Guid.NewGuid().ToString("N"));
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes, 0, bytes.Length);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: false);
        _sealSyncDirectory(_directory);
    }

    private static byte[] ReadSmallFile(string path, int max)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > max) throw new InvalidDataException("archive metadata too large");
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = file.Read(buffer, 0, Math.Min(buffer.Length, max + 1 - (int)memory.Length))) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > max) throw new InvalidDataException("archive metadata too large");
        }
        return memory.ToArray();
    }

    private static ArchiveState ParseState(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        ValidateStateShape(document.RootElement);
        return JsonSerializer.Deserialize<ArchiveState>(bytes) ?? throw new InvalidDataException("empty archive state");
    }

    private static ArchiveIntent ParseIntent(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        ExactFields(document.RootElement, "Version", "PreviousStateSha256", "State");
        ValidateStateShape(document.RootElement.GetProperty("State"));
        return JsonSerializer.Deserialize<ArchiveIntent>(bytes) ?? throw new InvalidDataException("empty archive intent");
    }

    private static ArchiveReceipt ParseReceipt(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement root = document.RootElement;
        ExactFields(root, "Version", "Segment", "StateSha256", "State");
        ExactFields(root.GetProperty("Segment"), "id", "size", "sha256");
        ValidateStateShape(root.GetProperty("State"));
        return JsonSerializer.Deserialize<ArchiveReceipt>(bytes) ?? throw new InvalidDataException("empty archive receipt");
    }

    private static void ValidateStateShape(JsonElement root)
    {
        ExactFields(root, "Version", "Segments", "SampleFile", "SampleBytes", "SampleSha256", "ExpectedLastSequence",
            "IdentityFile", "IdentityCount", "IdentitySha256", "ValidRounds", "SoloWins");
        JsonElement segments = root.GetProperty("Segments");
        if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() > MaxArchiveSegments)
            throw new InvalidDataException("invalid archive segment list");
        foreach (JsonElement segment in segments.EnumerateArray()) ExactFields(segment, "id", "size", "sha256");
    }

    private static void ExactFields(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("invalid archive object");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("unknown or duplicate archive field");
        if (remaining.Count != 0) throw new InvalidDataException("missing archive field");
    }

    private static string HashFile(string path, long expectedLength)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("archive path is a link");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != expectedLength) throw new InvalidDataException("archive file length mismatch");
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(file)).ToLowerInvariant();
    }

    private static string HashFilePrefix(string path, long length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (length > 0)
        {
            int read = file.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
            if (read == 0) throw new EndOfStreamException("compact history prefix is truncated");
            hash.AppendData(buffer, 0, read);
            length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool ValidId(string value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out Guid id) && id.ToString("N") == value;
    private static bool ValidSha(string value) => value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    [DllImport("libc", SetLastError = true, EntryPoint = "open")] private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")] private static extern int FsyncDirectory(int fd);
    [DllImport("libc", SetLastError = true, EntryPoint = "close")] private static extern int CloseDirectory(int fd);

    private static void DurableDirectorySync(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("durable sealing is currently supported on Linux only");
        int fd = OpenDirectory(path, 0x10000 | 0x80000); // O_DIRECTORY | O_CLOEXEC, read only.
        if (fd < 0) throw new IOException("cannot open archive directory for sync", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            if (FsyncDirectory(fd) != 0) throw new IOException("cannot sync archive directory", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { CloseDirectory(fd); }
    }
}
