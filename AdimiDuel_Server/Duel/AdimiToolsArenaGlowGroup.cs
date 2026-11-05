using System.Runtime.CompilerServices;
using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsArenaGlowGroup
{
    public const string GlowTag = "adimi_arena_glow";

    private sealed class Entry
    {
        public GameEntity Entity { get; init; } = null!;
        public SynchedMissionObject Synched { get; init; } = null!;
        public string Name { get; init; } = string.Empty;
        public bool OriginalVisible { get; init; }
        public bool? AppliedVisible { get; set; }
        public double RetryAt { get; set; }
        public double NextLogAt { get; set; }
        public HashSet<AdimiToolsArenaGlowGroup> HiddenOwners { get; } = new();
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

        var candidates = new List<(GameEntity root, SynchedMissionObject synched)>();
        foreach (GameEntity root in scene.FindEntitiesWithTag(GlowTag))
        {
            try
            {
                if (!root.WeakEntity.IsValid || !root.GlobalPosition.IsValid || !volume.IsPointIn(root.GlobalPosition))
                {
                    continue;
                }

                // Bind decorative roots only. Hiding the area, a trigger or a
                // moving barrier would also hide unrelated children.
                if (root.GetFirstScriptOfType<VolumeBox>() != null
                    || root.Tags.Any(tag => tag is "adimi_ai_area" or "adimi_team_area" or "adimi_ai_barrier"
                        or "adimi_ai_ladder" or "adimi_team_barrier" or "adimi_ai_trigger"
                        || tag.StartsWith("adimi_ai_trigger_", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("The glow tag belongs on an independent decorative root.");
                }

                SynchedMissionObject? synched = root.GetFirstScriptOfType<SynchedMissionObject>();
                if (synched == null || synched.GetType() != typeof(SynchedMissionObject) || synched.Id.Id < 0 || synched.IsDisabled)
                {
                    throw new InvalidOperationException("Pre-attach the original SynchedMissionObject with a valid scene ID; permanently disabled roots are unsupported.");
                }

                candidates.Add((root, synched));
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
                // Deduplicate nested marks only when that valid ancestor is
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
                        OriginalVisible = visible, AppliedVisible = visible,
                    };
                    state.Entries.Add(synched, entry);
                }

                if (!group._entries.Contains(entry))
                {
                    group._entries.Add(entry);
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
        // are already deleted. Restore these decorative snapshots before
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
        }

        _forgotten = true;
        _entries.Clear();
    }

    private static void Apply(Entry entry, double now)
    {
        bool visible = entry.HiddenOwners.Count == 0 && entry.OriginalVisible;
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
