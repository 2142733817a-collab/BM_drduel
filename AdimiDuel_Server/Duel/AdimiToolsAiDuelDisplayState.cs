namespace AdimiDuel_Server.Duel;

// UI metadata only. Neither this state machine nor its transport owns an Agent.
internal sealed class AdimiToolsAiDuelDisplayState<TPeer, TDisplay>
    where TPeer : notnull
    where TDisplay : class
{
    private const int MaximumEntries = 32;
    private const int MaximumRoundWins = 10;
    private readonly IAdimiToolsAiDuelDisplayTransport<TPeer, TDisplay> _transport;
    private readonly Dictionary<int, Entry> _entries = new();

    private sealed class Recipient
    {
        public bool MayExist { get; set; }
        public bool KnownPeer { get; set; }
        public bool Ready { get; set; }
        public bool MayHaveRow { get; set; }
        public bool Dirty { get; set; }
        public object? ConnectionIdentity { get; set; }
        public int PlayerWins { get; set; }
        public int AiWins { get; set; }
    }

    private sealed class Entry
    {
        public TPeer Owner { get; init; } = default!;
        public TDisplay Display { get; init; } = default!;
        public bool Closing { get; set; }
        public int PlayerWins { get; set; }
        public int AiWins { get; set; }
        public Dictionary<TPeer, Recipient> Recipients { get; } = new();
    }

    public AdimiToolsAiDuelDisplayState(IAdimiToolsAiDuelDisplayTransport<TPeer, TDisplay> transport)
    {
        _transport = transport;
    }

    public int Count => _entries.Count;

    public static string FormatName(string troopName, int totalCount, int difficulty)
    {
        string name = string.Join(" ", troopName.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (name.Length > 80)
        {
            name = name.Substring(0, 80);
        }

        string prefix = difficulty switch
        {
            1 => "AI简单",
            2 => "AI一般",
            3 => "AI困难",
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
        };
        return $"[{prefix}] {name} ×{totalCount}";
    }

    public bool Begin(int arenaId, TPeer owner, string troopName, int totalCount, int difficulty)
    {
        if (totalCount < 1 || totalCount > 20 || difficulty < 1 || difficulty > 3 || string.IsNullOrWhiteSpace(troopName))
        {
            return false;
        }

        if (_entries.ContainsKey(arenaId))
        {
            End(arenaId);
            if (_entries.ContainsKey(arenaId))
            {
                return false;
            }
        }

        if (_entries.Count >= MaximumEntries)
        {
            _transport.Log("AI duel display capacity reached; combat is unaffected.");
            return false;
        }

        try
        {
            _entries.Add(arenaId, new Entry
            {
                Owner = owner,
                Display = _transport.Allocate(FormatName(troopName, totalCount, difficulty)),
            });
            return true;
        }
        catch (Exception exception)
        {
            _transport.Log($"AI duel display allocation skipped: {exception.Message}");
            return false;
        }
    }

    public void RoundEnded(int arenaId, bool playerWon)
    {
        if (!_entries.TryGetValue(arenaId, out Entry? entry) || entry.Closing)
        {
            return;
        }

        if (playerWon)
        {
            entry.PlayerWins = Math.Min(MaximumRoundWins, entry.PlayerWins + 1);
        }
        else
        {
            entry.AiWins = Math.Min(MaximumRoundWins, entry.AiWins + 1);
        }
    }

    public void End(int arenaId)
    {
        if (_entries.TryGetValue(arenaId, out Entry? entry))
        {
            entry.Closing = true;
            Refresh();
        }
    }

    public void LateJoin(TPeer observer)
    {
        // A re-synchronized client may have lost its old mission peer objects.
        // Recreate only our private display identity, never any human identity.
        foreach (Entry entry in _entries.Values)
        {
            if (entry.Recipients.TryGetValue(observer, out Recipient? recipient))
            {
                object connection = _transport.ConnectionIdentity(observer);
                if (!ReferenceEquals(recipient.ConnectionIdentity, connection))
                {
                    // A reconnect replaces PlayerConnectionInfo and its client
                    // may not know our identity. Same-connection resync retains
                    // full component cleanup instead of leaking the old peer.
                    recipient.Ready = false;
                    recipient.MayHaveRow = false;
                    recipient.KnownPeer = false;
                }

                recipient.ConnectionIdentity = connection;
                recipient.Dirty = true;
            }
        }
    }

    public void RemovePeer(TPeer peer)
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Recipients.Remove(peer);
            if (EqualityComparer<TPeer>.Default.Equals(entry.Owner, peer))
            {
                entry.Closing = true;
            }
        }
    }

    public bool Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Closing = true;
        }

        Refresh();
        return _entries.Count == 0;
    }

    public void Refresh()
    {
        HashSet<TPeer> connected = new(_transport.ConnectedObservers());
        foreach (KeyValuePair<int, Entry> pair in _entries.ToArray())
        {
            Entry entry = pair.Value;
            foreach (TPeer peer in entry.Recipients.Keys.Where(p => !connected.Contains(p)).ToArray())
            {
                entry.Recipients.Remove(peer);
            }

            if (entry.Closing)
            {
                foreach (KeyValuePair<TPeer, Recipient> recipient in entry.Recipients.ToArray())
                {
                    if (TryDelete(recipient.Key, entry, recipient.Value))
                    {
                        entry.Recipients.Remove(recipient.Key);
                    }
                }

                if (entry.Recipients.Count == 0)
                {
                    // Delete events must be queued (or the client disconnected)
                    // before the reserved native index can be reused.
                    _entries.Remove(pair.Key);
                    try
                    {
                        _transport.Release(entry.Display);
                    }
                    catch (Exception exception)
                    {
                        // Do not repeat a possibly completed native release.
                        _transport.Log($"AI display index release failed; no repeated release: {exception.Message}");
                    }
                }

                continue;
            }

            foreach (TPeer observer in connected)
            {
                if (EqualityComparer<TPeer>.Default.Equals(observer, entry.Owner) || !_transport.CanObserve(observer, entry.Owner))
                {
                    continue;
                }

                if (!entry.Recipients.TryGetValue(observer, out Recipient? recipient))
                {
                    recipient = new() { ConnectionIdentity = _transport.ConnectionIdentity(observer) };
                    entry.Recipients.Add(observer, recipient);
                }

                try
                {
                    if (recipient.Dirty && !TryDelete(observer, entry, recipient))
                    {
                        continue;
                    }

                    if (!recipient.Ready)
                    {
                        recipient.MayExist = true;
                        _transport.Create(observer, entry.Display, () => recipient.KnownPeer = true);
                        recipient.Ready = true;
                        recipient.MayHaveRow = true;
                        _transport.Start(observer, entry.Owner, entry.Display);
                    }

                    while (recipient.PlayerWins < entry.PlayerWins)
                    {
                        _transport.Round(observer, entry.Owner, entry.Display, playerWon: true);
                        recipient.PlayerWins++;
                    }

                    while (recipient.AiWins < entry.AiWins)
                    {
                        _transport.Round(observer, entry.Owner, entry.Display, playerWon: false);
                        recipient.AiWins++;
                    }
                }
                catch (Exception exception)
                {
                    // A row rebuild resets incremental native scores before
                    // replay, even when a failed flush may have been delivered.
                    recipient.Dirty = true;
                    _transport.Log($"AI display observer update deferred: {exception.Message}");
                }
            }
        }
    }

    private bool TryDelete(TPeer observer, Entry entry, Recipient recipient)
    {
        try
        {
            if (recipient.Ready && recipient.MayHaveRow)
            {
                _transport.End(observer, entry.Owner, entry.Display);
                recipient.MayHaveRow = false;
            }

            if (recipient.MayExist)
            {
                _transport.Delete(observer, entry.Display, recipient.KnownPeer, () => recipient.KnownPeer = false);
            }

            recipient.MayExist = false;
            recipient.KnownPeer = false;
            recipient.Ready = false;
            recipient.Dirty = false;
            recipient.PlayerWins = 0;
            recipient.AiWins = 0;
            return true;
        }
        catch (Exception exception)
        {
            recipient.Dirty = true;
            _transport.Log($"AI display observer cleanup deferred: {exception.Message}");
            return false;
        }
    }
}

internal interface IAdimiToolsAiDuelDisplayTransport<TPeer, TDisplay>
    where TPeer : notnull
    where TDisplay : class
{
    IReadOnlyCollection<TPeer> ConnectedObservers();
    bool CanObserve(TPeer observer, TPeer owner);
    object ConnectionIdentity(TPeer observer);
    TDisplay Allocate(string name);
    void Release(TDisplay display);
    void Create(TPeer observer, TDisplay display, Action identityKnown);
    void Start(TPeer observer, TPeer owner, TDisplay display);
    void Round(TPeer observer, TPeer owner, TDisplay display, bool playerWon);
    void End(TPeer observer, TPeer owner, TDisplay display);
    void Delete(TPeer observer, TDisplay display, bool knownPeer, Action beforeIndexDelete);
    void Log(string message);
}
