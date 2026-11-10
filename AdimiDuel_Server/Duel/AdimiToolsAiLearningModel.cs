using System.Numerics;
using System.Text.Json;

namespace AdimiDuel_Server.Duel;

// Published instances are immutable and never retain source episodes.
internal sealed class AdimiToolsAiLearnedPolicy
{
    private readonly double _upEnd;
    private readonly double _downEnd;
    private readonly double _leftEnd;

    internal AdimiToolsAiLearnedPolicy(long samples, double upEnd, double downEnd, double leftEnd)
    {
        Samples = samples;
        _upEnd = upEnd;
        _downEnd = downEnd;
        _leftEnd = leftEnd;
    }

    public long Samples { get; }

    // Matches Agent.UsageDirection: 0 up, 1 down, 2 left, 3 right.
    public int PickDirection(double randomUnit)
    {
        if (!double.IsFinite(randomUnit) || randomUnit < 0d || randomUnit >= 1d)
        {
            return -1;
        }

        if (randomUnit < _upEnd)
        {
            return 0;
        }

        if (randomUnit < _downEnd)
        {
            return 1;
        }

        if (randomUnit < _leftEnd)
        {
            return 2;
        }

        return 3;
    }
}

// Persisted per-round inputs to the existing trainer, not a learned model or
// a replay. Version 1 is immutable as a wire schema: future training changes
// need a new version and an explicit migration/rebuild policy.
internal sealed class AdimiToolsAiTrainingSample
{
    public int Version { get; set; }
    public long Sequence { get; set; }
    public string EpisodeId { get; set; } = "";
    public AdimiToolsAiTrainingAi? Ai { get; set; }
    public AdimiToolsAiTrainingPvp? Pvp { get; set; }
}

internal sealed class AdimiToolsAiTrainingAi
{
    public int Players { get; set; }
    public int Bots { get; set; }
    public double AveragePlayerHealth { get; set; }
    public double AverageBotHealth { get; set; }
    public double Duration { get; set; }
    public double TotalTeamDamage { get; set; }
    public List<AdimiToolsAiTrainingContributor> Contributors { get; set; } = new();
}

internal sealed class AdimiToolsAiTrainingPvp
{
    public string Mode { get; set; } = "";
    public int Winners { get; set; }
    public int Losers { get; set; }
    public int WinnerHealthBand { get; set; }
    public int LoserHealthBand { get; set; }
    public ulong WinnerWeapons { get; set; }
    public ulong LoserWeapons { get; set; }
    public double LoserInitialHealth { get; set; }
    public double WinnerDamage { get; set; }
    public int MutualWindows { get; set; }
    public int TotalWindows { get; set; }
    public int MovingMutualWindows { get; set; }
    public List<AdimiToolsAiTrainingContributor> Contributors { get; set; } = new();
}

internal sealed class AdimiToolsAiTrainingContributor
{
    public double InitialHealth { get; set; }
    public double DamageTaken { get; set; }
    public double DamageDealt { get; set; }
    public int DirectionTotal { get; set; }
    public int Up { get; set; }
    public int Down { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }
}

internal static class AdimiToolsAiTrainingSampleCodec
{
    internal const int Version = 1;
    internal const int MaxSampleBytes = 128 * 1024;
    internal const int MaxSamples = 1000000;
    private const double MaxDamage = 4096d * float.MaxValue;

    internal static byte[] Encode(AdimiToolsAiTrainingSample sample)
    {
        Validate(sample);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(sample);
        Require(bytes.Length <= MaxSampleBytes, "compact sample exceeds size limit");
        return bytes;
    }

    internal static AdimiToolsAiTrainingSample Decode(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length > 0 && bytes.Length <= MaxSampleBytes, "invalid compact sample size");
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        ExactObject(root, "Version", "Sequence", "EpisodeId", "Ai", "Pvp");
        JsonElement ai = root.GetProperty("Ai"), pvp = root.GetProperty("Pvp");
        if (ai.ValueKind != JsonValueKind.Null)
        {
            ExactObject(ai, "Players", "Bots", "AveragePlayerHealth", "AverageBotHealth", "Duration", "TotalTeamDamage", "Contributors");
            ContributorShape(ai.GetProperty("Contributors"));
        }

        if (pvp.ValueKind != JsonValueKind.Null)
        {
            ExactObject(pvp, "Mode", "Winners", "Losers", "WinnerHealthBand", "LoserHealthBand", "WinnerWeapons", "LoserWeapons",
                "LoserInitialHealth", "WinnerDamage", "MutualWindows", "TotalWindows", "MovingMutualWindows", "Contributors");
            ContributorShape(pvp.GetProperty("Contributors"));
        }

        AdimiToolsAiTrainingSample? sample = JsonSerializer.Deserialize<AdimiToolsAiTrainingSample>(bytes);
        Validate(sample);
        return sample!;
    }

    // The storage owner supplies a separately committed expected tail, so an
    // otherwise valid prefix with a missing final sample cannot be published.
    internal static AdimiToolsAiLearningTrainer Rebuild(IEnumerable<AdimiToolsAiTrainingSample> samples,
        long expectedLastSequence, IReadOnlySet<Guid>? excluded = null)
    {
        if (samples == null)
        {
            throw new ArgumentNullException(nameof(samples));
        }

        Require(expectedLastSequence >= 0 && expectedLastSequence <= MaxSamples, "invalid compact expected sequence");
        Require(excluded == null || excluded.Count <= 25000, "too many excluded episodes");
        var trainer = new AdimiToolsAiLearningTrainer();
        var seen = new HashSet<Guid>();
        long previous = 0;
        foreach (AdimiToolsAiTrainingSample sample in samples)
        {
            Validate(sample);
            Require(sample.Sequence == previous + 1, "compact sample sequence has a gap or is out of order");
            previous = sample.Sequence;
            Guid id = Guid.ParseExact(sample.EpisodeId, "N");
            Require(seen.Count < MaxSamples && seen.Add(id), "duplicate ID or compact sample limit reached");
            if (excluded == null || !excluded.Contains(id))
            {
                trainer.ObserveCompact(sample);
            }
        }

        Require(previous == expectedLastSequence, "compact sample tail does not match committed sequence");
        return trainer;
    }

    internal static void Validate(AdimiToolsAiTrainingSample? sample)
    {
        Require(sample != null, "missing compact sample");
        Require(sample!.Version == Version && sample.Sequence > 0 && sample.Sequence <= MaxSamples && sample.EpisodeId != null
            && Guid.TryParseExact(sample.EpisodeId, "N", out Guid id) && id.ToString("N") == sample.EpisodeId,
            "invalid compact sample header");
        Require((sample.Ai == null) != (sample.Pvp == null), "compact sample must have one mode");
        if (sample.Ai is AdimiToolsAiTrainingAi ai)
        {
            Require(ai.Players is >= 1 and <= 255 && ai.Bots is >= 1 and <= 255 && ai.Players + ai.Bots <= 256
                && HealthyAverage(ai.AveragePlayerHealth) && HealthyAverage(ai.AverageBotHealth)
                && double.IsFinite(ai.Duration) && ai.Duration is >= .001d and <= 600d
                && PositiveDamage(ai.TotalTeamDamage), "invalid compact AI sample");
            ValidateContributors(ai.Contributors, ai.Players, ai.TotalTeamDamage);
        }
        else
        {
            AdimiToolsAiTrainingPvp pvp = sample.Pvp!;
            Require(pvp.Mode is "pvp" or "team_pvp" && pvp.Winners is >= 1 and <= 255 && pvp.Losers is >= 1 and <= 255
                && pvp.Winners + pvp.Losers <= 256 && (pvp.Mode != "pvp" || (pvp.Winners == 1 && pvp.Losers == 1))
                && pvp.WinnerHealthBand is >= 1 and <= 400 && pvp.LoserHealthBand is >= 1 and <= 400
                && double.IsFinite(pvp.LoserInitialHealth) && pvp.LoserInitialHealth > 0d
                && pvp.LoserInitialHealth <= pvp.Losers * 10000d && PositiveDamage(pvp.WinnerDamage)
                && pvp.MutualWindows is >= 1 and <= 60 && pvp.TotalWindows is >= 1 and <= 60
                && pvp.MovingMutualWindows >= 0 && pvp.MovingMutualWindows <= pvp.MutualWindows,
                "invalid compact PvP sample");
            ValidateContributors(pvp.Contributors, pvp.Winners, pvp.WinnerDamage);
        }
    }

    private static bool HealthyAverage(double value) => double.IsFinite(value) && value > 0d && value <= 10000d;
    private static bool PositiveDamage(double value) => double.IsFinite(value) && value > 0d && value <= MaxDamage;

    private static void ValidateContributors(List<AdimiToolsAiTrainingContributor>? contributors, int maximum, double totalDamage)
    {
        Require(contributors != null && contributors.Count >= 1 && contributors.Count <= maximum, "invalid compact contributors");
        double attributed = 0d;
        foreach (AdimiToolsAiTrainingContributor contributor in contributors!)
        {
            Require(contributor != null && HealthyAverage(contributor.InitialHealth)
                && double.IsFinite(contributor.DamageTaken) && contributor.DamageTaken >= 0d && contributor.DamageTaken <= MaxDamage
                && PositiveDamage(contributor.DamageDealt) && contributor.DamageDealt <= totalDamage * (1d + 1e-10d)
                && contributor.Up is >= 0 and <= 8192 && contributor.Down is >= 0 and <= 8192
                && contributor.Left is >= 0 and <= 8192 && contributor.Right is >= 0 and <= 8192
                && contributor.DirectionTotal is >= 1 and <= 8192
                && contributor.Up + contributor.Down + contributor.Left + contributor.Right == contributor.DirectionTotal,
                "invalid compact contributor");
            attributed += contributor!.DamageDealt;
        }

        Require(double.IsFinite(attributed) && attributed <= totalDamage * (1d + 1e-10d), "invalid compact attributed damage");
    }

    private static void ContributorShape(JsonElement array)
    {
        Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= 255, "invalid compact contributor array");
        foreach (JsonElement contributor in array.EnumerateArray())
        {
            ExactObject(contributor, "InitialHealth", "DamageTaken", "DamageDealt", "DirectionTotal", "Up", "Down", "Left", "Right");
        }
    }

    private static void ExactObject(JsonElement element, params string[] names)
    {
        Require(element.ValueKind == JsonValueKind.Object, "invalid compact object");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            Require(remaining.Remove(property.Name), "unknown or duplicate compact field");
        }

        Require(remaining.Count == 0, "missing compact field");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

internal static class AdimiToolsAiLearningModel
{
    private static AdimiToolsAiLearnedPolicy? _current;
    public static AdimiToolsAiLearnedPolicy? Current => Volatile.Read(ref _current);
    internal static void Publish(AdimiToolsAiLearnedPolicy? policy) => Volatile.Write(ref _current, policy);
}

// The writer is the sole owner. A trainer is discarded if rebuilding fails;
// it holds only bounded per-group statistics and four cumulative weights.
internal sealed class AdimiToolsAiLearningTrainer
{
    private const int ReadyMelee = 19;
    private const int AttackReady = 0;
    private const int AttackQuickReady = 1;
    private const string GreatSword = "TwoHandedSword";
    private readonly Dictionary<(int Players, int Bots, int PlayerHealthBand, int BotHealthBand), Baseline> _groups = new();
    private readonly Dictionary<PvpGroup, PvpBaseline> _pvpGroups = new();
    private readonly double[] _weights = new double[4];
    private long _samples;

    internal AdimiToolsAiLearnedPolicy? Current { get; private set; }
    internal long Samples => _samples;

    internal bool Observe(AdimiToolsAiLearningEpisode episode)
    {
        if (episode == null)
        {
            return false;
        }

        if (episode.MatchType is "pvp" or "team_pvp")
        {
            return ObservePvp(episode);
        }

        if (!TryExtract(episode, out Sample sample))
        {
            return false;
        }

        ApplyAi(sample);
        return true;
    }

    private void ApplyAi(Sample sample)
    {
        var key = (sample.Players, sample.Bots, HealthBand(sample.AveragePlayerHealth), HealthBand(sample.AverageBotHealth));
        _groups.TryGetValue(key, out Baseline baseline);
        double referenceDuration = baseline.Count == 0 ? sample.Duration : baseline.MeanDuration;
        double speedWeight = Math.Clamp(referenceDuration / sample.Duration, .5d, 2d);
        foreach (Track player in sample.Contributors)
        {
            double damageFraction = Math.Clamp(player.DamageTaken / player.InitialHealth, 0d, 1d);
            double participation = player.DamageDealt / sample.TotalTeamDamage;
            double weight = speedWeight * Math.Clamp(1d - .5d * damageFraction, .5d, 1d) * participation;
            for (int i = 0; i < 4; i++)
            {
                _weights[i] += weight * player.Directions[i] / player.DirectionTotal;
            }
        }

        long groupCount = baseline.Count + 1;
        _groups[key] = new Baseline(groupCount, baseline.MeanDuration + (sample.Duration - baseline.MeanDuration) / groupCount);
        CountSample();
    }

    private bool ObservePvp(AdimiToolsAiLearningEpisode episode)
    {
        if (!TryExtractPvp(episode, out PvpSample sample))
        {
            return false;
        }

        ApplyPvp(sample);
        return true;
    }

    private void ApplyPvp(PvpSample sample)
    {
        _pvpGroups.TryGetValue(sample.Group, out PvpBaseline baseline);
        double activeWindows = Math.Min(sample.MutualWindows, 8);
        double reference = baseline.Count == 0 ? activeWindows : baseline.MeanActiveWindows;
        double relative = Math.Clamp(activeWindows / reference, .75d, 1.25d);
        double activity = .75d + .25d * activeWindows / 8d;
        double density = Math.Clamp(2d * sample.MutualWindows / sample.TotalWindows, .5d, 1d);
        double impact = .5d + .5d * Math.Clamp(sample.WinnerDamage / sample.LoserInitialHealth, 0d, 1d);
        double movement = .95d + .05d * sample.MovingMutualWindows / sample.MutualWindows;
        double roundWeight = Math.Clamp(relative * activity * density * impact * movement, .25d, 1.25d);
        foreach (PvpTrack winner in sample.Contributors)
        {
            double damageFraction = Math.Clamp(winner.DamageTaken / winner.InitialHealth, 0d, 1d);
            double participation = winner.DamageDealt / sample.WinnerDamage;
            double weight = roundWeight * Math.Clamp(1d - .5d * damageFraction, .5d, 1d) * participation;
            for (int i = 0; i < 4; i++)
            {
                _weights[i] += weight * winner.Directions[i] / winner.DirectionTotal;
            }
        }

        long count = baseline.Count + 1;
        _pvpGroups[sample.Group] = new PvpBaseline(count, baseline.MeanActiveWindows + (activeWindows - baseline.MeanActiveWindows) / count);
        CountSample();
    }

    // The storage owner assigns a contiguous sequence (1, 2, ...) to eligible
    // samples in journal order, and must invoke this for excluded episodes too. The
    // caller must first perform the same schema-marker checks and ID
    // deduplication used for training the original journal.
    internal static bool TryMakeCompactSample(AdimiToolsAiLearningEpisode episode, long sequence,
        out AdimiToolsAiTrainingSample? compact)
    {
        compact = null;
        if (sequence < 1 || sequence > AdimiToolsAiTrainingSampleCodec.MaxSamples)
        {
            throw new InvalidDataException("compact sample sequence is outside the supported limit");
        }

        if (episode == null || !Guid.TryParse(episode.Id, out Guid id))
        {
            throw new InvalidDataException("compact sample requires a valid episode ID");
        }

        if (episode.MatchType is "pvp" or "team_pvp")
        {
            if (!TryExtractPvp(episode, out PvpSample sample))
            {
                return false;
            }

            compact = new AdimiToolsAiTrainingSample
            {
                Version = AdimiToolsAiTrainingSampleCodec.Version,
                Sequence = sequence,
                EpisodeId = id.ToString("N"),
                Pvp = new AdimiToolsAiTrainingPvp
                {
                    Mode = sample.Group.Mode,
                    Winners = sample.Group.Winners,
                    Losers = sample.Group.Losers,
                    WinnerHealthBand = sample.Group.WinnerHealthBand,
                    LoserHealthBand = sample.Group.LoserHealthBand,
                    WinnerWeapons = sample.Group.WinnerWeapons,
                    LoserWeapons = sample.Group.LoserWeapons,
                    LoserInitialHealth = sample.LoserInitialHealth,
                    WinnerDamage = sample.WinnerDamage,
                    MutualWindows = sample.MutualWindows,
                    TotalWindows = sample.TotalWindows,
                    MovingMutualWindows = sample.MovingMutualWindows,
                    Contributors = sample.Contributors.Select(track => CompactContributor(track.InitialHealth,
                        track.DamageTaken, track.DamageDealt, track.DirectionTotal, track.Directions)).ToList(),
                },
            };
        }
        else
        {
            if (!TryExtract(episode, out Sample sample))
            {
                return false;
            }

            compact = new AdimiToolsAiTrainingSample
            {
                Version = AdimiToolsAiTrainingSampleCodec.Version,
                Sequence = sequence,
                EpisodeId = id.ToString("N"),
                Ai = new AdimiToolsAiTrainingAi
                {
                    Players = sample.Players,
                    Bots = sample.Bots,
                    AveragePlayerHealth = sample.AveragePlayerHealth,
                    AverageBotHealth = sample.AverageBotHealth,
                    Duration = sample.Duration,
                    TotalTeamDamage = sample.TotalTeamDamage,
                    Contributors = sample.Contributors.Select(track => CompactContributor(track.InitialHealth,
                        track.DamageTaken, track.DamageDealt, track.DirectionTotal, track.Directions)).ToList(),
                },
            };
        }

        AdimiToolsAiTrainingSampleCodec.Validate(compact);
        return true;
    }

    private static AdimiToolsAiTrainingContributor CompactContributor(double initial, double taken, double dealt, int total, int[] directions)
        => new()
        {
            InitialHealth = initial,
            DamageTaken = taken,
            DamageDealt = dealt,
            DirectionTotal = total,
            Up = directions[0],
            Down = directions[1],
            Left = directions[2],
            Right = directions[3],
        };

    // An invalid persisted sample is a hard failure, not an ignorable record:
    // the storage owner can then fall back rather than publish partial data.
    internal void ObserveCompact(AdimiToolsAiTrainingSample compact)
    {
        AdimiToolsAiTrainingSampleCodec.Validate(compact);
        if (compact.Ai is AdimiToolsAiTrainingAi ai)
        {
            var contributors = new List<Track>(ai.Contributors.Count);
            foreach (AdimiToolsAiTrainingContributor contributor in ai.Contributors)
            {
                var track = new Track(true, contributor.InitialHealth, true)
                {
                    DamageTaken = contributor.DamageTaken,
                    DamageDealt = contributor.DamageDealt,
                    DirectionTotal = contributor.DirectionTotal,
                };
                CopyDirections(contributor, track.Directions);
                contributors.Add(track);
            }

            ApplyAi(new Sample(ai.Players, ai.Bots, ai.AveragePlayerHealth, ai.AverageBotHealth,
                ai.Duration, ai.TotalTeamDamage, contributors));
        }
        else
        {
            AdimiToolsAiTrainingPvp pvp = compact.Pvp!;
            var contributors = new List<PvpTrack>(pvp.Contributors.Count);
            foreach (AdimiToolsAiTrainingContributor contributor in pvp.Contributors)
            {
                var track = new PvpTrack(0, contributor.InitialHealth, "")
                {
                    DamageTaken = contributor.DamageTaken,
                    DamageDealt = contributor.DamageDealt,
                    DirectionTotal = contributor.DirectionTotal,
                };
                CopyDirections(contributor, track.Directions);
                contributors.Add(track);
            }

            var group = new PvpGroup(pvp.Mode, pvp.Winners, pvp.Losers, pvp.WinnerHealthBand,
                pvp.LoserHealthBand, pvp.WinnerWeapons, pvp.LoserWeapons);
            ApplyPvp(new PvpSample(group, pvp.LoserInitialHealth, pvp.WinnerDamage,
                pvp.MutualWindows, pvp.TotalWindows, pvp.MovingMutualWindows, contributors));
        }
    }

    private static void CopyDirections(AdimiToolsAiTrainingContributor source, int[] target)
    {
        target[0] = source.Up;
        target[1] = source.Down;
        target[2] = source.Left;
        target[3] = source.Right;
    }

    private void CountSample()
    {
        _samples++;
        if (_samples >= 20 && (_samples - 20) % 5 == 0)
        {
            Current = BuildPolicy();
        }
    }

    private AdimiToolsAiLearnedPolicy BuildPolicy()
    {
        double total = _weights[0] + _weights[1] + _weights[2] + _weights[3];
        if (!double.IsFinite(total) || total <= 0d)
        {
            throw new InvalidOperationException("Invalid learned direction weights.");
        }

        // Half uniform prior bounds every direction to 12.5%..62.5%.
        double up = .125d + .5d * _weights[0] / total;
        double down = .125d + .5d * _weights[1] / total;
        double left = .125d + .5d * _weights[2] / total;
        return new AdimiToolsAiLearnedPolicy(_samples, up, up + down, up + down + left);
    }

    private static int HealthBand(double health) => Math.Max(1, (int)Math.Round(health / 25d, MidpointRounding.AwayFromZero));

    private static bool TryExtract(AdimiToolsAiLearningEpisode episode, out Sample sample)
    {
        sample = default;
        if (episode == null || episode.SchemaVersion != 2 || episode.MatchType != "ai" || !episode.Valid || episode.Truncated
            || episode.Outcome != "player_win" || (episode.WinningSide != -1 && episode.WinningSide != 0)
            || episode.InitialPlayers < 1 || episode.InitialBots < 1 || episode.InitialPlayers + episode.InitialBots > 256
            || (episode.InitialPlayers == 1 && episode.InitialBots == 1 && !episode.SoloAttributedWin)
            || episode.ArenaKind != 0 || episode.Difficulty != 3
            || !double.IsFinite(episode.DurationSeconds) || episode.DurationSeconds < .001d || episode.DurationSeconds > 600d
            || episode.Frames == null || episode.Frames.Count < 2 || episode.Frames.Count > 8192
            || episode.Events == null || episode.Events.Count > 4096)
        {
            return false;
        }

        AdimiToolsAiLearningFrame? first = episode.Frames[0];
        if (first?.Actors == null || first.Actors.Count != episode.InitialPlayers + episode.InitialBots)
        {
            return false;
        }

        var roster = new Dictionary<int, Track>();
        double initialPlayersHealth = 0d, initialBotsHealth = 0d;
        int players = 0, bots = 0;
        foreach (AdimiToolsAiLearningActor? actor in first.Actors)
        {
            if (actor == null || actor.IsMounted || !ValidHealth(actor) || roster.ContainsKey(actor.Id)
                || (actor.Side != -1 && actor.Side != (actor.IsPlayer ? 0 : 1)))
            {
                return false;
            }

            if (!actor.IsPlayer && actor.WeaponClass != GreatSword)
            {
                return false;
            }

            var track = new Track(actor.IsPlayer, actor.Health, actor.WeaponClass == GreatSword);
            roster.Add(actor.Id, track);
            if (actor.IsPlayer)
            {
                players++;
                initialPlayersHealth += actor.Health;
            }
            else
            {
                bots++;
                initialBotsHealth += actor.Health;
            }
        }

        if (players != episode.InitialPlayers || bots != episode.InitialBots)
        {
            return false;
        }

        // A round's true death timestamps may occur after the last 50 ms frame.
        // Build them from events first so disappearing dead actors are allowed.
        double lastBotRemoval = -1d;
        foreach (AdimiToolsAiLearningEvent? item in episode.Events)
        {
            if (item == null || !double.IsFinite(item.Seconds) || item.Seconds < 0d || !float.IsFinite(item.Damage) || item.Damage < 0f)
            {
                return false;
            }

            if (item.Kind == "mount" || item.Kind == "dismount")
            {
                return false;
            }

            if (item.Seconds > episode.DurationSeconds || item.Kind != "removal")
            {
                continue;
            }

            if (!roster.TryGetValue(item.VictimId, out Track? victim) || !roster.TryGetValue(item.AttackerId, out Track? attacker)
                || victim.IsPlayer == attacker.IsPlayer || victim.RemovedAt.HasValue)
            {
                return false;
            }

            victim.RemovedAt = item.Seconds;
            if (!victim.IsPlayer)
            {
                lastBotRemoval = Math.Max(lastBotRemoval, item.Seconds);
            }
        }

        if (Math.Abs(lastBotRemoval - episode.DurationSeconds) > .001d
            || roster.Values.Any(t => !t.IsPlayer && !t.RemovedAt.HasValue)
            || !roster.Values.Any(t => t.IsPlayer && !t.RemovedAt.HasValue))
        {
            return false;
        }

        int inRoundFrames = 0;
        int snapshotCount = 0;
        double previousSeconds = -1d;
        foreach (AdimiToolsAiLearningFrame? frame in episode.Frames)
        {
            if (frame == null || !double.IsFinite(frame.Seconds) || frame.Seconds < 0d || frame.Seconds < previousSeconds)
            {
                return false;
            }

            previousSeconds = frame.Seconds;
            // Removal callback and round finalization may be separated by a tick.
            // A later frame can contain just the surviving player and must not
            // change the in-round health-loss estimate or attack distribution.
            if (frame.Seconds > episode.DurationSeconds)
            {
                continue;
            }

            if (frame.Actors == null || frame.Actors.Count == 0 || (snapshotCount += frame.Actors.Count) > 16384)
            {
                return false;
            }

            inRoundFrames++;
            var seen = new HashSet<int>();
            foreach (AdimiToolsAiLearningActor? actor in frame.Actors)
            {
                if (actor == null || !roster.TryGetValue(actor.Id, out Track? track) || !seen.Add(actor.Id)
                    || actor.IsPlayer != track.IsPlayer || actor.IsMounted || !ValidHealth(actor)
                    || (actor.Side != -1 && actor.Side != (actor.IsPlayer ? 0 : 1))
                    || !float.IsFinite(actor.ActionProgress) || !float.IsFinite(actor.WeaponLength)
                    || (track.RemovedAt.HasValue && frame.Seconds > track.RemovedAt.Value + .001d))
                {
                    return false;
                }

                if (!actor.IsPlayer && actor.WeaponClass != GreatSword)
                {
                    return false;
                }

                if (!actor.IsPlayer)
                {
                    continue;
                }

                track.LastHealth = actor.Health;
                if (actor.WeaponClass != GreatSword)
                {
                    track.Eligible = false;
                }

                int direction = actor.ActionDirection;
                bool ready = actor.WeaponClass == GreatSword && actor.ActionType == ReadyMelee
                    && (actor.ActionStage == AttackReady || actor.ActionStage == AttackQuickReady)
                    && direction >= 0 && direction < 4;
                if (!ready)
                {
                    track.PreviousDirection = -1;
                }
                else
                {
                    if (direction != track.PreviousDirection)
                    {
                        track.Directions[direction]++;
                        track.DirectionTotal++;
                    }

                    track.PreviousDirection = direction;
                }
            }

            foreach (var pair in roster)
            {
                if ((!pair.Value.RemovedAt.HasValue || frame.Seconds < pair.Value.RemovedAt.Value - .001d)
                    && !seen.Contains(pair.Key))
                {
                    return false;
                }
            }
        }

        if (inRoundFrames < 2)
        {
            return false;
        }

        double teamDamage = 0d;
        foreach (AdimiToolsAiLearningEvent? item in episode.Events)
        {
            if (item == null || item.Kind != "hit" || item.IsBlocked || item.Seconds > episode.DurationSeconds)
            {
                continue;
            }

            if (!roster.TryGetValue(item.VictimId, out Track? victim) || !roster.TryGetValue(item.AttackerId, out Track? attacker))
            {
                return false;
            }

            if (victim.RemovedAt.HasValue && item.Seconds > victim.RemovedAt.Value + .001d)
            {
                continue;
            }

            if (victim.IsPlayer)
            {
                victim.HitDamageTaken += item.Damage;
            }
            else if (attacker.IsPlayer)
            {
                teamDamage += item.Damage;
                // Projectile damage remains in the full-team denominator but
                // cannot qualify a player's greatsword directions for training.
                if (!item.IsMissile && item.AttackerWeaponClass == GreatSword && item.IsAlternativeAttack == false)
                {
                    attacker.DamageDealt += item.Damage;
                }
            }
        }

        if (!double.IsFinite(teamDamage) || teamDamage <= 0d)
        {
            return false;
        }

        var contributors = new List<Track>();
        foreach (Track player in roster.Values.Where(t => t.IsPlayer))
        {
            if (!double.IsFinite(player.DamageDealt) || !double.IsFinite(player.HitDamageTaken))
            {
                return false;
            }

            player.DamageTaken = Math.Max(player.HitDamageTaken, Math.Max(0d, player.InitialHealth - player.LastHealth));
            if (player.RemovedAt.HasValue)
            {
                player.DamageTaken = Math.Max(player.DamageTaken, player.InitialHealth);
            }

            if (player.Eligible && player.DamageDealt > 0d && player.DirectionTotal > 0)
            {
                contributors.Add(player);
            }
        }

        if (contributors.Count == 0)
        {
            return false;
        }

        sample = new Sample(players, bots, initialPlayersHealth / players, initialBotsHealth / bots, episode.DurationSeconds, teamDamage, contributors);
        return true;
    }

    private static bool TryExtractPvp(AdimiToolsAiLearningEpisode episode, out PvpSample sample)
    {
        sample = default;
        if (episode.SchemaVersion != 2 || episode.MatchType is not ("pvp" or "team_pvp")
            || !episode.Valid || episode.Truncated || episode.SoloAttributedWin || episode.InitialBots != 0
            || episode.InitialPlayers < 2 || episode.InitialPlayers > 256 || episode.WinningSide is not (0 or 1)
            || episode.Outcome != (episode.WinningSide == 0 ? "side_a_win" : "side_b_win")
            || !double.IsFinite(episode.DurationSeconds) || episode.DurationSeconds < .001d || episode.DurationSeconds > 600d
            || episode.Frames == null || episode.Frames.Count < 2 || episode.Frames.Count > 8192
            || episode.Events == null || episode.Events.Count > 4096)
        {
            return false;
        }

        AdimiToolsAiLearningFrame? first = episode.Frames[0];
        if (first?.Actors == null || first.Actors.Count != episode.InitialPlayers)
        {
            return false;
        }

        var roster = new Dictionary<int, PvpTrack>();
        int winnerCount = 0, loserCount = 0;
        double winnerHealth = 0d, loserHealth = 0d;
        foreach (AdimiToolsAiLearningActor? actor in first.Actors)
        {
            if (actor == null || !actor.IsPlayer || actor.Side is not (0 or 1) || actor.IsMounted || !ValidHealth(actor)
                || actor.WeaponClass != GreatSword || string.IsNullOrEmpty(actor.WeaponId) || actor.WeaponId.Length > 128
                || !float.IsFinite(actor.WeaponLength) || roster.ContainsKey(actor.Id))
            {
                return false;
            }

            var track = new PvpTrack(actor.Side, actor.Health, actor.WeaponId);
            roster.Add(actor.Id, track);
            if (actor.Side == episode.WinningSide)
            {
                winnerCount++;
                winnerHealth += actor.Health;
            }
            else
            {
                loserCount++;
                loserHealth += actor.Health;
            }
        }

        if (winnerCount == 0 || loserCount == 0 || (episode.MatchType == "pvp" && (winnerCount != 1 || loserCount != 1)))
        {
            return false;
        }

        double lastLoserRemoval = -1d;
        foreach (AdimiToolsAiLearningEvent? item in episode.Events)
        {
            if (item == null || !double.IsFinite(item.Seconds) || item.Seconds < 0d || !float.IsFinite(item.Damage) || item.Damage < 0f)
            {
                return false;
            }

            if (item.Kind is "mount" or "dismount" or "mount_hit" or "mount_removal" or "shot")
            {
                return false;
            }

            if (item.Seconds > episode.DurationSeconds || item.Kind != "removal")
            {
                continue;
            }

            if (!roster.TryGetValue(item.VictimId, out PvpTrack? victim) || !roster.TryGetValue(item.AttackerId, out PvpTrack? attacker)
                || victim.Side == attacker.Side || victim.RemovedAt.HasValue)
            {
                return false;
            }

            victim.RemovedAt = item.Seconds;
            if (victim.Side != episode.WinningSide)
            {
                lastLoserRemoval = Math.Max(lastLoserRemoval, item.Seconds);
            }
        }

        if (Math.Abs(lastLoserRemoval - episode.DurationSeconds) > .001d
            || roster.Values.Any(t => t.Side != episode.WinningSide && !t.RemovedAt.HasValue)
            || !roster.Values.Any(t => t.Side == episode.WinningSide && !t.RemovedAt.HasValue))
        {
            return false;
        }

        int validFrames = 0, snapshots = 0;
        double previousSeconds = -1d;
        ulong movementWindows = 0;
        foreach (AdimiToolsAiLearningFrame? frame in episode.Frames)
        {
            if (frame == null || !double.IsFinite(frame.Seconds) || frame.Seconds < 0d || frame.Seconds < previousSeconds)
            {
                return false;
            }

            previousSeconds = frame.Seconds;
            if (frame.Seconds > episode.DurationSeconds)
            {
                continue;
            }

            if (frame.Actors == null || frame.Actors.Count == 0 || (snapshots += frame.Actors.Count) > 16384)
            {
                return false;
            }

            validFrames++;
            var seen = new HashSet<int>();
            foreach (AdimiToolsAiLearningActor? actor in frame.Actors)
            {
                if (actor == null || !roster.TryGetValue(actor.Id, out PvpTrack? track) || !seen.Add(actor.Id)
                    || !actor.IsPlayer || actor.Side != track.Side || actor.IsMounted || !ValidHealth(actor)
                    || actor.WeaponClass != GreatSword || actor.WeaponId != track.WeaponId
                    || !float.IsFinite(actor.WeaponLength) || !float.IsFinite(actor.ActionProgress)
                    || !float.IsFinite(actor.MoveInputX) || !float.IsFinite(actor.MoveInputY)
                    || !float.IsFinite(actor.VelocityX) || !float.IsFinite(actor.VelocityY)
                    || (track.RemovedAt.HasValue && frame.Seconds > track.RemovedAt.Value + .001d))
                {
                    return false;
                }

                track.LastHealth = actor.Health;
                if (track.Side != episode.WinningSide)
                {
                    continue;
                }

                if (actor.MoveInputX * actor.MoveInputX + actor.MoveInputY * actor.MoveInputY >= .01f
                    && actor.VelocityX * actor.VelocityX + actor.VelocityY * actor.VelocityY >= .01f)
                {
                    movementWindows |= WindowMask(frame.Seconds);
                }

                int direction = actor.ActionDirection;
                bool ready = actor.ActionType == ReadyMelee && (actor.ActionStage == AttackReady || actor.ActionStage == AttackQuickReady)
                    && direction >= 0 && direction < 4;
                if (!ready)
                {
                    track.PreviousDirection = -1;
                }
                else
                {
                    if (direction != track.PreviousDirection)
                    {
                        track.Directions[direction]++;
                        track.DirectionTotal++;
                    }

                    track.PreviousDirection = direction;
                }
            }

            foreach (var pair in roster)
            {
                if ((!pair.Value.RemovedAt.HasValue || frame.Seconds < pair.Value.RemovedAt.Value - .001d) && !seen.Contains(pair.Key))
                {
                    return false;
                }
            }
        }

        if (validFrames < 2)
        {
            return false;
        }

        ulong winnerWindows = 0, loserWindows = 0;
        double winnerDamage = 0d;
        foreach (AdimiToolsAiLearningEvent? item in episode.Events)
        {
            if (item == null || item.Kind != "hit" || item.Seconds > episode.DurationSeconds)
            {
                continue;
            }

            if (item.IsMissile || item.AttackerWeaponClass != GreatSword || item.IsAlternativeAttack != false
                || !roster.TryGetValue(item.VictimId, out PvpTrack? victim)
                || !roster.TryGetValue(item.AttackerId, out PvpTrack? attacker))
            {
                return false;
            }

            if ((victim.RemovedAt.HasValue && item.Seconds > victim.RemovedAt.Value + .001d)
                || (attacker.RemovedAt.HasValue && item.Seconds > attacker.RemovedAt.Value + .001d))
            {
                continue;
            }

            if (victim.Side == attacker.Side)
            {
                if (!item.IsBlocked)
                {
                    victim.FriendlyDamage += item.Damage;
                }

                continue;
            }

            if (item.IsBlocked || item.Damage > 0f)
            {
                if (attacker.Side == episode.WinningSide)
                {
                    winnerWindows |= WindowMask(item.Seconds);
                }
                else
                {
                    loserWindows |= WindowMask(item.Seconds);
                }
            }

            if (item.IsBlocked || item.Damage <= 0f)
            {
                continue;
            }

            victim.EnemyDamageTaken += item.Damage;
            if (attacker.Side == episode.WinningSide)
            {
                attacker.DamageDealt += item.Damage;
                winnerDamage += item.Damage;
            }
        }

        ulong mutual = winnerWindows & loserWindows;
        int mutualWindows = BitOperations.PopCount(mutual);
        if (mutualWindows == 0 || !double.IsFinite(winnerDamage) || winnerDamage <= 0d
            || roster.Values.Any(t => t.Side != episode.WinningSide && t.EnemyDamageTaken <= 0d))
        {
            return false;
        }

        var contributors = new List<PvpTrack>();
        foreach (PvpTrack winner in roster.Values.Where(t => t.Side == episode.WinningSide))
        {
            if (!double.IsFinite(winner.DamageDealt) || !double.IsFinite(winner.EnemyDamageTaken) || !double.IsFinite(winner.FriendlyDamage))
            {
                return false;
            }

            double healthEvidence = Math.Max(0d, winner.InitialHealth - winner.LastHealth - winner.FriendlyDamage);
            winner.DamageTaken = Math.Max(winner.EnemyDamageTaken, healthEvidence);
            if (winner.RemovedAt.HasValue)
            {
                winner.DamageTaken = Math.Max(winner.DamageTaken, winner.InitialHealth);
            }

            if (winner.DamageDealt > 0d && winner.DirectionTotal > 0)
            {
                contributors.Add(winner);
            }
        }

        if (contributors.Count == 0)
        {
            return false;
        }

        ulong winnersWeapons = WeaponFingerprint(roster.Values.Where(t => t.Side == episode.WinningSide));
        ulong losersWeapons = WeaponFingerprint(roster.Values.Where(t => t.Side != episode.WinningSide));
        var group = new PvpGroup(episode.MatchType, winnerCount, loserCount, HealthBand(winnerHealth / winnerCount),
            HealthBand(loserHealth / loserCount), winnersWeapons, losersWeapons);
        int totalWindows = Math.Max(1, (int)Math.Ceiling(episode.DurationSeconds / 10d));
        sample = new PvpSample(group, loserHealth, winnerDamage, mutualWindows, totalWindows,
            BitOperations.PopCount(mutual & movementWindows), contributors);
        return true;
    }

    private static ulong WindowMask(double seconds) => 1UL << Math.Clamp((int)(seconds / 10d), 0, 59);

    private static ulong WeaponFingerprint(IEnumerable<PvpTrack> tracks)
    {
        const ulong prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        foreach (string id in tracks.Select(t => t.WeaponId).OrderBy(x => x, StringComparer.Ordinal))
        {
            foreach (char character in id)
            {
                hash ^= character;
                hash *= prime;
            }

            hash ^= 0xffff;
            hash *= prime;
        }

        return hash;
    }

    private static bool ValidHealth(AdimiToolsAiLearningActor actor)
        => float.IsFinite(actor.Health) && float.IsFinite(actor.HealthLimit) && actor.Health > 0f
            && actor.Health <= 10000f && actor.HealthLimit >= actor.Health && actor.HealthLimit <= 10000f;

    private readonly record struct Baseline(long Count, double MeanDuration);
    private sealed class Track
    {
        internal Track(bool isPlayer, double initialHealth, bool eligible)
        {
            IsPlayer = isPlayer;
            InitialHealth = initialHealth;
            LastHealth = initialHealth;
            Eligible = eligible;
        }

        internal bool IsPlayer { get; }
        internal double InitialHealth { get; }
        internal double LastHealth { get; set; }
        internal bool Eligible { get; set; }
        internal double? RemovedAt { get; set; }
        internal double HitDamageTaken { get; set; }
        internal double DamageTaken { get; set; }
        internal double DamageDealt { get; set; }
        internal int[] Directions { get; } = new int[4];
        internal int DirectionTotal { get; set; }
        internal int PreviousDirection { get; set; } = -1;
    }

    private readonly record struct Sample(int Players, int Bots, double AveragePlayerHealth, double AverageBotHealth,
        double Duration, double TotalTeamDamage, List<Track> Contributors);

    private sealed class PvpTrack
    {
        internal PvpTrack(int side, double initialHealth, string weaponId)
        {
            Side = side;
            InitialHealth = initialHealth;
            LastHealth = initialHealth;
            WeaponId = weaponId;
        }

        internal int Side { get; }
        internal string WeaponId { get; }
        internal double InitialHealth { get; }
        internal double LastHealth { get; set; }
        internal double? RemovedAt { get; set; }
        internal double EnemyDamageTaken { get; set; }
        internal double FriendlyDamage { get; set; }
        internal double DamageTaken { get; set; }
        internal double DamageDealt { get; set; }
        internal int[] Directions { get; } = new int[4];
        internal int DirectionTotal { get; set; }
        internal int PreviousDirection { get; set; } = -1;
    }

    private readonly record struct PvpGroup(string Mode, int Winners, int Losers, int WinnerHealthBand, int LoserHealthBand,
        ulong WinnerWeapons, ulong LoserWeapons);
    private readonly record struct PvpBaseline(long Count, double MeanActiveWindows);
    private readonly record struct PvpSample(PvpGroup Group, double LoserInitialHealth, double WinnerDamage,
        int MutualWindows, int TotalWindows, int MovingMutualWindows, List<PvpTrack> Contributors);
}
