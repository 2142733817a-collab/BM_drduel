using AdimiToolsShared;
using TaleWorlds.Core;

namespace AdimiDuel_Server.Duel;

// Native clients regenerate bot appearances from the character's own range,
// equipment hair cover, gender and BodyPropertiesSeed in CreateAgent.
internal sealed class AdimiToolsAiArenaAppearance
{
    private const int SeedCount = 2001;
    private const int MaxAttempts = 32;
    private const int HistoryLimit = 512;
    private readonly Func<int> _nextSeed;
    private readonly HashSet<StaticBodyProperties> _reserved = new();
    private readonly HashSet<StaticBodyProperties> _recent = new();
    private readonly Queue<StaticBodyProperties> _recentOrder = new();
    private bool _firstFemale;
    private bool _warnedAboutDuplicates;
    private int _nextIndex;

    public AdimiToolsAiArenaAppearance()
        : this(() => Random.Shared.Next(SeedCount))
    {
    }

    internal AdimiToolsAiArenaAppearance(Func<int> nextSeed)
    {
        _nextSeed = nextSeed;
    }

    public void BeginBatch(bool firstFemale)
    {
        _reserved.Clear();
        _firstFemale = firstFemale;
        _warnedAboutDuplicates = false;
        _nextIndex = 0;
    }

    public void Reserve(BodyProperties body) => _reserved.Add(body.StaticProperties);

    public (BodyProperties body, bool isFemale, int seed) Create(BasicCharacterObject character, Equipment equipment)
    {
        bool isFemale = (_nextIndex++ & 1) == 0 ? _firstFemale : !_firstFemale;
        if (character.BodyPropertyRange == null)
        {
            throw new InvalidOperationException("训练 AI 缺少对应兵种的人物外观数据。");
        }

        BodyProperties last = default;
        BodyProperties candidate = default;
        int lastSeed = 0;
        int candidateSeed = 0;
        bool hasCandidate = false;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            int seed = _nextSeed();
            if (seed < 0 || seed >= SeedCount)
            {
                throw new InvalidOperationException("训练 AI 外观随机种子超出有效范围。");
            }

            last = BodyProperties.GetRandomBodyProperties(character.Race, isFemale,
                character.GetBodyPropertiesMin(), character.GetBodyPropertiesMax(), (int)equipment.HairCoverType,
                seed, character.BodyPropertyRange.HairTags, character.BodyPropertyRange.BeardTags,
                character.BodyPropertyRange.TattooTags);
            lastSeed = seed;
            StaticBodyProperties key = last.StaticProperties;
            if (!_reserved.Contains(key) && !_recent.Contains(key))
            {
                Remember(key);
                return (last, isFemale, seed);
            }

            // If a restricted face range exhausts recent history, prefer a
            // repeated historical face over a duplicate already in this batch.
            if (!_reserved.Contains(key))
            {
                candidate = last;
                candidateSeed = seed;
                hasCandidate = true;
            }
        }

        if (!hasCandidate)
        {
            // A custom character may expose only one fixed face key. Its own
            // range is required so unmodified clients regenerate the same face.
            if (!_warnedAboutDuplicates)
            {
                AdimiToolsConsoleLog.Log("AI appearance range could not produce a distinct face for this batch.");
                _warnedAboutDuplicates = true;
            }

            candidate = last;
            candidateSeed = lastSeed;
        }

        Remember(candidate.StaticProperties);
        return (candidate, isFemale, candidateSeed);
    }

    private void Remember(StaticBodyProperties key)
    {
        _reserved.Add(key);
        if (_recent.Add(key))
        {
            _recentOrder.Enqueue(key);
            if (_recentOrder.Count > HistoryLimit)
            {
                _recent.Remove(_recentOrder.Dequeue());
            }
        }
    }
}
