using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaAgentStatModel : MultiplayerAgentStatCalculateModel
{
    public const int SkillBonus = 50;
    public const int HardSkill = 330;

    public override int GetEffectiveSkill(Agent agent, SkillObject skill)
    {
        int value = base.GetEffectiveSkill(agent, skill);
        int difficulty = GetDifficulty(agent);
        return difficulty == 3 ? HardSkill : difficulty == 2 ? (int)System.Math.Min((long)value + SkillBonus, int.MaxValue) : value;
    }

    public override int GetEffectiveSkillForWeapon(Agent agent, WeaponComponentData weapon)
    {
        return GetDifficulty(agent) == 3 ? HardSkill : base.GetEffectiveSkillForWeapon(agent, weapon);
    }

    public override void UpdateAgentStats(Agent agent, AgentDrivenProperties properties)
    {
        base.UpdateAgentStats(agent, properties);
        if (agent.IsHuman && IsEnhanced(agent))
        {
            ApplyHumanSkillStats(agent, properties);
        }
        else if (agent.IsMount && IsEnhanced(agent.RiderAgent))
        {
            ApplyMountSkillStats(agent, properties);
        }
    }

    private static bool IsEnhanced(Agent? agent)
    {
        return GetDifficulty(agent) > 1;
    }

    private static int GetDifficulty(Agent? agent)
    {
        return (agent?.Origin as AdimiToolsAiArenaEnhancedOrigin)?.Difficulty ?? 1;
    }

    private static float WithPerk(MPPerkObject.MPPerkHandler? perks, DrivenProperty property, float value)
    {
        return value + (perks?.GetDrivenPropertyBonus(property, value) ?? 0f);
    }

    private void ApplyHumanSkillStats(Agent agent, AgentDrivenProperties properties)
    {
        EquipmentIndex index = agent.GetPrimaryWieldedItemIndex();
        ItemObject? item = index == EquipmentIndex.None ? null : agent.Equipment[index].Item;
        WeaponComponentData? weapon = index == EquipmentIndex.None ? null : agent.Equipment[index].CurrentUsageItem;
        MPPerkObject.MPPerkHandler? perks = MPPerkObject.GetPerkHandler(agent);
        int weaponSkill = GetEffectiveSkill(agent, item?.RelevantSkill ?? DefaultSkills.Athletics);
        int riding = GetEffectiveSkill(agent, DefaultSkills.Riding);
        float speed = 0.93f + 0.0007f * weaponSkill;

        // The original private MP human update reads Character directly for
        // these values. Recompute only those reads with the effective skill,
        // preserving native ordering: skill formula, perk, mounted penalty.
        properties.SwingSpeedMultiplier = WithPerk(perks, DrivenProperty.SwingSpeedMultiplier, speed);
        properties.ThrustOrRangedReadySpeedMultiplier = WithPerk(perks, DrivenProperty.ThrustOrRangedReadySpeedMultiplier, speed);
        properties.ReloadSpeed = WithPerk(perks, DrivenProperty.ReloadSpeed, speed);
        if (agent.HasMount && weapon != null)
        {
            float penalty = 0.3f - riding * 0.003f;
            if (penalty > 0f)
            {
                properties.SwingSpeedMultiplier = System.Math.Max(0f, properties.SwingSpeedMultiplier * (1f - penalty));
                properties.ThrustOrRangedReadySpeedMultiplier = System.Math.Max(0f, properties.ThrustOrRangedReadySpeedMultiplier * (1f - penalty));
                properties.ReloadSpeed = System.Math.Max(0f, properties.ReloadSpeed * (1f - penalty));
            }
        }

        float mountRiding = agent.MountAgent?.GetAgentDrivenPropertyValue(DrivenProperty.AttributeRiding) ?? 1f;
        properties.AttributeRiding = WithPerk(perks, DrivenProperty.AttributeRiding, riding * mountRiding);
        if (agent.HasMount && weapon?.IsRangedWeapon == true)
        {
            ApplyMountedRangedAccuracy(agent, properties, weapon, riding, perks);
        }
    }

    private void ApplyMountedRangedAccuracy(Agent agent, AgentDrivenProperties properties, WeaponComponentData weapon, int riding, MPPerkObject.MPPerkHandler? perks)
    {
        int skill = GetEffectiveSkillForWeapon(agent, weapon);
        float penalty = System.Math.Max(0f, (1f - skill / 500f) * (1f - riding / 1800f));
        float movement = System.Math.Max(0f, 0.025f * penalty);
        float unsteady = System.Math.Max(0f, 0.06f * penalty);
        if (weapon.RelevantSkill == DefaultSkills.Bow)
        {
            float value = MBMath.ClampFloat((weapon.ThrustSpeed - 60f) / 75f, 0f, 1f);
            movement *= 6f;
            unsteady *= 4.5f / MBMath.Lerp(0.75f, 2f, value);
        }
        else if (weapon.RelevantSkill == DefaultSkills.Throwing)
        {
            if (weapon.WeaponClass == WeaponClass.Sling)
            {
                float value = MBMath.ClampFloat((weapon.ThrustSpeed - 30f) / 90f, 0f, 1f);
                movement *= 5f;
                unsteady *= 2.4f * MBMath.Lerp(2.4f, 1.2f, value);
            }
            else
            {
                float value = MBMath.ClampFloat((weapon.ThrustSpeed - 85f) / 17f, 0f, 1f);
                movement *= 0.5f;
                unsteady *= 1.5f * MBMath.Lerp(1.5f, 0.8f, value);
            }
        }
        else if (weapon.RelevantSkill == DefaultSkills.Crossbow)
        {
            movement *= 2.5f;
            unsteady *= 1.2f;
        }

        properties.WeaponMaxMovementAccuracyPenalty = WithPerk(perks, DrivenProperty.WeaponWorstMobileAccuracyPenalty, movement);
        properties.WeaponMaxUnsteadyAccuracyPenalty = WithPerk(perks, DrivenProperty.WeaponWorstUnsteadyAccuracyPenalty, unsteady);
    }

    private void ApplyMountSkillStats(Agent agent, AgentDrivenProperties properties)
    {
        Agent rider = agent.RiderAgent;
        int riding = GetEffectiveSkill(rider, DefaultSkills.Riding);
        MPPerkObject.MPPerkHandler? perks = MPPerkObject.GetPerkHandler(rider);
        EquipmentElement mount = agent.SpawnEquipment[EquipmentIndex.Horse];
        EquipmentElement harness = agent.SpawnEquipment[EquipmentIndex.HorseHarness];
        properties.MountManeuver = mount.GetModifiedMountManeuver(in harness) * (1f + (perks?.GetMountManeuver() ?? 0f));
        properties.MountSpeed = (mount.GetModifiedMountSpeed(in harness) + 1) * 0.22f * (1f + (perks?.GetMountSpeed() ?? 0f));
        properties.TopSpeedReachDuration = Game.Current.BasicModels.RidingModel.CalculateAcceleration(in mount, in harness, riding);
        properties.MountSpeed *= 1f + riding * 0.0032f;
        properties.MountManeuver *= 1f + riding * 0.0035f;
    }
}

internal sealed class AdimiToolsAiArenaEnhancedOrigin : BasicBattleAgentOrigin
{
    public AdimiToolsAiArenaEnhancedOrigin(BasicCharacterObject character, int difficulty = 2, AdimiToolsAiArenaHardProfile? hardProfile = null)
        : base(character)
    {
        if (difficulty < 1 || difficulty > 3)
        {
            throw new System.ArgumentOutOfRangeException(nameof(difficulty));
        }

        Difficulty = difficulty;
        HardProfile = hardProfile;
    }

    public int Difficulty { get; }

    public AdimiToolsAiArenaHardProfile? HardProfile { get; }
}
