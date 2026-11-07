using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// AI do not have a MissionPeer. Store their combat perks separately from
// player perks, with the same conditions as the Native Duel presets.
internal sealed class AdimiToolsAiPerkEffects
{
    private static readonly ConditionalWeakTable<Agent, AdimiToolsAiPerkEffects> Active = new();

    private readonly string _classId;
    private readonly bool _throwingSpear;

    private AdimiToolsAiPerkEffects(string classId, bool throwingSpear)
    {
        _classId = classId;
        _throwingSpear = throwingSpear;
    }

    public static void Register(Agent agent, string classId, bool throwingSpear)
        => Active.Add(agent, new AdimiToolsAiPerkEffects(classId, throwingSpear));

    public static AdimiToolsAiPerkEffects? Find(Agent? agent)
        => agent != null && Active.TryGetValue(agent, out AdimiToolsAiPerkEffects? effects) ? effects : null;

    public static AdimiToolsAiPerkEffects? ForCombatant(Agent? agent) => Find(agent?.IsMount == true ? agent.RiderAgent : agent);

    public bool IsFiann => _classId == "mp_heavy_ranged_battania";

    public float RelativeSpeedBonus(Agent attacker) => _classId switch
    {
        "mp_shock_infantry_aserai" when !attacker.HasMount => 0.4f,
        "mp_light_cavalry_battania" when attacker.HasMount => 0.5f,
        "mp_heavy_cavalry_khuzait" when attacker.HasMount => 0.33f,
        _ => 0f,
    };

    public float ShieldDamage(Agent attacker) => _classId switch
    {
        "mp_shock_infantry_aserai" when !attacker.HasMount => 0.25f,
        "mp_shock_infantry_sturgia" => 0.30f,
        _ => 0f,
    };

    public float Damage(Agent attacker, bool ranged, DamageTypes damageType) => _classId switch
    {
        "mp_heavy_infantry_vlandia" or "mp_heavy_infantry_battania" when !ranged && !attacker.HasMount => 0.10f,
        "mp_shock_infantry_battania" when !ranged => BloodRage(attacker),
        "mp_heavy_infantry_sturgia" when !ranged && !attacker.HasMount && damageType == DamageTypes.Cut => 0.17f,
        "mp_heavy_infantry_khuzait" when !ranged && !_throwingSpear => 0.05f,
        _ => 0f,
    };

    public float MountDamage(bool ranged) => _classId switch
    {
        "mp_light_ranged_sturgia" => 0.5f,
        "mp_heavy_infantry_khuzait" when ranged && _throwingSpear => 1f,
        _ => 0f,
    };

    public float DamageTaken(Agent defender, bool ranged, DamageTypes type)
    {
        if (defender.IsMount)
        {
            return !ranged ? 0f : _classId switch
            {
                "mp_heavy_cavalry_empire" => -0.35f,
                "mp_heavy_cavalry_vlandia" => -0.33f,
                "mp_heavy_cavalry_sturgia" => -0.25f,
                "mp_heavy_cavalry_aserai" => -0.50f,
                _ => 0f,
            };
        }

        return _classId switch
        {
            "mp_heavy_infantry_empire" when type == DamageTypes.Cut => -0.20f,
            "mp_heavy_cavalry_empire" when ranged => -0.35f,
            "mp_heavy_cavalry_vlandia" when ranged => -0.33f,
            "mp_shock_infantry_battania" when !ranged => -BloodRage(defender),
            "mp_light_cavalry_battania" when type == DamageTypes.Pierce => -0.20f,
            "mp_shock_infantry_sturgia" when type == DamageTypes.Blunt => -0.30f,
            "mp_heavy_infantry_sturgia" when type == DamageTypes.Blunt => -0.33f,
            "mp_heavy_cavalry_sturgia" => -0.15f,
            _ => 0f,
        };
    }

    public void ApplyStats(Agent agent, AgentDrivenProperties properties)
    {
        float speed = _classId switch
        {
            "mp_heavy_ranged_empire" or "mp_heavy_ranged_khuzait" => 1.05f,
            "mp_heavy_infantry_vlandia" or "mp_heavy_infantry_battania" when !agent.HasMount => 1.05f,
            "mp_heavy_infantry_khuzait" when !_throwingSpear => 1.07f,
            _ => 1f,
        };
        if (speed != 1f)
        {
            Scale(properties, DrivenProperty.MaxSpeedMultiplier, speed);
        }

        EquipmentIndex index = agent.GetPrimaryWieldedItemIndex();
        WeaponComponentData? weapon = index == EquipmentIndex.None ? null : agent.Equipment[index].CurrentUsageItem;
        if (weapon == null)
        {
            return;
        }

        bool bow = weapon.WeaponClass == WeaponClass.Bow;
        bool wideGrip = !weapon.IsRangedWeapon && (weapon.WeaponFlags & WeaponFlags.WideGrip) != 0;
        bool marksman = _classId == "mp_heavy_ranged_khuzait"
            || (!agent.HasMount && (_classId is "mp_heavy_ranged_aserai" or "mp_heavy_ranged_battania"));
        if (marksman && (bow || wideGrip))
        {
            Scale(properties, DrivenProperty.WeaponUnsteadyBeginTime, 3f);
            Scale(properties, DrivenProperty.WeaponUnsteadyEndTime, 3f);
        }

        if (bow && agent.HasMount && (_classId is "mp_horse_archer_khuzait" or "mp_heavy_cavalry_aserai"))
        {
            Scale(properties, DrivenProperty.WeaponWorstMobileAccuracyPenalty, 0.5f);
            if (_classId == "mp_horse_archer_khuzait")
            {
                Scale(properties, DrivenProperty.WeaponRotationalAccuracyPenaltyInRadians, 0.5f);
            }
        }
    }

    private static void Scale(AgentDrivenProperties properties, DrivenProperty property, float factor)
        => properties.SetStat(property, properties.GetStat(property) * factor);

    private static float BloodRage(Agent agent)
    {
        float health = agent.Health;
        return health <= 20f ? 0.28f : health <= 40f ? 0.21f : health <= 60f ? 0.14f : health <= 80f ? 0.07f : 0f;
    }
}
