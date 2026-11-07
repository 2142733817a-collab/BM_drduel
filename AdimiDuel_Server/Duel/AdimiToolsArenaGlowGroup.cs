using System.Runtime.CompilerServices;
using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsArenaGlowGroup
{
    public const string GlowTag = "adimi_arena_glow";
    public const string NativeGlowPrefab = "highlight_item_glow_ground";
    public const string NativePointerGlowPrefab = "highlight_pointer_glow_ground";

    private sealed class Entry
    {
        public GameEntity Entity { get; init; } = null!;
        public SynchedMissionObject Synched { get; init; } = null!;
        public string Name { get; init; } = string.Empty;
        public bool? AppliedVisible { get; set; }
        public double RetryAt { get; set; }
        public double NextLogAt { get; set; }
        public HashSet<AdimiToolsArenaGlowGroup> HiddenOwners { get; } = new();
        public HashSet<AdimiToolsArenaGlowGroup> RegisteredOwners { get; } = new();
    }

    private sealed class SceneState
    {
        // Script instances are canonical even when a scene query returns new
        // GameEntity wrappers. Never identify a shared root by wrapper alone.
        public Dictionary<SynchedMissionObject, Entry> Entries { get; } = new(ReferenceEqualityComparer.Instance);
    }

    // Weak scene keys keep snapshots available to later-created overlapping
    // groups without retaining a previous mission after its scene is gone.
    private static readonly ConditionalWeakTable<Scene, SceneState> SceneStates = new();

    private readonly List<Entry> _entries = new();
    private bool _forgotten;

    public static AdimiToolsArenaGlowGroup Create(Scene scene, GameEntity? area)
    {
        var group = new AdimiToolsArenaGlowGroup();
        if (!GameNetwork.IsServer || area == null)
        {
            return group;
        }

        VolumeBox? volume = area.GetFirstScriptOfType<VolumeBox>();
        if (volume == null)
        {
            return group;
        }

        var scale = area.GetGlobalFrame().rotation.GetScaleVector();
        if (!float.IsFinite(scale.x) || !float.IsFinite(scale.y) || !float.IsFinite(scale.z)
            || scale.x <= 0f || scale.y <= 0f || scale.z <= 0f)
        {
            return group;
        }

        // The decoration's Native identity determines what it is; the Box
        // determines which arena owns it. Existing glow tags are ignored.
        var entities = new List<GameEntity>();
        scene.GetEntities(ref entities);
        var candidates = new List<(GameEntity root, SynchedMissionObject synched)>();
        foreach (GameEntity root in entities)
        {
            try
            {
                if (!root.WeakEntity.IsValid
                    || (!IsNativeGlowName(root.Name) && !IsNativeGlowName(root.GetPrefabName()))
                    || !root.GlobalPosition.IsValid || !volume.IsPointIn(root.GlobalPosition))
                {
                    continue;
                }

                // Bind decorative roots only. Hiding the area, a trigger or a
                // moving barrier would also hide unrelated children.
                if (IsInteractionObject(root))
                {
                    throw new InvalidOperationException("A Native glow must be an independent decorative root, not an arena box or interaction object.");
                }

                SynchedMissionObject? synched = root.GetFirstScriptOfType<SynchedMissionObject>();
                if (synched != null)
                {
                    ValidateSynched(synched);
                    candidates.Add((root, synched));
                    continue;
                }

                // A leaf-mounted layout keeps MissionObject.OnPreInit from
                // creating variable-rate physics on the beam through its parent.
                // This changes the native initialization path, not the shader.
                // Register all controls together so a malformed part cannot
                // leave a partially controlled effect behind.
                var children = new List<GameEntity>();
                root.GetChildrenRecursive(ref children);
                var parts = new List<(GameEntity root, SynchedMissionObject synched)>();
                foreach (GameEntity child in children)
                {
                    SynchedMissionObject? part = child.GetFirstScriptOfType<SynchedMissionObject>();
                    if (part == null)
                    {
                        continue;
                    }

                    if (!child.WeakEntity.IsValid || child.ChildCount != 0 || IsInteractionObject(child)
                        || part.Id.CreatedAtRuntime)
                    {
                        throw new InvalidOperationException("Glow part controllers must be decorative scene leaves with no child entities.");
                    }

                    ValidateSynched(part);
                    parts.Add((child, part));
                }

                if (parts.Count == 0)
                {
                    throw new InvalidOperationException("Pre-attach the original SynchedMissionObject on the glow root or its effect leaves.");
                }

                candidates.AddRange(parts);
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Arena glow skipped: {exception.Message}");
            }
        }

        var candidateScripts = candidates.Select(candidate => candidate.synched).ToHashSet<SynchedMissionObject>(ReferenceEqualityComparer.Instance);
        SceneState state = SceneStates.GetValue(scene, _ => new SceneState());
        foreach (var (root, synched) in candidates)
        {
            try
            {
                // Deduplicate nested decorations only when that valid ancestor is
                // also inside this Box. Outside parents cannot steal a glow.
                bool nested = false;
                for (GameEntity? parent = root.Parent; parent != null; parent = parent.Parent)
                {
                    var parentScript = parent.GetFirstScriptOfType<SynchedMissionObject>();
                    if (parentScript != null && candidateScripts.Contains(parentScript))
                    {
                        nested = true;
                        break;
                    }
                }

                if (nested)
                {
                    continue;
                }

                if (!state.Entries.TryGetValue(synched, out Entry? entry))
                {
                    bool visible = root.GetVisibilityExcludeParents();
                    entry = new Entry
                    {
                        Entity = root, Synched = synched, Name = root.Name,
                        // A scene/native synchronization snapshot is not the
                        // desired idle state. Confirm the first target even
                        // when the server currently reports that same flag.
                        AppliedVisible = null,
                    };
                    state.Entries.Add(synched, entry);
                    AdimiToolsConsoleLog.Log($"Arena glow '{root.Name}' id={synched.Id.Id}: initial local visibility={visible}; idle visibility will be confirmed explicitly.");
                }

                if (!group._entries.Contains(entry))
                {
                    group._entries.Add(entry);
                    entry.RegisteredOwners.Add(group);
                }
            }
            catch (Exception exception)
            {
                AdimiToolsConsoleLog.Log($"Arena glow skipped: {exception.Message}");
            }
        }

        return group;
    }

    public void Update(bool hidden, double now)
    {
        if (_forgotten || !GameNetwork.IsServer)
        {
            return;
        }

        foreach (Entry entry in _entries)
        {
            if (hidden)
            {
                entry.HiddenOwners.Add(this);
            }
            else
            {
                entry.HiddenOwners.Remove(this);
            }

            Apply(entry, now);
        }
    }

    public void RestoreAndForget(double now)
    {
        if (_forgotten)
        {
            return;
        }

        // OnClearScene still has valid static scene objects, while its Agents
        // are already deleted. Restore these decorative idle states before
        // releasing them; native mission reset does not guarantee this reset.
        foreach (Entry entry in _entries)
        {
            entry.RetryAt = 0d;
        }

        Update(false, now);
        Forget();
    }

    // No native access when only dropping already-invalid scene references.
    public void Forget()
    {
        foreach (Entry entry in _entries)
        {
            entry.HiddenOwners.Remove(this);
            entry.RegisteredOwners.Remove(this);
            if (entry.RegisteredOwners.Count == 0)
            {
                entry.AppliedVisible = null;
            }
        }

        _forgotten = true;
        _entries.Clear();
    }

    private static bool IsNativeGlowName(string name)
        => name is NativeGlowPrefab or NativePointerGlowPrefab;

    private static bool IsInteractionObject(GameEntity entity)
        => entity.GetFirstScriptOfType<VolumeBox>() != null
            || entity.Tags.Any(tag => tag is "adimi_ai_area" or "adimi_team_area" or "adimi_ai_barrier"
                or "adimi_ai_ladder" or "adimi_team_barrier" or "adimi_ai_trigger"
                || tag.StartsWith("adimi_ai_trigger_", StringComparison.Ordinal));

    private static void ValidateSynched(SynchedMissionObject synched)
    {
        if (synched.GetType() != typeof(SynchedMissionObject) || synched.Id.Id < 0 || synched.IsDisabled)
        {
            throw new InvalidOperationException("Pre-attach the original SynchedMissionObject with a valid scene ID; permanently disabled controls are unsupported.");
        }
    }

    private static void Apply(Entry entry, double now)
    {
        bool visible = entry.HiddenOwners.Count == 0;
        if (entry.AppliedVisible == visible || now < entry.RetryAt)
        {
            return;
        }

        try
        {
            if (!entry.Entity.WeakEntity.IsValid || (visible && entry.Synched.IsDisabled))
            {
                entry.RetryAt = now + 2d;
                return;
            }

            // Keep the local flag correct even under an already-hidden parent.
            // Stock forceChildren would overwrite authored child visibility.
            entry.Entity.SetVisibilityExcludeParents(visible);
            GameNetwork.BeginBroadcastModuleEvent();
            try
            {
                GameNetwork.WriteMessage(new SetMissionObjectVisibility(entry.Synched.Id, visible));
            }
            finally
            {
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
            }

            entry.AppliedVisible = visible;
            entry.RetryAt = 0d;
        }
        catch (Exception exception)
        {
            // A failed publication remains unconfirmed even if its local
            // setter succeeded. Any owning group can retry the shared target.
            entry.AppliedVisible = null;
            entry.RetryAt = now + 0.25d;
            if (now >= entry.NextLogAt)
            {
                AdimiToolsConsoleLog.Log($"Arena glow '{entry.Name}' will retry: {exception.Message}");
                entry.NextLogAt = now + 2d;
            }
        }
    }
}
