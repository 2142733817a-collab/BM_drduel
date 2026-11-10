using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AdimiDuel_Server.Duel;

// These records contain only detached values, never an Agent, Peer or other
// live game object. A completed episode is copied before it enters the queue.
internal sealed class AdimiToolsAiLearningEpisode
{
    // Missing version in an older journal is unknown, never a current schema.
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = string.Empty;
    public int ArenaId { get; set; }
    public int ArenaKind { get; set; }
    public int Difficulty { get; set; }
    public string MatchType { get; set; } = "ai";
    public int WinningSide { get; set; } = -1;
    public string StartedUtc { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int InitialPlayers { get; set; }
    public int InitialBots { get; set; }
    public bool Valid { get; set; }
    public bool SoloAttributedWin { get; set; }
    public bool Truncated { get; set; }
    public List<AdimiToolsAiLearningFrame> Frames { get; set; } = new();
    public List<AdimiToolsAiLearningEvent> Events { get; set; } = new();
}

internal sealed class AdimiToolsAiLearningFrame
{
    public double Seconds { get; set; }
    public List<AdimiToolsAiLearningActor> Actors { get; set; } = new();
}

internal sealed class AdimiToolsAiLearningActor
{
    public int Id { get; set; }
    public bool IsPlayer { get; set; }
    public int Side { get; set; } = -1;
    public string PlayerToken { get; set; } = string.Empty;
    public string CharacterId { get; set; } = string.Empty;
    public string WeaponId { get; set; } = string.Empty;
    public string WeaponClass { get; set; } = string.Empty;
    public float WeaponLength { get; set; }
    public bool IsMounted { get; set; }
    public int MountId { get; set; } = -1;
    public float MountX { get; set; }
    public float MountY { get; set; }
    public float MountZ { get; set; }
    public float MountLookX { get; set; }
    public float MountLookY { get; set; }
    public float MountLookZ { get; set; }
    public float MountVelocityX { get; set; }
    public float MountVelocityY { get; set; }
    public float MountHealth { get; set; }
    public float MountHealthLimit { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float LookX { get; set; }
    public float LookY { get; set; }
    public float LookZ { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public float MoveInputX { get; set; }
    public float MoveInputY { get; set; }
    public float MountInputX { get; set; }
    public float MountInputY { get; set; }
    public float Health { get; set; }
    public float HealthLimit { get; set; }
    public int ActionType { get; set; }
    public int ActionStage { get; set; }
    public int ActionDirection { get; set; }
    public float ActionProgress { get; set; }
    public uint MovementFlags { get; set; }
    public uint EventFlags { get; set; }
}

internal sealed class AdimiToolsAiLearningEvent
{
    public string Kind { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public int AttackerId { get; set; }
    public int VictimId { get; set; }
    public bool IsBlocked { get; set; }
    public float Damage { get; set; }
    public int RemovalState { get; set; }
    public bool IsMissile { get; set; }
    public bool ThrustTipHit { get; set; }
    public string AttackerWeaponClass { get; set; } = string.Empty;
    public bool? IsAlternativeAttack { get; set; }
    public int AttackDirection { get; set; }
    public int StrikeType { get; set; }
    public int WeaponSlot { get; set; }
    public float AttackProgress { get; set; }
    public float ShotX { get; set; }
    public float ShotY { get; set; }
    public float ShotZ { get; set; }
    public float ShotVelocityX { get; set; }
    public float ShotVelocityY { get; set; }
    public float ShotVelocityZ { get; set; }
}

internal readonly struct AdimiToolsAiLearningCounts
{
    public bool Ready { get; }
    public long ValidRounds { get; }
    public long SoloAttributedWins { get; }
    public int Pending { get; }
    public long Errors { get; }
    public long Dropped { get; }
    public bool PausedForSpace { get; }

    internal AdimiToolsAiLearningCounts(bool ready, long validRounds, long soloAttributedWins, int pending, long errors, long dropped, bool pausedForSpace = false)
    {
        Ready = ready;
        ValidRounds = validRounds;
        SoloAttributedWins = soloAttributedWins;
        Pending = pending;
        Errors = errors;
        Dropped = dropped;
        PausedForSpace = pausedForSpace;
    }
}

internal static class AdimiToolsAiLearningStore
{
    private static readonly Lazy<AdimiToolsAiLearningStoreInstance> Instance = new(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);
    private static long _lastUnavailableWarning;

    // Starts the single lazy instance and its background journal scan without
    // waiting for file I/O or for a new completed round.
    public static void EnsureInitialized() { _ = GetCounts(); }

    public static bool TryEnqueue(AdimiToolsAiLearningEpisode episode)
    {
        try { return Instance.Value.TryEnqueue(episode); }
        catch (Exception exception)
        {
            AdimiToolsAiLearningLog.QueueThrottled(ref _lastUnavailableWarning,
                "Store unavailable (" + exception.GetType().Name + "); records cannot be saved. Check the storage path and restart the server.");
            return false;
        }
    }

    public static AdimiToolsAiLearningCounts GetCounts()
    {
        try { return Instance.Value.GetCounts(); }
        catch (Exception exception)
        {
            AdimiToolsAiLearningLog.QueueThrottled(ref _lastUnavailableWarning,
                "Store unavailable (" + exception.GetType().Name + "); records cannot be saved. Check the storage path and restart the server.");
            return new AdimiToolsAiLearningCounts(false, 0, 0, 0, 1, 0);
        }
    }

    internal static Task<AdimiToolsAiSealedSegment?> RequestSealAsync() => Instance.Value.RequestSealAsync();
    internal static Task<bool> ConfirmAndDeleteAsync(string id, long size, string sha256) => Instance.Value.ConfirmAndDeleteAsync(id, size, sha256);

    private static AdimiToolsAiLearningStoreInstance CreateDefault()
    {
        string? configured = Environment.GetEnvironmentVariable("ADIMI_AI_TRAINING_PATH");
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "Logs", "AiTraining")
            : configured;
        return new AdimiToolsAiLearningStoreInstance(Path.GetFullPath(path), publish: AdimiToolsAiLearningModel.Publish);
    }
}

// Console writes can fail (or block), so producers only schedule these warnings
// on the thread pool. Messages contain no episode contents or player identity.
internal static class AdimiToolsAiLearningLog
{
    private const long WarningIntervalMilliseconds = 30000;

    internal static bool ShouldWarn(ref long lastWarning)
    {
        long now = Math.Max(1, Environment.TickCount64);
        while (true)
        {
            long previous = Interlocked.Read(ref lastWarning);
            if (previous != 0 && now - previous < WarningIntervalMilliseconds) return false;
            if (Interlocked.CompareExchange(ref lastWarning, now, previous) == previous) return true;
        }
    }

    internal static void QueueThrottled(ref long lastWarning, string message)
    {
        if (!ShouldWarn(ref lastWarning)) return;
        try { ThreadPool.QueueUserWorkItem(static state => Write((string)state!), message); }
        catch (Exception) { }
    }

    internal static void Write(string message)
    {
        try { Console.Error.WriteLine("[Adimi AI Training] " + message); }
        catch (Exception) { }
    }
}

// The writer owns journal order and durable commits. A separate model worker
// reads only committed prefixes and never blocks that writer while training.
internal sealed partial class AdimiToolsAiLearningStoreInstance
{
    internal const int MaxFrames = 8192;
    internal const int MaxActorSnapshots = 16384;
    internal const int MaxEvents = 4096;
    private const int DefaultCapacity = 8;
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private const int MaxJournalIdentities = 1000000;
    private const int MaxReviewsBytes = 1024 * 1024;
    private const int MaxExcludedEpisodes = 25000;
    // Keep a reserve for the OS and game. Other applications may still consume it.
    internal const long DefaultMinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _file;
    private readonly string _reviewsFile;
    private readonly string _receiptFile;
    private readonly int _capacity;
    private readonly Channel<WriterItem> _queue;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _writer;
    private readonly Task _modelWorker;
    private readonly TaskCompletionSource<bool> _indexReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _modelSignal = new(0, 1);
    private readonly Action<AdimiToolsAiLearnedPolicy?>? _publish;
    private readonly Action? _modelScanHook;
    private readonly Action? _beforeReceiptReplaceHook;
    private readonly TimeSpan _reviewInterval;
    private readonly Func<string, long> _availableBytes;
    private readonly long _minimumFreeBytes;
    private readonly Action<string> _sealSyncDirectory;
    private readonly Action<string>? _sealFaultHook;
    private HashSet<Guid> _excluded = new();
    private string? _reviewDigest;
    private string? _appliedDigest;
    private bool _modelDirty = true;
    private bool _reviewFault;
    private bool _modelHasPublished;
    private AdimiToolsAiLearnedPolicy? _lastPublished;
    private AdimiToolsAiLearningTrainer? _trainer;
    private bool _ready;
    private bool _closed;
    private bool _writerFinished;
    private long _committedLength;
    private long _archiveEpoch;
    private ArchiveState? _archiveState;
    private HashSet<Guid> _archiveIds = new();
    private readonly HashSet<TaskCompletionSource<AdimiToolsAiSealedSegment?>> _sealRequests = new();
    private long _validRounds;
    private long _soloWins;
    private int _pending;
    private long _errors;
    private long _dropped;
    private bool _pausedForSpace;
    private long _lastUnavailableDropWarning;
    private long _lastInvalidDropWarning;
    private long _lastSerializationWarning;
    private long _lastReceiptWarning;

    internal AdimiToolsAiLearningStoreInstance(string directory, int capacity = DefaultCapacity, Action<AdimiToolsAiLearnedPolicy?>? publish = null,
        TimeSpan? reviewInterval = null, Action? modelScanHook = null, Action? beforeReceiptReplaceHook = null,
        Func<string, long>? availableBytes = null, long minimumFreeBytes = DefaultMinimumFreeBytes,
        Action<string>? sealSyncDirectory = null, Action<string>? sealFaultHook = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A storage directory is required.", nameof(directory));
        if (capacity < 1 || capacity > DefaultCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (reviewInterval.HasValue && (reviewInterval.Value < TimeSpan.FromMilliseconds(50) || reviewInterval.Value > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(reviewInterval));
        if (minimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(minimumFreeBytes));
        _directory = Path.GetFullPath(directory);
        _file = Path.Combine(_directory, "episodes.jsonl");
        _reviewsFile = Path.Combine(_directory, "reviews.json");
        _receiptFile = Path.Combine(_directory, "reviews.applied.sha256");
        _capacity = capacity;
        _publish = publish;
        _modelScanHook = modelScanHook;
        _beforeReceiptReplaceHook = beforeReceiptReplaceHook;
        _reviewInterval = reviewInterval ?? TimeSpan.FromSeconds(5);
        _availableBytes = availableBytes ?? (path => new DriveInfo(path).AvailableFreeSpace);
        _minimumFreeBytes = minimumFreeBytes;
        _sealSyncDirectory = sealSyncDirectory ?? DurableDirectorySync;
        _sealFaultHook = sealFaultHook;
        _queue = Channel.CreateBounded<WriterItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writer = Task.Run(WriteLoopAsync);
        _modelWorker = _publish == null ? Task.CompletedTask : Task.Run(ModelLoopAsync);
    }

    internal bool TryEnqueue(AdimiToolsAiLearningEpisode episode)
    {
        bool rejected = false;
        bool invalid = false;
        lock (_gate)
        {
            if (_closed || _pending >= _capacity || episode == null)
            {
                _dropped++;
                rejected = true;
                invalid = episode == null;
            }
            else _pending++;
        }
        if (rejected)
        {
            ReportDrop(invalid);
            return false;
        }

        bool accepted = false;
        try
        {
            if (TryCopy(episode!, out AdimiToolsAiLearningEpisode? snapshot))
            {
                accepted = _queue.Writer.TryWrite(new WriterItem(snapshot!, null, null));
            }
            else invalid = true;
        }
        catch (Exception)
        {
            // Producer errors cannot escape into the mission callback.
            invalid = true;
        }
        finally
        {
            if (!accepted)
            {
                lock (_gate) { _pending--; _dropped++; }
                ReportDrop(invalid);
            }
        }
        return accepted;
    }

    private void ReportDrop(bool invalid)
    {
        if (invalid)
            AdimiToolsAiLearningLog.QueueThrottled(ref _lastInvalidDropWarning, "Recording dropped because its snapshot was invalid.");
        else
            AdimiToolsAiLearningLog.QueueThrottled(ref _lastUnavailableDropWarning, "Recording dropped because the queue is full or the writer is unavailable.");
    }

    internal AdimiToolsAiLearningCounts GetCounts()
    {
        lock (_gate) return new AdimiToolsAiLearningCounts(_ready, _validRounds, _soloWins, _pending, _errors, _dropped, _pausedForSpace);
    }

    // Used by tests/controlled shutdown. Ordinary enqueue never waits on I/O.
    internal async Task<bool> CompleteAsync(TimeSpan timeout)
    {
        lock (_gate) { _closed = true; }
        _queue.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_writer, _modelWorker).WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            _cancel.Cancel();
            SignalModel();
            try { await Task.WhenAll(_writer, _modelWorker).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (Exception) { }
            return false;
        }
    }

    private async Task WriteLoopAsync()
    {
        var ids = new HashSet<Guid>();
        bool initializationFailed = false;
        try
        {
            if (_publish != null) ClearReceipt();
            while (!_cancel.IsCancellationRequested)
            {
                try
                {
                    RecoverAndLoadArchive();
                    (long valid, long wins, long corrupt, _) = InitializeAndRead(ids, null, null);
                    long committed = new FileInfo(_file).Length;
                    lock (_gate) { _validRounds = valid; _soloWins = wins; _errors += corrupt; _committedLength = committed; _ready = true; }
                    _indexReady.TrySetResult(true);
                    SignalModel();
                    if (initializationFailed) AdimiToolsAiLearningLog.Write("Storage initialization recovered.");
                    if (corrupt != 0) AdimiToolsAiLearningLog.Write("Storage recovery found " + corrupt + " invalid or incomplete record(s); they were excluded from counts.");
                    break;
                }
                catch (Exception exception)
                {
                    lock (_gate) { _ready = false; _errors++; }
                    if (!initializationFailed)
                    {
                        AdimiToolsAiLearningLog.Write("Storage initialization failed (" + exception.GetType().Name + "); retrying every 2 seconds.");
                        initializationFailed = true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), _cancel.Token).ConfigureAwait(false);
                }
            }

            while (!_cancel.IsCancellationRequested)
            {
                if (!_queue.Reader.TryRead(out WriterItem? item))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                    timeout.CancelAfter(_reviewInterval);
                    try
                    {
                        if (!await _queue.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false)) break;
                    }
                    catch (OperationCanceledException) when (!_cancel.IsCancellationRequested) { }
                    continue;
                }
                if (item.SealRequest != null)
                {
                    ProcessSeal(item.SealRequest, ids);
                    continue;
                }
                if (item.Confirmation != null)
                {
                    ProcessConfirmation(item.Confirmation);
                    continue;
                }
                AdimiToolsAiLearningEpisode episode = item.Episode!;
                Guid id = Guid.Parse(episode.Id);
                if (ids.Contains(id))
                {
                    lock (_gate) { _pending--; }
                    continue;
                }
                if (ids.Count >= MaxJournalIdentities)
                {
                    lock (_gate) { _pending--; _dropped++; _errors++; }
                    AdimiToolsAiLearningLog.QueueThrottled(ref _lastUnavailableDropWarning,
                        "Recording dropped: journal identity limit reached; archive journal before restarting.");
                    continue;
                }

                byte[] json;
                try
                {
                    byte[] raw = JsonSerializer.SerializeToUtf8Bytes(episode);
                    json = AdimiToolsAiLearningCodec.Encode(raw, MaxRecordBytes);
                }
                catch (Exception exception)
                {
                    lock (_gate) { _pending--; _dropped++; _errors++; }
                    if (AdimiToolsAiLearningLog.ShouldWarn(ref _lastSerializationWarning))
                        AdimiToolsAiLearningLog.Write("Recording serialization failed (" + exception.GetType().Name + "); the record was dropped.");
                    continue;
                }

                bool writeFailed = false;
                while (!_cancel.IsCancellationRequested)
                {
                    try
                    {
                        // Never remove saved records to make room. When the
                        // reserve is reached, drop this newly captured round;
                        // the next round rechecks the same filesystem.
                        if (!HasRecordingSpace(json.Length + 1L, out string spaceReason))
                        {
                            bool notify;
                            lock (_gate)
                            {
                                notify = !_pausedForSpace;
                                _pausedForSpace = true;
                                _pending--;
                                _dropped++;
                            }
                            if (notify) AdimiToolsAiLearningLog.Write("Recording paused: " + spaceReason + "; saved data was retained.");
                            break;
                        }
                        bool resumed;
                        lock (_gate) { resumed = _pausedForSpace; _pausedForSpace = false; }
                        if (resumed) AdimiToolsAiLearningLog.Write("Recording resumed after available storage recovered.");
                        long committed = Append(json);
                        ids.Add(id);
                        lock (_gate)
                        {
                            if (episode.Valid) _validRounds++;
                            if (episode.Valid && episode.SoloAttributedWin && episode.MatchType == "ai") _soloWins++;
                            _pending--;
                            _committedLength = committed;
                            _ready = true;
                        }
                        SignalModel();
                        if (writeFailed) AdimiToolsAiLearningLog.Write("Storage writing recovered.");
                        break;
                    }
                    catch (Exception exception)
                    {
                        lock (_gate) { _ready = false; _errors++; }
                        if (!writeFailed)
                        {
                            SignalModel();
                            AdimiToolsAiLearningLog.Write("Storage write failed (" + exception.GetType().Name + "); retrying every 2 seconds.");
                            writeFailed = true;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(2), _cancel.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            int pending;
            lock (_gate) { pending = _pending; }
            if (pending > 0) AdimiToolsAiLearningLog.Write("Writer stopped with " + pending + " unsaved record(s).");
        }
        catch (Exception exception)
        {
            lock (_gate) { _ready = false; _errors++; _closed = true; }
            _queue.Writer.TryComplete();
            AdimiToolsAiLearningLog.Write("Writer stopped unexpectedly (" + exception.GetType().Name + "); pending records may be lost and recording is unavailable until restart.");
        }
        finally
        {
            _indexReady.TrySetResult(false);
            lock (_gate)
            {
                _writerFinished = true;
                foreach (var request in _sealRequests) request.TrySetException(new IOException("recording writer is unavailable"));
                _sealRequests.Clear();
                foreach (var request in _confirmationRequests) request.TrySetException(new IOException("recording writer is unavailable"));
                _confirmationRequests.Clear();
            }
            SignalModel();
        }
    }

    private bool PublishPolicy(AdimiToolsAiLearnedPolicy? policy)
    {
        if (_publish == null) return false;
        try { _publish(policy); _lastPublished = policy; _modelHasPublished = true; return true; }
        catch (Exception exception) { FailLearning(exception); return false; }
    }

    private void FailLearning(Exception exception)
    {
        _trainer = null;
        _modelDirty = true;
        _lastPublished = null;
        _modelHasPublished = false;
        ClearReceipt();
        try { _publish?.Invoke(null); } catch (Exception) { }
        AdimiToolsAiLearningLog.Write("Learning model unavailable (" + exception.GetType().Name + "); using original AI behavior.");
    }

    private void SignalModel()
    {
        if (_publish == null) return;
        try { _modelSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private void ReviewFailure(Exception exception)
    {
        _trainer = null;
        _modelDirty = true;
        ClearReceipt();
        if (_lastPublished != null) PublishPolicy(null);
        lock (_gate) { _errors++; }
        if (!_reviewFault)
        {
            AdimiToolsAiLearningLog.Write("Learning review/rebuild failed (" + exception.GetType().Name + "); using original AI and retrying.");
            _reviewFault = true;
        }
    }

    private sealed class ModelSnapshotChangedException : Exception { }

    // This worker never touches a live agent and never modifies the journal.
    // It scans only a prefix whose final newline was successfully flushed by
    // the writer, and catches up by offset before publishing a snapshot.
    private async Task ModelLoopAsync()
    {
        var seen = new HashSet<Guid>();
        long cursor = 0;
        long modelEpoch = -1;
        try
        {
            if (!await _indexReady.Task.WaitAsync(_cancel.Token).ConfigureAwait(false)) return;
            while (!_cancel.IsCancellationRequested)
            {
                bool ready, finished;
                long limit, epoch;
                ArchiveState? archive;
                HashSet<Guid> archiveIds;
                lock (_gate) { ready = _ready; finished = _writerFinished; limit = _committedLength; epoch = _archiveEpoch; archive = _archiveState; archiveIds = _archiveIds; }
                if (!ready)
                {
                    _trainer = null; _modelDirty = true; seen.Clear(); cursor = 0;
                    ClearReceipt();
                    if (_lastPublished != null) PublishPolicy(null);
                    if (finished) break;
                    await _modelSignal.WaitAsync(_reviewInterval, _cancel.Token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    ReviewSnapshot requested = ReadReviews();
                    if (modelEpoch != epoch) { _trainer = null; _modelDirty = true; seen.Clear(); cursor = 0; modelEpoch = epoch; }
                    if (_trainer == null || _modelDirty || requested.Digest != _reviewDigest)
                    {
                        ClearReceipt();
                        if (_lastPublished != null) PublishPolicy(null);
                        _modelHasPublished = false;
                        if (archive == null) _trainer = new AdimiToolsAiLearningTrainer();
                        else
                        {
                            ValidateArchive(archive, verifySegments: false);
                            _trainer = AdimiToolsAiTrainingSampleCodec.Rebuild(ReadArchiveSamples(archive, archiveIds), archive.ExpectedLastSequence, requested.Excluded);
                        }
                        _excluded = requested.Excluded;
                        _reviewDigest = requested.Digest;
                        _modelDirty = false;
                        seen.Clear();
                        seen.UnionWith(archiveIds);
                        cursor = 0;
                    }
                    if (cursor > limit) throw new IOException("Committed journal prefix moved backwards.");
                    if (cursor < limit)
                    {
                        using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (stream.Length < limit) throw new IOException("Committed journal prefix is unavailable.");
                        stream.Position = cursor;
                        long nextCheck = Environment.TickCount64 + (long)_reviewInterval.TotalMilliseconds;
                        (_, _, _, bool trainingOk) = ScanRows(stream, limit - cursor, seen, _trainer, requested.Excluded, () =>
                        {
                            _modelScanHook?.Invoke(); // Optional controlled test synchronization; production passes null.
                            _cancel.Token.ThrowIfCancellationRequested();
                            bool storageReady;
                            lock (_gate) { storageReady = _ready && _archiveEpoch == epoch; }
                            if (!storageReady) throw new ModelSnapshotChangedException();
                            if (Environment.TickCount64 >= nextCheck)
                            {
                                nextCheck = Environment.TickCount64 + (long)_reviewInterval.TotalMilliseconds;
                                if (requested.Digest != ReadReviews().Digest) throw new ModelSnapshotChangedException();
                            }
                        });
                        if (!trainingOk) throw new InvalidOperationException("Could not rebuild training data.");
                        cursor = limit;
                    }
                    if (requested.Digest != ReadReviews().Digest) throw new ModelSnapshotChangedException();
                    lock (_gate) { ready = _ready && _archiveEpoch == epoch; finished = _writerFinished; limit = _committedLength; }
                    if (!ready) throw new ModelSnapshotChangedException();
                    if (cursor != limit) continue;
                    bool published = _modelHasPublished && ReferenceEquals(_lastPublished, _trainer.Current);
                    lock (_gate)
                    {
                        if (!_ready || _archiveEpoch != epoch) throw new ModelSnapshotChangedException();
                        if (!published) published = PublishPolicy(_trainer.Current);
                    }
                    if (!published) throw new InvalidOperationException("Could not publish the rebuilt model.");
                    if (requested.Digest != null)
                    {
                        if (_appliedDigest != requested.Digest || !File.Exists(_receiptFile))
                            TryWriteReceipt(requested.Digest, epoch);
                        if (requested.Digest != ReadReviews().Digest) throw new ModelSnapshotChangedException();
                        lock (_gate) { if (!_ready || _archiveEpoch != epoch) throw new ModelSnapshotChangedException(); }
                    }
                    else ClearReceipt();
                    if (_reviewFault) { AdimiToolsAiLearningLog.Write("Learning review data recovered."); _reviewFault = false; }
                    if (finished) break;
                }
                catch (ModelSnapshotChangedException)
                {
                    _trainer = null; _modelDirty = true; seen.Clear(); cursor = 0;
                    ClearReceipt();
                    if (_lastPublished != null) PublishPolicy(null);
                    continue;
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    ReviewFailure(exception);
                    seen.Clear(); cursor = 0;
                    if (finished) break;
                }
                await _modelSignal.WaitAsync(_reviewInterval, _cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            ClearReceipt();
            if (_lastPublished != null) PublishPolicy(null);
        }
        catch (Exception exception) { ReviewFailure(exception); }
    }

    private readonly record struct ReviewSnapshot(HashSet<Guid> Excluded, string? Digest);

    private ReviewSnapshot ReadReviews()
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(_reviewsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxReviewsBytes) throw new InvalidDataException("Review file is too large.");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
            {
                if (buffer.Length + count > MaxReviewsBytes) throw new InvalidDataException("Review file is too large.");
                buffer.Write(chunk, 0, count);
            }
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException) { return new ReviewSnapshot(new HashSet<Guid>(), null); }
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int versionNumber) || versionNumber != 1
            || !root.TryGetProperty("excluded", out JsonElement excluded) || excluded.ValueKind != JsonValueKind.Array
            || excluded.GetArrayLength() > MaxExcludedEpisodes) throw new InvalidDataException("Invalid review schema.");
        var result = new HashSet<Guid>();
        foreach (JsonElement value in excluded.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(value.GetString(), "N", out Guid id))
                throw new InvalidDataException("Invalid excluded episode ID.");
            result.Add(id);
        }
        return new ReviewSnapshot(result, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private bool ClearReceipt()
    {
        _appliedDigest = null;
        try { File.Delete(_receiptFile); return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception exception)
        {
            if (AdimiToolsAiLearningLog.ShouldWarn(ref _lastReceiptWarning))
                AdimiToolsAiLearningLog.Write("Review receipt could not be removed (" + exception.GetType().Name + "); retrying without affecting recording.");
            return false;
        }
    }

    private void TryWriteReceipt(string digest, long epoch)
    {
        if (!ClearReceipt()) return;
        string temporary = Path.Combine(_directory, "reviews-applied-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(digest + "\n");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            _beforeReceiptReplaceHook?.Invoke(); // Controlled failure injection for isolated tests.
            lock (_gate)
            {
                if (!_ready || _archiveEpoch != epoch) throw new ModelSnapshotChangedException();
                File.Move(temporary, _receiptFile, overwrite: true);
                _appliedDigest = digest;
            }
        }
        catch (Exception exception)
        {
            if (AdimiToolsAiLearningLog.ShouldWarn(ref _lastReceiptWarning))
                AdimiToolsAiLearningLog.Write("Review receipt could not be written (" + exception.GetType().Name + "); retrying without affecting recording.");
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    private (long valid, long wins, long corrupt, bool trainingOk) InitializeAndRead(HashSet<Guid> ids, AdimiToolsAiLearningTrainer? trainer, HashSet<Guid>? excluded)
    {
        Directory.CreateDirectory(_directory);
        ids.Clear();
        ids.UnionWith(_archiveIds);
        using var stream = new FileStream(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        bool recovered = RecoverIncompleteTail(stream);
        stream.Position = 0;
        (long valid, long wins, long corrupt, bool trainingOk) = ScanRows(stream, stream.Length, ids, trainer, excluded, null);
        return (valid + (_archiveState?.ValidRounds ?? 0), wins + (_archiveState?.SoloWins ?? 0), corrupt + (recovered ? 1 : 0), trainingOk);
    }

    private (long valid, long wins, long corrupt, bool trainingOk) ScanRows(Stream stream, long byteCount,
        HashSet<Guid> ids, AdimiToolsAiLearningTrainer? trainer, HashSet<Guid>? excluded, Action? monitor,
        Action<AdimiToolsAiLearningEpisode>? captureCompact = null)
    {
        long valid = 0, wins = 0, corrupt = 0;
        bool trainingOk = true;
        foreach (byte[]? line in ReadBoundedLinesWithin(stream, byteCount))
        {
            monitor?.Invoke();
            if (line == null) { corrupt++; continue; }
            if (line.Length == 0) continue;
            try
            {
                using JsonDocument outer = JsonDocument.Parse(line);
                byte[]? decoded = outer.RootElement.ValueKind == JsonValueKind.Object && outer.RootElement.TryGetProperty("Format", out _)
                    ? AdimiToolsAiLearningCodec.Decode(outer.RootElement, MaxRecordBytes) : null;
                using JsonDocument? decodedDocument = decoded == null ? null : JsonDocument.Parse(decoded);
                JsonElement root = decodedDocument == null ? outer.RootElement : decodedDocument.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("Format", out _)
                    || !root.TryGetProperty("Id", out JsonElement idValue) || idValue.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(idValue.GetString(), out Guid id)
                    || !root.TryGetProperty("Valid", out JsonElement validValue)
                    || validValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !root.TryGetProperty("SoloAttributedWin", out JsonElement winValue)
                    || winValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    corrupt++;
                    continue;
                }
                if (ids.Contains(id)) continue;
                if (ids.Count >= MaxJournalIdentities) throw new InvalidOperationException("Journal identity limit exceeded.");
                ids.Add(id);
                if (validValue.GetBoolean()) valid++;
                bool isAi = !root.TryGetProperty("MatchType", out JsonElement matchType)
                    || (matchType.ValueKind == JsonValueKind.String && matchType.GetString() == "ai");
                if (validValue.GetBoolean() && winValue.GetBoolean() && isAi) wins++;
                if (((trainer != null && trainingOk && (excluded == null || !excluded.Contains(id))) || captureCompact != null)
                    && validValue.GetBoolean() && HasMountedMarkers(root))
                {
                    try
                    {
                        AdimiToolsAiLearningEpisode? episode = decoded == null
                            ? JsonSerializer.Deserialize<AdimiToolsAiLearningEpisode>(line)
                            : JsonSerializer.Deserialize<AdimiToolsAiLearningEpisode>(decoded);
                        if (episode != null)
                        {
                            if (captureCompact != null)
                            {
                                try { captureCompact(episode); }
                                catch (Exception exception) { throw new ArchiveCaptureException(exception); }
                            }
                            if (trainer != null && (excluded == null || !excluded.Contains(id))) trainer.Observe(episode);
                        }
                    }
                    catch (JsonException exception)
                    {
                        if (captureCompact != null) throw new ArchiveCaptureException(exception);
                        /* A malformed episode is not training data. */
                    }
                    catch (ArchiveCaptureException) { throw; }
                    catch (Exception exception)
                    {
                        trainingOk = false;
                        AdimiToolsAiLearningLog.Write("Learning rebuild failed (" + exception.GetType().Name + "); using original AI behavior.");
                    }
                }
            }
            catch (JsonException) { corrupt++; }
            catch (InvalidDataException) { corrupt++; }
            catch (IOException) { corrupt++; }
        }
        return (valid, wins, corrupt, trainingOk);
    }

    // Never let an untrusted line allocate more than the record byte limit.
    // UTF-8 BOM is accepted only at the start, and CRLF matches StreamReader's
    // former behavior. Oversized records are skipped through the next newline.
    private static IEnumerable<byte[]?> ReadBoundedLines(Stream stream) => ReadBoundedLinesWithin(stream, stream.Length - stream.Position);

    private static IEnumerable<byte[]?> ReadBoundedLinesWithin(Stream stream, long byteCount)
    {
        const int extraForBomAndCr = 4;
        byte[] chunk = new byte[64 * 1024];
        using var line = new MemoryStream();
        bool oversized = false;
        bool first = stream.Position == 0;
        int read;
        long remaining = byteCount;
        while (remaining > 0 && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining))) > 0)
        {
            remaining -= read;
            int offset = 0;
            while (offset < read)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', offset, read - offset);
                int end = newline < 0 ? read : newline;
                int length = end - offset;
                if (!oversized)
                {
                    if (line.Length + length > MaxRecordBytes + extraForBomAndCr)
                    {
                        oversized = true;
                        line.SetLength(0);
                    }
                    else
                    {
                        int required = checked((int)line.Length + length);
                        if (required > line.Capacity)
                            line.Capacity = Math.Min(MaxRecordBytes + extraForBomAndCr, Math.Max(required, Math.Max(4096, line.Capacity * 2)));
                        line.Write(chunk, offset, length);
                    }
                }
                if (newline >= 0)
                {
                    byte[]? result = null;
                    if (!oversized)
                    {
                        byte[] bytes = line.ToArray();
                        int start = first && bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                        int endPosition = bytes.Length;
                        if (endPosition > start && bytes[endPosition - 1] == '\r') endPosition--;
                        int recordLength = endPosition - start;
                        if (recordLength <= MaxRecordBytes)
                        {
                            if (start == 0 && recordLength == bytes.Length) result = bytes;
                            else { result = new byte[recordLength]; Buffer.BlockCopy(bytes, start, result, 0, recordLength); }
                        }
                    }
                    yield return result;
                    first = false;
                    oversized = false;
                    line.SetLength(0);
                }
                offset = newline < 0 ? read : newline + 1;
            }
        }
        if (remaining != 0 || line.Length != 0 || oversized) throw new IOException("Committed journal prefix ended unexpectedly.");
    }

    // In schema 2 in-round actors and hits must carry explicit mounted/missile
    // markers; deserialization would otherwise turn omitted fields into false.
    private static bool HasMountedMarkers(JsonElement root)
    {
        if (!root.TryGetProperty("SchemaVersion", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int version) || version != 2
            || !root.TryGetProperty("DurationSeconds", out JsonElement durationValue) || durationValue.ValueKind != JsonValueKind.Number
            || !durationValue.TryGetDouble(out double duration) || !double.IsFinite(duration)
            || !root.TryGetProperty("Frames", out JsonElement frames) || frames.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement frame in frames.EnumerateArray())
        {
            if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("Seconds", out JsonElement timeValue)
                || timeValue.ValueKind != JsonValueKind.Number || !timeValue.TryGetDouble(out double time) || !double.IsFinite(time)) return false;
            if (time > duration) continue;
            if (!frame.TryGetProperty("Actors", out JsonElement actors) || actors.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement actor in actors.EnumerateArray())
            {
                if (actor.ValueKind != JsonValueKind.Object || !actor.TryGetProperty("IsMounted", out JsonElement mounted)
                    || mounted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            }
        }
        if (!root.TryGetProperty("Events", out JsonElement events) || events.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Kind", out JsonElement kind)
                || kind.ValueKind != JsonValueKind.String || !item.TryGetProperty("Seconds", out JsonElement eventTime)
                || eventTime.ValueKind != JsonValueKind.Number || !eventTime.TryGetDouble(out double seconds) || !double.IsFinite(seconds)) return false;
            if (seconds <= duration && kind.GetString() == "hit"
                && (!item.TryGetProperty("IsMissile", out JsonElement missile)
                    || missile.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) return false;
        }
        return true;
    }

    // A newline is the commit marker for a complete JSONL record. Preserve an
    // interrupted tail as a separate file before trimming it from the journal.
    private bool RecoverIncompleteTail(FileStream stream)
    {
        if (stream.Length == 0) return false;
        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == '\n') return false;
        long lastNewline = -1;
        var buffer = new byte[4096];
        for (long end = stream.Length; end > 0 && lastNewline < 0;)
        {
            int length = (int)Math.Min(buffer.Length, end);
            long start = end - length;
            stream.Position = start;
            int read = stream.Read(buffer, 0, length);
            for (int i = read - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n') { lastNewline = start + i; break; }
            }
            end = start;
        }
        long keep = lastNewline + 1;
        string recovery = Path.Combine(_directory, "episodes-incomplete-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".partial");
        stream.Position = keep;
        using (var saved = new FileStream(recovery, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.CopyTo(saved);
            saved.Flush(flushToDisk: true);
        }
        stream.SetLength(keep);
        stream.Flush(flushToDisk: true);
        return true;
    }

    private long Append(byte[] json)
    {
        using var stream = new FileStream(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        long before = stream.Length;
        if (before > 0)
        {
            stream.Position = before - 1;
            if (stream.ReadByte() != '\n')
            {
                if (RecoverIncompleteTail(stream)) { lock (_gate) { _errors++; } }
                before = stream.Length;
            }
        }
        stream.Position = before;
        try
        {
            stream.Write(json, 0, json.Length);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
            return stream.Position;
        }
        catch
        {
            try { stream.SetLength(before); stream.Flush(flushToDisk: true); } catch (Exception) { }
            throw;
        }
    }

    private bool HasRecordingSpace(long recordBytes, out string reason)
    {
        try
        {
            long available = _availableBytes(_directory);
            if (available >= _minimumFreeBytes && available - _minimumFreeBytes >= recordBytes)
            {
                reason = string.Empty;
                return true;
            }
            reason = "available disk space is below the recording reserve";
        }
        catch (Exception exception)
        {
            // When free space cannot be determined, fail closed rather than
            // risk filling the game server disk with more recordings.
            reason = "available disk space could not be checked (" + exception.GetType().Name + ")";
        }
        return false;
    }

    private static bool TryCopy(AdimiToolsAiLearningEpisode source, out AdimiToolsAiLearningEpisode? copy)
    {
        copy = null;
        if (!Guid.TryParse(source.Id, out Guid id)) return false;
        bool truncated = source.Truncated;
        double duration = source.DurationSeconds;
        if (!double.IsFinite(duration) || duration < 0d) { duration = 0d; truncated = true; }
        var result = new AdimiToolsAiLearningEpisode
        {
            SchemaVersion = source.SchemaVersion, Id = id.ToString("N", CultureInfo.InvariantCulture),
            ArenaId = source.ArenaId, ArenaKind = source.ArenaKind, Difficulty = source.Difficulty,
            MatchType = BoundedText(source.MatchType, 32, ref truncated), WinningSide = source.WinningSide,
            StartedUtc = BoundedText(source.StartedUtc, 64, ref truncated), DurationSeconds = duration,
            Outcome = BoundedText(source.Outcome, 64, ref truncated), InitialPlayers = source.InitialPlayers,
            InitialBots = source.InitialBots, Valid = source.Valid, SoloAttributedWin = source.SoloAttributedWin,
        };
        int actors = 0;
        List<AdimiToolsAiLearningFrame>? sourceFrames = source.Frames;
        if (sourceFrames == null) truncated = true;
        else
        {
            for (int i = 0; i < sourceFrames.Count; i++)
            {
                if (i >= MaxFrames) { truncated = true; break; }
                AdimiToolsAiLearningFrame? frame = sourceFrames[i];
                if (frame == null || !double.IsFinite(frame.Seconds) || frame.Seconds < 0d || frame.Actors == null) { truncated = true; continue; }
                var frameCopy = new AdimiToolsAiLearningFrame { Seconds = frame.Seconds };
                foreach (AdimiToolsAiLearningActor? actor in frame.Actors)
                {
                    if (actors >= MaxActorSnapshots) { truncated = true; break; }
                    if (actor == null || !ActorFinite(actor)) { truncated = true; continue; }
                    frameCopy.Actors.Add(CopyActor(actor, ref truncated));
                    actors++;
                }
                result.Frames.Add(frameCopy);
            }
        }

        List<AdimiToolsAiLearningEvent>? sourceEvents = source.Events;
        if (sourceEvents == null) truncated = true;
        else
        {
            for (int i = 0; i < sourceEvents.Count; i++)
            {
                if (i >= MaxEvents) { truncated = true; break; }
                AdimiToolsAiLearningEvent? item = sourceEvents[i];
                if (item == null || !double.IsFinite(item.Seconds) || item.Seconds < 0d || !EventFinite(item)) { truncated = true; continue; }
                result.Events.Add(new AdimiToolsAiLearningEvent
                {
                    Kind = BoundedText(item.Kind, 64, ref truncated), Seconds = item.Seconds, AttackerId = item.AttackerId,
                    VictimId = item.VictimId, IsBlocked = item.IsBlocked, Damage = item.Damage, RemovalState = item.RemovalState,
                    IsMissile = item.IsMissile, ThrustTipHit = item.ThrustTipHit, AttackDirection = item.AttackDirection,
                    AttackerWeaponClass = BoundedText(item.AttackerWeaponClass, 64, ref truncated),
                    IsAlternativeAttack = item.IsAlternativeAttack,
                    StrikeType = item.StrikeType, WeaponSlot = item.WeaponSlot, AttackProgress = item.AttackProgress,
                    ShotX = item.ShotX, ShotY = item.ShotY, ShotZ = item.ShotZ,
                    ShotVelocityX = item.ShotVelocityX, ShotVelocityY = item.ShotVelocityY, ShotVelocityZ = item.ShotVelocityZ,
                });
            }
        }
        result.Truncated = truncated;
        if (truncated) result.Valid = false;
        copy = result;
        return true;
    }

    private static AdimiToolsAiLearningActor CopyActor(AdimiToolsAiLearningActor actor, ref bool truncated)
        => new()
        {
            Id = actor.Id, IsPlayer = actor.IsPlayer, Side = actor.Side, PlayerToken = actor.IsPlayer && IsSafeToken(actor.PlayerToken) ? actor.PlayerToken.ToLowerInvariant() : string.Empty,
            CharacterId = BoundedText(actor.CharacterId, 128, ref truncated), WeaponId = BoundedText(actor.WeaponId, 128, ref truncated),
            WeaponClass = BoundedText(actor.WeaponClass, 64, ref truncated),
            WeaponLength = actor.WeaponLength, IsMounted = actor.IsMounted, MountId = actor.IsMounted ? actor.MountId : -1,
            MountX = actor.IsMounted ? actor.MountX : 0f, MountY = actor.IsMounted ? actor.MountY : 0f, MountZ = actor.IsMounted ? actor.MountZ : 0f,
            MountLookX = actor.IsMounted ? actor.MountLookX : 0f, MountLookY = actor.IsMounted ? actor.MountLookY : 0f, MountLookZ = actor.IsMounted ? actor.MountLookZ : 0f,
            MountVelocityX = actor.IsMounted ? actor.MountVelocityX : 0f, MountVelocityY = actor.IsMounted ? actor.MountVelocityY : 0f,
            MountHealth = actor.IsMounted ? actor.MountHealth : 0f, MountHealthLimit = actor.IsMounted ? actor.MountHealthLimit : 0f,
            X = actor.X, Y = actor.Y, Z = actor.Z, LookX = actor.LookX, LookY = actor.LookY, LookZ = actor.LookZ,
            VelocityX = actor.VelocityX, VelocityY = actor.VelocityY, Health = actor.Health, HealthLimit = actor.HealthLimit,
            MoveInputX = actor.MoveInputX, MoveInputY = actor.MoveInputY,
            MountInputX = actor.IsMounted ? actor.MountInputX : 0f, MountInputY = actor.IsMounted ? actor.MountInputY : 0f,
            ActionType = actor.ActionType, ActionStage = actor.ActionStage, ActionDirection = actor.ActionDirection,
            ActionProgress = actor.ActionProgress,
            MovementFlags = actor.MovementFlags, EventFlags = actor.EventFlags,
        };

    private static bool IsSafeToken(string? value)
        => value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    private static string BoundedText(string? value, int length, ref bool truncated)
    {
        if (value == null) { truncated = true; return string.Empty; }
        if (value.Length <= length) return value;
        truncated = true;
        return value.Substring(0, length);
    }

    private static bool ActorFinite(AdimiToolsAiLearningActor actor)
        => float.IsFinite(actor.X) && float.IsFinite(actor.Y) && float.IsFinite(actor.Z)
            && float.IsFinite(actor.LookX) && float.IsFinite(actor.LookY) && float.IsFinite(actor.LookZ)
            && float.IsFinite(actor.VelocityX) && float.IsFinite(actor.VelocityY)
            && float.IsFinite(actor.MoveInputX) && float.IsFinite(actor.MoveInputY)
            && float.IsFinite(actor.MountInputX) && float.IsFinite(actor.MountInputY)
            && float.IsFinite(actor.Health) && float.IsFinite(actor.HealthLimit) && float.IsFinite(actor.ActionProgress)
            && float.IsFinite(actor.WeaponLength) && float.IsFinite(actor.MountX) && float.IsFinite(actor.MountY) && float.IsFinite(actor.MountZ)
            && float.IsFinite(actor.MountLookX) && float.IsFinite(actor.MountLookY) && float.IsFinite(actor.MountLookZ)
            && float.IsFinite(actor.MountVelocityX) && float.IsFinite(actor.MountVelocityY)
            && float.IsFinite(actor.MountHealth) && float.IsFinite(actor.MountHealthLimit)
            && (!actor.IsMounted || (actor.MountId >= 0 && actor.MountHealth > 0f && actor.MountHealthLimit >= actor.MountHealth));

    private static bool EventFinite(AdimiToolsAiLearningEvent item)
        => float.IsFinite(item.Damage) && float.IsFinite(item.AttackProgress)
            && float.IsFinite(item.ShotX) && float.IsFinite(item.ShotY) && float.IsFinite(item.ShotZ)
            && float.IsFinite(item.ShotVelocityX) && float.IsFinite(item.ShotVelocityY) && float.IsFinite(item.ShotVelocityZ);
}
