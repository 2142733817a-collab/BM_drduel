using AdimiToolsShared;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsDuelSpawnFrame : SpawnFrameBehaviorBase
{
    private IEnumerable<GameEntity>? _sectionASpawnPoints;
    private IEnumerable<GameEntity>? _sectionBSpawnPoints;
    private IEnumerable<GameEntity>? _sectionCSpawnPoints;
    private IEnumerable<GameEntity>? _sectionEntities;

    public override void Initialize()
    {
        base.Initialize();
        _sectionASpawnPoints = new List<GameEntity>();
        _sectionBSpawnPoints = new List<GameEntity>();
        _sectionCSpawnPoints = new List<GameEntity>();
        _sectionEntities = Mission.Current.Scene.FindEntitiesWithTag("is_section");

        AdimiToolsConsoleLog.Log($"Initialize spawnpints");
        foreach (GameEntity spawnPoint in SpawnPoints)
        {
            float closestDistance = float.MaxValue;
            GameEntity? closestEntity = null;
            foreach (GameEntity section in _sectionEntities)
            {
                float distance = spawnPoint.GlobalPosition.Distance(section.GlobalPosition);
                if (distance < closestDistance)
                {
                    closestEntity = section;
                    closestDistance = distance;
                }
            }

            if (closestEntity != null && closestEntity.HasTag("section_b"))
            {
                _sectionBSpawnPoints = _sectionBSpawnPoints.Append(spawnPoint);
            }
            else if (closestEntity != null && closestEntity.HasTag("section_c"))
            {
                _sectionCSpawnPoints = _sectionCSpawnPoints.Append(spawnPoint);
            }
            else if (closestEntity != null) /* if (closestEntity != null && closestEntity.HasTag("teleport_door_a")) */ // A is default
            {
                _sectionASpawnPoints = _sectionASpawnPoints.Append(spawnPoint);
            }
        }
    }

    public override MatrixFrame GetSpawnFrame(Team team, bool hasMount, bool isInitialSpawn)
    {
        return GetSpawnFrameFromSpawnPoints(SpawnPoints.ToList(), team, hasMount);
    }

    public MatrixFrame? GetTroopSpawnFrame(AdimiToolsDuelSpawnClass spawnClass, Team team, bool hasMount)
    {
        List<GameEntity> matchingSpawnPoints = SpawnPoints.Where(spawnPoint =>
            AdimiToolsDuelSpawnPolicy.IsExclusiveMatch(
                spawnClass,
                spawnPoint.HasTag(AdimiToolsDuelSpawnPolicy.InfantryTag),
                spawnPoint.HasTag(AdimiToolsDuelSpawnPolicy.RangedTag),
                spawnPoint.HasTag(AdimiToolsDuelSpawnPolicy.CavalryTag)) &&
            (!hasMount || !spawnPoint.HasTag("exclude_mounted")) &&
            (hasMount || !spawnPoint.HasTag("exclude_footmen"))).ToList();

        if (matchingSpawnPoints.Count == 0)
        {
            return null;
        }

        // Keep the native candidate scoring so nearby enemies and occupied areas
        // make a matching point less desirable instead of choosing by tag alone.
        return GetSpawnFrameFromSpawnPoints(matchingSpawnPoints, team, hasMount);
    }

    public MatrixFrame GetRandomSectionSpawn(string section, bool hasMount = false)
    {
        IEnumerable<GameEntity> sectionSpawnPoints = section switch
        {
            "B" => _sectionBSpawnPoints!,
            "C" => _sectionCSpawnPoints!,
            _ => _sectionASpawnPoints!,
        };
        List<GameEntity> compatiblePoints = sectionSpawnPoints.Where(spawnPoint => IsCompatibleSpawn(spawnPoint, hasMount)).ToList();
        if (compatiblePoints.Count == 0)
        {
            compatiblePoints = SpawnPoints.Where(spawnPoint => IsCompatibleSpawn(spawnPoint, hasMount)).ToList();
        }

        return GetSpawnFrameFromSpawnPoints(compatiblePoints, null, hasMount);
    }

    public MatrixFrame GetRandomSpawnInCurrentSection(Vec3 pos)
    {
        string strSection = "A";

        float closestDistance = float.MaxValue;
        GameEntity? closestEntity = null;
        foreach (GameEntity section in _sectionEntities!)
        {
            float distance = pos.Distance(section.GlobalPosition);
            if (distance < closestDistance)
            {
                closestEntity = section;
                closestDistance = distance;
            }
        }

        if (closestEntity != null && closestEntity.HasTag("section_b"))
        {
            strSection = "B";
        }
        else if (closestEntity != null && closestEntity.HasTag("section_c"))
        {
            strSection = "C";
        }

        return GetRandomSectionSpawn(strSection);
    }

    // Tries to get the closest respawn to death position
    public MatrixFrame GetBestRespawn(Vec3 deathPosition, bool hasMount = false)
    {
        float closestDistance = float.MaxValue;
        GameEntity? closestSpawnSection = null;

        // Get closest section
        foreach (GameEntity section in _sectionEntities!)
        {
            // Some doors have the same section and are therefore more distributed around an area but in fact only one should be considered if it comes to spawnpoints.
            // Ignore the doors that shouldnt taken into account when figuring out a spawnpoint.
            if (section.HasTag("skip_for_spawn_detection"))
            {
                continue;
            }

            float distance = deathPosition.Distance(section.GlobalPosition);

            if (distance < closestDistance)
            {
                closestSpawnSection = section;
                closestDistance = distance;
            }
        }

        IEnumerable<GameEntity> currentSpawnList = _sectionASpawnPoints!;
        if (closestSpawnSection != null && closestSpawnSection.HasTag("section_b"))
        {
            currentSpawnList = _sectionBSpawnPoints!;
        }
        else if (closestSpawnSection != null && closestSpawnSection.HasTag("section_c"))
        {
            currentSpawnList = _sectionCSpawnPoints!;
        }

        closestDistance = float.MaxValue;
        GameEntity? closestSpawnpoint = null;

        foreach (GameEntity spawnPoint in currentSpawnList!)
        {
            if (!IsCompatibleSpawn(spawnPoint, hasMount))
            {
                continue;
            }

            float distance = deathPosition.Distance(spawnPoint.GlobalPosition);
            if (distance < closestDistance)
            {
                closestSpawnpoint = spawnPoint;
                closestDistance = distance;
            }
        }

        if (closestSpawnpoint == null)
        {
            return GetSpawnFrameFromSpawnPoints(SpawnPoints.Where(spawnPoint => IsCompatibleSpawn(spawnPoint, hasMount)).ToList(), null, hasMount);
        }

        MatrixFrame globalFrame = closestSpawnpoint.GetGlobalFrame();
        globalFrame.rotation.OrthonormalizeAccordingToForwardAndKeepUpAsZAxis();
        return globalFrame;
    }

    private static bool IsCompatibleSpawn(GameEntity spawnPoint, bool hasMount)
    {
        return (!hasMount || !spawnPoint.HasTag("exclude_mounted"))
            && (hasMount || !spawnPoint.HasTag("exclude_footmen"));
    }
}
