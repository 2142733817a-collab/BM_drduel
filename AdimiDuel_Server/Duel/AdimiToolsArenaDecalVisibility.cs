using AdimiToolsShared;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsArenaDecalVisibility : MissionLogic
{
    internal const string Tag = "adimi_hide_during_combat";
    private const float HiddenOffsetZ = 3f;
    private const float TravelSeconds = 1.5f;
    private readonly Dictionary<VolumeBox, List<DecalRoot>> _groups = new(ReferenceEqualityComparer.Instance);
    private AdimiToolsMissionMultiplayerDuel? _duel;
    private AdimiToolsTeamBattleBehavior? _teamBattle;
    private AdimiToolsAiArenaBehavior? _aiArena;

    private sealed record DecalRoot(SynchedMissionObject Synched, MatrixFrame VisibleFrame, MatrixFrame HiddenFrame)
    {
        public bool TargetVisible { get; set; } = true;
    }

    public override void AfterStart()
    {
        GameEntity[] roots = Mission.Scene.FindEntitiesWithTag(Tag).ToArray();
        if (roots.Length == 0)
        {
            return;
        }

        VolumeBox[] areas = Mission.Scene.FindEntitiesWithTag(AdimiToolsAiArenaTags.AreaRoleTag)
            .Concat(Mission.Scene.FindEntitiesWithTag(AdimiToolsTeamBattlePolicy.Area))
            .Select(entity => entity.GetFirstScriptOfType<VolumeBox>())
            .OfType<VolumeBox>()
            .Distinct<VolumeBox>(ReferenceEqualityComparer.Instance)
            .ToArray();

        foreach (GameEntity root in roots)
        {
            SynchedMissionObject? synched = root.GetFirstScriptOfType<SynchedMissionObject>();
            VolumeBox[] matches = areas.Where(area => area.IsPointIn(root.GlobalPosition)).ToArray();
            if (synched == null || synched.GetType() != typeof(SynchedMissionObject)
                || synched.Id.Id < 0 || synched.IsDisabled || matches.Length != 1 || !IsSeparateVisualGroup(root))
            {
                AdimiToolsConsoleLog.Log($"Arena decal '{root.Name}' skipped: use a separate visible root with its original SynchedMissionObject inside one arena box.");
                continue;
            }

            if (root.GetVisibilityExcludeParents())
            {
                if (!_groups.TryGetValue(matches[0], out List<DecalRoot>? group))
                {
                    group = new List<DecalRoot>();
                    _groups.Add(matches[0], group);
                }

                MatrixFrame visibleFrame = root.GetFrame();
                MatrixFrame hiddenFrame = visibleFrame;
                hiddenFrame.origin.z -= HiddenOffsetZ;
                group.Add(new DecalRoot(synched, visibleFrame, hiddenFrame));
                AdimiToolsConsoleLog.Log($"Arena decal '{root.Name}' ready; object={synched.Id.Id}, z={visibleFrame.origin.z:F2}/{hiddenFrame.origin.z:F2}.");
            }
        }

        _duel = Mission.GetMissionBehavior<AdimiToolsMissionMultiplayerDuel>();
        _teamBattle = Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>();
        _aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
    }

    public override void OnMissionTick(float dt)
    {
        foreach (var (area, group) in _groups)
        {
            bool fighting = _duel?.IsFightingIn(area) == true || _teamBattle?.IsFightingIn(area) == true
                || _aiArena?.IsFightingIn(area) == true;
            SetVisibility(group, visible: !fighting);
        }
    }

    public override void OnRemoveBehavior()
    {
        foreach (List<DecalRoot> group in _groups.Values)
        {
            SetVisibility(group, visible: true, immediately: true);
        }

        _groups.Clear();
        base.OnRemoveBehavior();
    }

    private static bool IsSeparateVisualGroup(GameEntity root)
    {
        // Group only the decals. Detection boxes, triggers and collision objects
        // must stay outside the visibility hierarchy.
        var children = new List<GameEntity>();
        root.GetChildrenRecursive(ref children);
        return root.Parent == null && !children.Append(root).Any(entity => entity.GetFirstScriptOfType<VolumeBox>() != null
            || entity.Tags.Any(tag => tag.StartsWith("adimi_ai_", StringComparison.Ordinal)
                || tag.StartsWith("adimi_team_", StringComparison.Ordinal))
            || (!entity.Equals(root) && entity.HasTag(Tag)));
    }

    private static void SetVisibility(List<DecalRoot> group, bool visible, bool immediately = false)
    {
        foreach (DecalRoot root in group)
        {
            SynchedMissionObject synched = root.Synched;
            if (synched.IsDisabled || !synched.GameEntity.IsValid)
            {
                continue;
            }

            MatrixFrame frame = visible ? root.VisibleFrame : root.HiddenFrame;
            bool atTarget = frame.NearlyEquals(synched.GameEntity.GetFrame());
            bool targetChanged = root.TargetVisible != visible;
            if (immediately || targetChanged || (!atTarget && synched.SynchronizeCompleted))
            {
                // Reverse from the current frame without restarting the same
                // animation every mission tick.
                if (immediately || atTarget)
                {
                    synched.SetFrameSynched(ref frame);
                }
                else
                {
                    synched.SetFrameSynchedOverTime(ref frame, TravelSeconds);
                    AdimiToolsConsoleLog.Log($"Arena decal object={synched.Id.Id} moving {(visible ? "up" : "down")}; z={frame.origin.z:F2}, seconds={TravelSeconds:F1}.");
                }

                root.TargetVisible = visible;
            }

            // Keep the group visible during its descent and show it when it
            // starts rising. Hide only after the synchronized descent finishes.
            bool showNow = visible || !synched.SynchronizeCompleted || !atTarget;
            if (synched.GameEntity.GetVisibilityExcludeParents() != showNow)
            {
                synched.SetVisibleSynched(showNow);
            }
        }
    }
}
