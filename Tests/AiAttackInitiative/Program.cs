using System.Diagnostics;
using AdimiDuel_Server.Duel;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Action = TaleWorlds.MountAndBlade.Agent.ActionCodeType;
using Direction = TaleWorlds.MountAndBlade.Agent.UsageDirection;
using Event = TaleWorlds.MountAndBlade.Agent.EventControlFlag;
using Move = TaleWorlds.MountAndBlade.Agent.MovementControlFlag;
using Stage = TaleWorlds.MountAndBlade.Agent.ActionStage;

internal static class Program
{
    private const Move Travel = Move.Forward | Move.TurnRight;
    private static int _checks;

    private sealed class Scenario
    {
        private long _clock;
        public readonly AdimiToolsAiArenaAttackInitiative Initiative;
        public Scenario() => Initiative = new AdimiToolsAiArenaAttackInitiative(new Agent(), () => Interlocked.Read(ref _clock));

        public void Time(double seconds) => Interlocked.Exchange(ref _clock, (long)(seconds * Stopwatch.Frequency));
        public void Observe(double time, Action own = Action.ReadyMelee, Stage stage = Stage.AttackReady,
            Direction direction = Direction.AttackUp, int difficulty = 3, bool targetReady = true, bool danger = false,
            bool busy = false, bool eligible = true, int target = 17, Move allowed = Move.AttackMask,
            Move preferred = Move.AttackLeft)
        {
            Time(time);
            Initiative.Observe(time, target, eligible, targetReady, danger, busy, difficulty, own, stage, direction, allowed, preferred);
        }

        public (Event events, Move flags, Vec2 vector) Input(double time, Move flags, Event events = Event.Run)
        {
            Time(time);
            Vec2 vector = new(.25f, -.75f);
            Initiative.OnAIInputSet(ref events, ref flags, ref vector);
            return (events, flags, vector);
        }

        public void ReadyUntil(double end, int difficulty = 3, Action own = Action.ReadyMelee,
            Stage stage = Stage.AttackReady, Direction direction = Direction.AttackUp, Move allowed = Move.AttackMask,
            Move preferred = Move.AttackLeft, int target = 17)
        {
            for (double time = 0; time < end; time += .02d)
                Observe(time, own, stage, direction, difficulty, target: target, allowed: allowed, preferred: preferred);
            Observe(end, own, stage, direction, difficulty, target: target, allowed: allowed, preferred: preferred);
        }
    }

    private static void Main()
    {
        ReactionAndProtection();
        NeutralAndOrdinaryDefense();
        SafetyAndPriority();
        FreshnessAndDeadlines();
        DirectionAndCooldown();
        VisibilityCache();
        Concurrency();
        Console.WriteLine($"AiAttackInitiative: {_checks} checks passed (deterministic stubs; Native, game and multiplayer unverified).");
    }

    private static void ReactionAndProtection()
    {
        foreach (var (difficulty, delay) in new[] { (1, .45d), (2, .25d), (3, .12d) })
        {
            var s = new Scenario();
            s.Observe(0d, difficulty: difficulty);
            True(s.Initiative.HasPendingOrControl, "ready reaction protects against premature defense");
            var held = s.Input(0d, Travel | Move.DefendUp);
            Equal(Travel | Move.AttackUp, held.flags, "ordinary guard replaced by current own ready direction");
            Equal(Event.Run, held.events, "protection preserves event");
            Equal(.25f, held.vector.x, "protection preserves input X");
            Equal(-.75f, held.vector.y, "protection preserves input Y");
            Equal(Travel, s.Input(.001d, Travel).flags, "no guard input leaves native natural release alone");
            s.ReadyUntil(delay - .01d, difficulty);
            Equal(Travel | Move.AttackUp, s.Input(delay - .01d, Travel | Move.DefendUp).flags,
                "before difficulty delay protects ready without committing");
            s.Observe(delay + .001d, difficulty: difficulty);
            Equal(Travel, s.Input(delay + .001d, Travel | Move.AttackUp | Move.DefendUp).flags,
                "after difficulty delay requests release of observed ready");
            True(s.Initiative.HasPendingOrControl, "release control advertised while snapshot fresh");
            s.Observe(delay + .021d, own: Action.ReleaseMelee, stage: Stage.AttackRelease, difficulty: difficulty);
            Equal(Travel | Move.DefendUp, s.Input(delay + .021d, Travel | Move.DefendUp).flags,
                "actual own release ends forced input");
            False(s.Initiative.HasPendingOrControl, "release stops control");
        }

        var passive = new Scenario();
        passive.Observe(0d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        False(passive.Initiative.HasPendingOrControl, "neutral reaction does not claim input");
        Equal(Travel | Move.DefendUp, passive.Input(0d, Travel | Move.DefendUp).flags,
            "neutral reaction does not suppress native guard before delay");

        var quick = new Scenario();
        quick.Observe(0d, stage: Stage.AttackQuickReady);
        Equal(Travel | Move.AttackUp, quick.Input(0d, Travel | Move.DefendUp).flags,
            "quick ready receives the same bounded guard protection");
    }

    private static void NeutralAndOrdinaryDefense()
    {
        foreach (Action action in new[] { Action.Idle, Action.Guard, Action.Other, Action.DefendUp2h, Action.DefendShield })
        {
            Stage stage = action is Action.DefendUp2h or Action.DefendShield ? Stage.Defend : Stage.None;
            var s = new Scenario();
            s.ReadyUntil(.121d, own: action, stage: stage, direction: Direction.None);
            True(s.Initiative.HasPendingOrControl, "starting stage advertises ownership");
            var first = s.Input(.121d, Travel | Move.DefendUp);
            Equal(Travel | Move.AttackLeft, first.flags, $"{action} starts preferred legal attack");
            Equal(Event.Run, first.events, "start preserves events");
            Equal(.25f, first.vector.x, "start preserves vector");
            Equal(Travel | Move.AttackLeft, s.Input(.122d, Travel).flags, "one held direction is maintained, not repeated events");
            s.Observe(.14d, own: Action.ReadyMelee, stage: Stage.AttackReady, direction: Direction.AttackLeft);
            Equal(Travel, s.Input(.14d, Travel | Move.AttackLeft).flags, "own ready transitions to bounded release");
            s.Observe(.16d, own: Action.ReleaseMelee, stage: Stage.AttackRelease, direction: Direction.AttackLeft);
            Equal(Travel | Move.DefendUp, s.Input(.16d, Travel | Move.DefendUp).flags, "own release restores native control");
        }

        var parry = new Scenario();
        parry.ReadyUntil(.2d, own: Action.DefendUp2h, stage: Stage.DefendParry, direction: Direction.None);
        Equal(Travel | Move.DefendUp, parry.Input(.2d, Travel | Move.DefendUp).flags, "parry stun never starts attack");
    }

    private static void SafetyAndPriority()
    {
        foreach (string why in new[] { "danger", "busy", "ineligible", "target not ready", "target changed", "bad difficulty", "bad direction" })
        {
            var s = new Scenario();
            s.Observe(0d);
            switch (why)
            {
                case "danger": s.Observe(.01d, danger: true); break;
                case "busy": s.Observe(.01d, busy: true); break;
                case "ineligible": s.Observe(.01d, eligible: false); break;
                case "target not ready": s.Observe(.01d, targetReady: false); break;
                case "target changed": s.Observe(.01d, target: -1); break;
                case "bad difficulty": s.Observe(.01d, difficulty: 0); break;
                case "bad direction": s.Observe(.01d, direction: Direction.None); break;
            }
            Equal(Travel | Move.DefendUp, s.Input(.01d, Travel | Move.DefendUp).flags, $"{why} leaves defense intact");
            False(s.Initiative.HasPendingOrControl, $"{why} disarms");
        }

        foreach (Event ev in new[] { Event.Kick, Event.Jump, Event.Mount, Event.Dismount, Event.Rear,
                     Event.Wield0, Event.Wield1, Event.Wield2, Event.Wield3, Event.Sheath0, Event.Sheath1,
                     Event.ToggleAlternativeWeapon })
        {
            var s = new Scenario();
            s.Observe(0d);
            var output = s.Input(0d, Travel | Move.DefendUp, ev | Event.Run);
            Equal(Travel | Move.DefendUp, output.flags, $"{ev} retains native input");
            Equal(ev | Event.Run, output.events, $"{ev} stays unchanged");
            False(s.Initiative.HasPendingOrControl, $"{ev} cancels initiative");
        }

        var invalid = new Scenario();
        invalid.Observe(0d);
        Equal(Travel | Move.DefendUp | Move.Action, invalid.Input(0d, Travel | Move.DefendUp | Move.Action).flags,
            "native action flag is respected");
        invalid.Observe(.02d);
        Equal(Travel | Move.DefendUp | Move.AttackLeft | Move.AttackRight,
            invalid.Input(.02d, Travel | Move.DefendUp | Move.AttackLeft | Move.AttackRight).flags,
            "ambiguous multi-direction native input is preserved");

        var actualThreat = new Scenario();
        actualThreat.Observe(0d);
        actualThreat.Observe(.02d, danger: true); // next main-thread sample sees release or kick
        Equal(Travel | Move.DefendUp, actualThreat.Input(.02d, Travel | Move.DefendUp).flags,
            "a sampled actual release immediately restores native defense");
        var blocked = new Scenario();
        blocked.Observe(0d);
        blocked.Initiative.Suspend(); // actual hit or removal callback, before the next sample
        Equal(Travel | Move.DefendUp, blocked.Input(.01d, Travel | Move.DefendUp).flags,
            "asynchronous suspend revokes even a still-fresh input");

        var order = new Scenario();
        order.Observe(0d);
        Move initiative = order.Input(0d, Travel | Move.DefendUp).flags;
        Move laterGuard = (initiative & ~Move.AttackMask) | Move.DefendRight;
        Equal(Travel | Move.DefendRight, laterGuard, "a later guard callback overrides initiative");
    }

    private static void FreshnessAndDeadlines()
    {
        var stale = new Scenario();
        stale.Observe(0d);
        Equal(Travel | Move.DefendUp, stale.Input(.031d, Travel | Move.DefendUp).flags,
            "expired 30ms snapshot cannot suppress native guard");
        False(stale.Initiative.HasPendingOrControl, "expired snapshot has no input ownership");
        stale.Observe(.05d);
        Equal(Travel | Move.AttackUp, stale.Input(.05d, Travel | Move.DefendUp).flags,
            "safe low-rate resampling refreshes control without restarting reaction");
        stale.Observe(.13d);
        Equal(Travel, stale.Input(.13d, Travel | Move.AttackUp).flags,
            "50-80ms sampling still reaches hard reaction delay");

        var race = new Scenario();
        race.Observe(0d);
        Equal(Travel | Move.AttackUp, race.Input(.029d, Travel | Move.DefendUp).flags,
            "documented race: unsampled new threat can be overridden within snapshot TTL");
        Equal(Travel | Move.DefendUp, race.Input(.031d, Travel | Move.DefendUp).flags,
            "same unsampled threat is no longer overridden after TTL");

        var noCallback = new Scenario();
        noCallback.ReadyUntil(.121d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        noCallback.Observe(.20d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        noCallback.Observe(.28d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        noCallback.Observe(.36d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        noCallback.Observe(.37d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        Equal(Travel | Move.DefendUp, noCallback.Input(.37d, Travel | Move.DefendUp).flags,
            "attempt expires even if native never called it");
        noCallback.Observe(.39d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        False(noCallback.Initiative.HasPendingOrControl, "held target cannot loop after attempt expires");

        var discontinuity = new Scenario();
        discontinuity.Observe(0d);
        discontinuity.Observe(.20d);
        Equal(Travel | Move.AttackUp, discontinuity.Input(.20d, Travel | Move.DefendUp).flags,
            "long sampling gap restarts reaction instead of instant commit");
        discontinuity.Observe(.19d);
        Equal(Travel | Move.DefendUp, discontinuity.Input(.19d, Travel | Move.DefendUp).flags,
            "mission time rollback fails open");
    }

    private static void DirectionAndCooldown()
    {
        var unsupported = new Scenario();
        unsupported.Observe(0d, allowed: Move.AttackLeft);
        Equal(Travel | Move.DefendUp, unsupported.Input(0d, Travel | Move.DefendUp).flags,
            "own ready must be a permitted direction");
        var invalidMask = new Scenario();
        invalidMask.Observe(0d, allowed: Move.AttackUp | Move.Forward);
        Equal(Travel | Move.DefendUp, invalidMask.Input(0d, Travel | Move.DefendUp).flags,
            "invalid allowed mask fails open");

        var fallback = new Scenario();
        fallback.ReadyUntil(.121d, own: Action.Idle, stage: Stage.None, direction: Direction.None,
            allowed: Move.AttackRight, preferred: Move.AttackLeft);
        Equal(Travel | Move.AttackRight, fallback.Input(.121d, Travel | Move.DefendUp).flags,
            "unsupported preferred direction falls back to allowed direction");
        fallback.Observe(.14d, own: Action.ReadyMelee, stage: Stage.AttackReady, direction: Direction.AttackLeft,
            allowed: Move.AttackRight, preferred: Move.AttackLeft);
        Equal(Travel | Move.AttackLeft | Move.DefendUp, fallback.Input(.14d, Travel | Move.AttackLeft | Move.DefendUp).flags,
            "mismatched own ready is never forcibly released");

        var once = new Scenario();
        once.ReadyUntil(.121d);
        once.Input(.121d, Travel | Move.AttackUp);
        once.Initiative.Clear();
        for (double time = .14d; time < 2d; time += .04d)
            once.Observe(time, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        False(once.Initiative.HasPendingOrControl, "same continuously held target gets only one attempt despite Clear");
        once.Observe(2d, targetReady: false, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        once.Observe(2.01d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        once.Observe(2.06d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        once.Observe(2.11d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        once.Observe(2.14d, own: Action.Idle, stage: Stage.None, direction: Direction.None);
        Equal(Travel | Move.AttackLeft, once.Input(2.14d, Travel).flags, "new ready after cooldown can start another attempt");
    }

    private static void VisibilityCache()
    {
        var s = new Scenario();
        Vec3 a = new(0f, 0f, 1.6f), b = new(1f, 0f, 1.6f);
        False(s.Initiative.TryGetCachedSight(0d, 1, a, b, out _), "empty sight cache misses");
        s.Initiative.RememberSight(0d, 1, a, b, false);
        True(s.Initiative.TryGetCachedSight(.05d, 1, a, b, out bool clear), "blocked sight result is cached");
        False(clear, "cached result preserves obstruction");
        True(s.Initiative.TryGetCachedSight(.099d, 1, new Vec3(.09f, 0f, 1.6f), b, out _), "small movement stays cached");
        False(s.Initiative.TryGetCachedSight(.05d, 1, new Vec3(.101f, 0f, 1.6f), b, out _), "moving eye over .1m invalidates");
        False(s.Initiative.TryGetCachedSight(.05d, 1, a, new Vec3(1.101f, 0f, 1.6f), out _), "target eye movement invalidates");
        False(s.Initiative.TryGetCachedSight(.10d, 1, a, b, out _), "100ms cache age invalidates");
        False(s.Initiative.TryGetCachedSight(.05d, 2, a, b, out _), "target switch invalidates");
        s.Initiative.RememberSight(1d, 1, a, b, true);
        False(s.Initiative.TryGetCachedSight(.9d, 1, a, b, out _), "time rollback invalidates");
        False(s.Initiative.TryGetCachedSight(1d, 1, new Vec3(float.NaN, 0f, 0f), b, out _), "NaN eye invalidates");
        s.Initiative.Suspend();
        True(s.Initiative.TryGetCachedSight(1.01d, 1, a, b, out clear) && clear, "thread-safe suspend never touches main-thread cache");
        s.Initiative.RememberSight(double.NaN, 1, a, b, true);
        False(s.Initiative.TryGetCachedSight(1d, 1, a, b, out _), "invalid sight publication clears cache");
    }

    private static void Concurrency()
    {
        var s = new Scenario();
        s.Observe(0d);
        Parallel.For(0, 10000, i =>
        {
            switch (i % 3)
            {
                case 0: s.Observe(0d); break;
                case 1: s.Input(0d, Travel | Move.DefendUp); break;
                default: s.Initiative.Suspend(); break;
            }
        });
        s.Initiative.Suspend();
        Equal(Travel | Move.DefendUp, s.Input(0d, Travel | Move.DefendUp).flags,
            "after concurrent samples/input/suspend, final suspend wins");
    }

    private static void True(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static void False(bool value, string message) => True(!value, message);
    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        _checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message}: expected {expected}, got {actual}");
    }
}
