using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using MathF = TaleWorlds.Library.MathF;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaApplyDamageModel : MultiplayerAgentApplyDamageModel
{
    public override float ApplyGeneralDamageModifiers(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
    {
        if (Mission.Current?.GetMissionBehavior<AdimiToolsTeamBattleBehavior>()?.ShouldBlockDamage(attackInformation.AttackerAgent, attackInformation.VictimAgent) == true)
        {
            return 0f;
        }

        Agent? attacker = attackInformation.AttackerAgent;
        Agent? victim = attackInformation.VictimAgent;
        AdimiToolsAiPerkEffects? attackingAi = AdimiToolsAiPerkEffects.ForCombatant(attacker);
        AdimiToolsAiPerkEffects? defendingAi = AdimiToolsAiPerkEffects.ForCombatant(victim);
        bool legacyMoloch = attackingAi == null && AdimiToolsAiArenaStrikeMagnitudeModel.HasMoloch(in attackInformation);
        if (attackingAi == null && defendingAi == null && !legacyMoloch)
        {
            return base.ApplyGeneralDamageModifiers(in attackInformation, in collisionData, baseDamage);
        }

        // Keep the native order and add the AI's perk terms alongside player terms.
        MPPerkObject.MPCombatPerkHandler? perks = MPPerkObject.GetCombatPerkHandler(attacker, victim);
        float damage = baseDamage;
        if (collisionData.AttackBlockedWithShield)
        {
            float ownBonus = attacker != null && victim != null && attacker != victim
                ? attackingAi?.ShieldDamage(attacker) ?? (legacyMoloch ? 0.25f : 0f)
                : 0f;
            float shield = 1f + ownBonus + (perks?.GetShieldDamage(collisionData.CorrectSideShieldBlock) ?? 0f)
                + (perks?.GetShieldDamageTaken(collisionData.CorrectSideShieldBlock) ?? 0f);
            damage = MathF.Max(0f, damage * shield);
        }

        if (perks != null || attackingAi != null || defendingAi != null)
        {
            bool wrongBone = MissionCombatMechanicsHelper.IsCollisionBoneDifferentThanWeaponAttachBone(in collisionData, attackInformation.WeaponAttachBoneIndex);
            MissionWeapon weapon = attackInformation.AttackerWeapon;
            DamageTypes type = ((weapon.IsEmpty | wrongBone) || collisionData.IsAlternativeAttack || collisionData.IsFallDamage || collisionData.IsHorseCharge) ? DamageTypes.Blunt : (DamageTypes)collisionData.DamageType;
            WeaponComponentData? usage = weapon.CurrentUsageItem;
            bool ranged = usage?.IsConsumable == true || usage?.IsRangedWeapon == true;
            float attackBonus = (perks?.GetDamage(usage, type, collisionData.IsAlternativeAttack) ?? 0f)
                + (attacker != null && victim != null ? attackingAi?.Damage(attacker, ranged, type) ?? 0f : 0f);
            if (victim?.IsMount == true)
            {
                attackBonus += attackingAi?.MountDamage(ranged) ?? 0f;
            }

            float defenceBonus = (perks?.GetDamageTaken(usage, type) ?? 0f)
                + (victim != null ? defendingAi?.DamageTaken(victim, ranged, type) ?? 0f : 0f);
            float factor = MathF.Max(0f, 1f + attackBonus + defenceBonus);
            if (attackInformation.IsHeadShot && ranged)
            {
                factor += perks?.GetRangedHeadShotDamage() ?? 0f;
            }

            damage *= factor;
        }

        return damage;
    }
}
