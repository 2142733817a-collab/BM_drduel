using System.Runtime.CompilerServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A shared scene Box has one lease through readiness, combat and cleanup.
// The original VolumeBox script is canonical across GameEntity wrappers.
internal sealed class AdimiToolsSharedArena
{
    private sealed class MissionAreas
    {
        public Dictionary<VolumeBox, AdimiToolsSharedArena> Areas { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class Boundary
    {
        public object Controller { get; init; } = null!;
        public bool IsAi { get; init; }
        public GameEntity[] Roots { get; init; } = Array.Empty<GameEntity>();
        public Action<bool, bool> Move { get; init; } = null!;
        public Action<double> Tick { get; init; } = null!;
        public Func<bool, bool> IsAtTarget { get; init; } = null!;
        public Func<bool>? IsAvailableForDuel { get; init; }
    }

    private static readonly ConditionalWeakTable<Mission, MissionAreas> Missions = new();
    private readonly List<Boundary> _boundaries = new();
    private readonly VolumeBox _area;
    private object? _owner;

    private AdimiToolsSharedArena(VolumeBox area) => _area = area;

    public bool HasAiController => _boundaries.Any(boundary => boundary.IsAi);
    public GameEntity? DuelSpawn { get; private set; }

    public static AdimiToolsSharedArena? FindFreeDuelArena(Mission mission, Vec3 first, Vec3 second)
    {
        if (!Missions.TryGetValue(mission, out MissionAreas? state))
        {
            return null;
        }

        AdimiToolsSharedArena? match = null;
        foreach (var (box, arena) in state.Areas)
        {
            if (arena._owner != null || !arena._boundaries.Any(boundary => boundary.Roots.Length != 0)
                || !box.IsPointIn(first) || !box.IsPointIn(second)
                || !arena._boundaries.All(boundary => boundary.IsAvailableForDuel?.Invoke() == true))
            {
                continue;
            }

            if (match != null)
            {
                return null;
            }

            match = arena;
        }

        return match;
    }

    public static AdimiToolsSharedArena For(Mission mission, VolumeBox area)
    {
        MissionAreas state = Missions.GetValue(mission, _ => new MissionAreas());
        if (!state.Areas.TryGetValue(area, out AdimiToolsSharedArena? shared))
        {
            shared = new AdimiToolsSharedArena(area);
            state.Areas.Add(area, shared);
        }

        return shared;
    }

    public bool ContainsPoint(Vec3 point) => _area.IsPointIn(point);

    public bool TryAcquire(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner != null && !ReferenceEquals(_owner, owner))
        {
            return false;
        }

        _owner = owner;
        return true;
    }

    public bool Owns(object owner) => owner != null && ReferenceEquals(_owner, owner);

    public bool IsOwnedByOther(object owner) => _owner != null && !ReferenceEquals(_owner, owner);

    public void Release(object owner)
    {
        if (Owns(owner))
        {
            _owner = null;
        }
    }

    public void RegisterBoundary(object controller, bool isAi, IEnumerable<GameEntity> roots,
        Action<bool, bool> move, Action<double> tick, Func<bool, bool> isAtTarget,
        Func<bool>? isAvailableForDuel = null, GameEntity? duelSpawn = null)
    {
        if (_boundaries.Any(boundary => ReferenceEquals(boundary.Controller, controller) || (isAi && boundary.IsAi)))
        {
            throw new InvalidOperationException("A shared arena controller may be registered only once.");
        }

        GameEntity[] entities = roots.ToArray();
        var scripts = new HashSet<SynchedMissionObject>(ReferenceEqualityComparer.Instance);
        foreach (GameEntity root in entities)
        {
            SynchedMissionObject script = root.GetFirstScriptOfType<SynchedMissionObject>()
                ?? throw new InvalidOperationException("A shared boundary needs its original scene synchronization script.");
            if (!scripts.Add(script)
                || entities.Any(other => !ReferenceEquals(other, root) && Related(root, other))
                || _boundaries.Any(boundary => boundary.Roots.Any(other => Related(root, other))))
            {
                throw new InvalidOperationException("A shared moving hierarchy must have exactly one boundary controller.");
            }
        }

        // Publish only after every root passed validation; failed registration
        // must not leave a partial controller in another mode's live arena.
        _boundaries.Add(new Boundary
        {
            Controller = controller, IsAi = isAi, Roots = entities,
            Move = move, Tick = tick, IsAtTarget = isAtTarget,
            IsAvailableForDuel = isAvailableForDuel,
        });
        if (isAi)
        {
            DuelSpawn = duelSpawn;
        }
    }

    public bool IsAiBoundary(GameEntity entity)
        => _boundaries.Any(boundary => boundary.IsAi && boundary.Roots.Any(root => Related(entity, root)));

    public void Move(object owner, bool raised, bool immediately = false)
    {
        RequireOwner(owner);
        InvokeAll(boundary => boundary.Move(raised, immediately));
    }

    public void Tick(object owner, double now)
    {
        RequireOwner(owner);
        InvokeAll(boundary => boundary.Tick(now));
    }

    public bool IsAtTarget(object owner, bool raised)
        => Owns(owner) && _boundaries.All(boundary => boundary.IsAtTarget(raised));

    private static bool Related(GameEntity first, GameEntity second)
        => IsAncestor(first, second) || IsAncestor(second, first);

    private static bool IsAncestor(GameEntity entity, GameEntity ancestor)
    {
        SynchedMissionObject? script = ancestor.GetFirstScriptOfType<SynchedMissionObject>();
        for (GameEntity? node = entity; node != null; node = node.Parent)
        {
            if (node.Equals(ancestor) || (script != null
                && ReferenceEquals(node.GetFirstScriptOfType<SynchedMissionObject>(), script)))
            {
                return true;
            }
        }

        return false;
    }

    private void RequireOwner(object owner)
    {
        if (!Owns(owner))
        {
            throw new InvalidOperationException("Only the current match may move a shared arena boundary.");
        }
    }

    private void InvokeAll(Action<Boundary> action)
    {
        var failures = new List<Exception>();
        foreach (Boundary boundary in _boundaries)
        {
            try
            {
                action(boundary);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }
}
