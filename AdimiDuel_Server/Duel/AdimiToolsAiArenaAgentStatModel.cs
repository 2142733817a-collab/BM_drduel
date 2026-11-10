using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaAgentStatModel : MultiplayerAgentStatCalculateModel
{
    public const int SkillBonus = 50;
    public const int HardSkill = 330;
    internal const float HardMeleeReadyHoldMaximum = 0.5f;
    internal const float NormalMeleeReadyHoldMaximum = 0.25f;
    internal const float DefaultKickDesire = 0.45f;
    internal static readonly float KickDesire = ParseKickDesire(Environment.GetEnvironmentVariable("ADIMI_AI_KICK_DESIRE"));

    public override int GetEffectiveSkill(Agent agent, SkillObject skill)
    {
        if (AdimiToolsAiPerkEffects.Find(agent)?.IsFiann == true && (skill == DefaultSkills.Bow || skill == DefaultSkills.TwoHanded))
        {
            return 220;
        }

        if (agent.Origin is AdimiToolsAiArenaSkillOrigin origin && origin.GetSkillOverride(skill) is int overridden)
        {
            return overridden;
        }

        int value = base.GetEffectiveSkill(agent, skill);
        int difficulty = GetDifficulty(agent);
        return difficulty == 3 ? HardSkill : difficulty == 2 ? (int)System.Math.Min((long)value + SkillBonus, int.MaxValue) : value;
    }

    public override int GetEffectiveSkillForWeapon(Agent agent, WeaponComponentData weapon)
    {
        if (AdimiToolsAiPerkEffects.Find(agent)?.IsFiann == true
            && (weapon.RelevantSkill == DefaultSkills.Bow || weapon.RelevantSkill == DefaultSkills.TwoHanded))
        {
            return 220;
        }

        if (agent.Origin is AdimiToolsAiArenaSkillOrigin origin && origin.GetSkillOverride(weapon.RelevantSkill) is int overridden)
        {
            return overridden;
        }

        return GetDifficulty(agent) == 3 ? HardSkill : base.GetEffectiveSkillForWeapon(agent, weapon);
    }

    public override void UpdateAgentStats(Agent agent, AgentDrivenProperties properties)
    {
        base.UpdateAgentStats(agent, properties);
        AdimiToolsAiPerkEffects? effects = AdimiToolsAiPerkEffects.Find(agent);
        if (agent.IsHuman && (IsEnhanced(agent) || agent.Origin is AdimiToolsAiArenaSkillOrigin || effects?.IsFiann == true))
        {
            ApplyHumanSkillStats(agent, properties);
        }
        else if (agent.IsMount && IsEnhanced(agent.RiderAgent))
        {
            ApplyMountSkillStats(agent, properties);
        }

        effects?.ApplyStats(agent, properties);
        ApplyArenaCombatDecisions(agent, properties);
        ApplyNormalMeleeReadyWindow(agent, properties);
        ApplyHardMeleeTactics(agent, properties);
        ApplyHardRoleNativeDecisions(agent, properties);
    }

    internal static float ParseKickDesire(string? setting)
    {
        return float.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            && value is >= 0f and <= 1f ? value : DefaultKickDesire;
    }

    internal static void ApplyHardRoleNativeDecisions(Agent agent, AgentDrivenProperties properties)
    {
        if (agent.Origin is not AdimiToolsAiArenaEnhancedOrigin { Difficulty: 3 } origin
            || !agent.IsHuman || agent.Controller != AgentControllerType.AI)
        {
            return;
        }

        if (origin.NativeRole == AdimiToolsAiArenaNativeRole.HardArcher && !agent.HasMount)
        {
            // Native 1.4.8 derives these two ranger-lead-error properties from
            // difficulty and skill. Native still owns target selection,
            // trajectory, release, defensive actions and navigation.
            properties.AiRangerLeadErrorMin = 0f;
            properties.AiRangerLeadErrorMax = 0f;
        }
        else if (origin.NativeRole == AdimiToolsAiArenaNativeRole.HardCavalry && agent.HasMount
            && float.IsFinite(properties.AiWeaponFavorMultiplierPolearm)
            && properties.AiWeaponFavorMultiplierPolearm > 0f)
        {
            // A bounded preference, not an equipment lock or a forced thrust.
            properties.AiWeaponFavorMultiplierPolearm = System.Math.Max(properties.AiWeaponFavorMultiplierPolearm, 1.15f);
        }
    }

    internal static void ApplyHardMeleeTactics(Agent agent, AgentDrivenProperties properties)
    {
        if (agent.Origin is not AdimiToolsAiArenaEnhancedOrigin { Difficulty: 3 }
            || !agent.IsHuman || agent.Controller != AgentControllerType.AI)
        {
            return;
        }

        EquipmentIndex index = agent.GetPrimaryWieldedItemIndex();
        WeaponComponentData? usage = index == EquipmentIndex.None ? null : agent.Equipment[index].CurrentUsageItem;
        if (usage?.IsMeleeWeapon != true || usage.IsRangedWeapon)
        {
            return;
        }

        // Give Native's ready-hold decision a modest, variable window. Native
        // still decides when to defend, change attack direction and release.
        properties.AIHoldingReadyMaxDuration = System.Math.Max(properties.AIHoldingReadyMaxDuration, HardMeleeReadyHoldMaximum);
        properties.AIDecideOnRealizeEnemyBlockingAttackAbility = System.Math.Max(properties.AIDecideOnRealizeEnemyBlockingAttackAbility, 1f);
    }

    internal static void ApplyNormalMeleeReadyWindow(Agent agent, AgentDrivenProperties properties)
    {
        if (agent.Origin is not AdimiToolsAiArenaEnhancedOrigin { Difficulty: 2, NativeRole: AdimiToolsAiArenaNativeRole.Other }
            || !agent.IsHuman || agent.Controller != AgentControllerType.AI || agent.HasMount)
        {
            return;
        }

        EquipmentIndex index = agent.GetPrimaryWieldedItemIndex();
        WeaponComponentData? usage = index == EquipmentIndex.None ? null : agent.Equipment[index].CurrentUsageItem;
        if (usage?.IsMeleeWeapon != true || usage.IsRangedWeapon) return;

        // Gives Native a small chance to retain a visible ready long enough
        // for a deliberate feint. It does not force every attack to be held.
        properties.AIHoldingReadyMaxDuration = System.Math.Max(properties.AIHoldingReadyMaxDuration, NormalMeleeReadyHoldMaximum);
    }

    internal static void ApplyArenaCombatDecisions(Agent agent, AgentDrivenProperties properties)
    {
        if (agent.Origin is not AdimiToolsAiArenaOrigin
            || !agent.IsHuman || agent.Controller != AgentControllerType.AI)
        {
            return;
        }

        // Reapply after every Native stat update, including weapon changes.
        // These are decision inputs to the engine, not guaranteed outcomes;
        // keep physical animation speeds, health and damage unchanged.
        properties.AIAttackOnParryChance = System.Math.Max(properties.AIAttackOnParryChance, 0.12f);
        properties.AIAttackOnDecideChance = System.Math.Max(properties.AIAttackOnDecideChance, 0.24f);
        if (!agent.HasMount && KickDesire > 0f)
        {
            // Native chooses eligible targets and action timing. Zero keeps the stock value.
            properties.AiKick = KickDesire;
        }

        if (properties.AiShootFreq > 0f && properties.AiShootFreq < 1f)
        {
            properties.AiShootFreq = System.Math.Min(1f, properties.AiShootFreq * 1.15f);
        }

        if (properties.AiWaitBeforeShootFactor > 0f)
        {
            properties.AiWaitBeforeShootFactor *= 0.85f;
        }

        if (agent.Origin is not AdimiToolsAiArenaEnhancedOrigin { Difficulty: 3 })
        {
            return;
        }

        properties.AIBlockOnDecideAbility = System.Math.Max(properties.AIBlockOnDecideAbility, 0.99f);
        properties.AIParryOnDecideAbility = System.Math.Max(properties.AIParryOnDecideAbility, 0.98f);
        properties.AIParryOnAttackingContinueAbility = System.Math.Max(properties.AIParryOnAttackingContinueAbility, 0.98f);
        properties.AIDecideOnRealizeEnemyBlockingAttackAbility = System.Math.Max(properties.AIDecideOnRealizeEnemyBlockingAttackAbility, 0.96f);
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
        int riding = GetEffectiveSkill(agent, DefaultSkills.Riding);

        // Keep the base melee animation speed and apply boosted proficiency
        // to ranged timing. The engine still uses effective skill for AI decisions.
        if (weapon?.IsRangedWeapon == true)
        {
            int weaponSkill = GetEffectiveSkill(agent, item?.RelevantSkill ?? DefaultSkills.Athletics);
            float speed = 0.93f + 0.0007f * weaponSkill;
            float mountedFactor = agent.HasMount ? 1f - System.Math.Max(0f, 0.3f - riding * 0.003f) : 1f;
            properties.SwingSpeedMultiplier = System.Math.Max(0f, WithPerk(perks, DrivenProperty.SwingSpeedMultiplier, speed) * mountedFactor);
            properties.ThrustOrRangedReadySpeedMultiplier = System.Math.Max(0f, WithPerk(perks, DrivenProperty.ThrustOrRangedReadySpeedMultiplier, speed) * mountedFactor);
            properties.ReloadSpeed = System.Math.Max(0f, WithPerk(perks, DrivenProperty.ReloadSpeed, speed) * mountedFactor);
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

// Identifies only bots created by this training mode, including otherwise
// ordinary and selectively skilled agents. It does not change troop skills.
internal class AdimiToolsAiArenaOrigin : BasicBattleAgentOrigin
{
    public AdimiToolsAiArenaOrigin(BasicCharacterObject character)
        : base(character)
    {
    }
}

internal enum AdimiToolsAiArenaNativeRole
{
    Other,
    HardArcher,
    HardCavalry,
}

internal sealed class AdimiToolsAiArenaEnhancedOrigin : AdimiToolsAiArenaOrigin
{
    public AdimiToolsAiArenaEnhancedOrigin(BasicCharacterObject character, int difficulty = 2, AdimiToolsAiArenaHardProfile? hardProfile = null,
        AdimiToolsAiArenaNativeRole nativeRole = AdimiToolsAiArenaNativeRole.Other)
        : base(character)
    {
        if (difficulty < 1 || difficulty > 3)
        {
            throw new System.ArgumentOutOfRangeException(nameof(difficulty));
        }

        Difficulty = difficulty;
        HardProfile = hardProfile;
        NativeRole = nativeRole;
    }

    public int Difficulty { get; }

    public AdimiToolsAiArenaHardProfile? HardProfile { get; }

    public AdimiToolsAiArenaNativeRole NativeRole { get; }
}
