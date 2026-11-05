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

        if (!AdimiToolsAiArenaStrikeMagnitudeModel.HasMoloch(in attackInformation))
        {
            return base.ApplyGeneralDamageModifiers(in attackInformation, in collisionData, baseDamage);
        }

        // The original MP model obtains attack perks from MissionPeer. These
        // independent AI have none. Add only their selected native Moloch
        // shield term, preserving additive defender bonuses and native order.
        MPPerkObject.MPCombatPerkHandler? perks = MPPerkObject.GetCombatPerkHandler(attackInformation.AttackerAgent, attackInformation.VictimAgent);
        float damage = baseDamage;
        if (collisionData.AttackBlockedWithShield)
        {
            float ownBonus = attackInformation.VictimAgent != null ? 0.25f : 0f;
            float shield = 1f + ownBonus + (perks?.GetShieldDamage(collisionData.CorrectSideShieldBlock) ?? 0f)
                + (perks?.GetShieldDamageTaken(collisionData.CorrectSideShieldBlock) ?? 0f);
            damage = MathF.Max(0f, damage * shield);
        }

        if (perks != null)
        {
            bool wrongBone = MissionCombatMechanicsHelper.IsCollisionBoneDifferentThanWeaponAttachBone(in collisionData, attackInformation.WeaponAttachBoneIndex);
            MissionWeapon weapon = attackInformation.AttackerWeapon;
            DamageTypes type = ((weapon.IsEmpty | wrongBone) || collisionData.IsAlternativeAttack || collisionData.IsFallDamage || collisionData.IsHorseCharge) ? DamageTypes.Blunt : (DamageTypes)collisionData.DamageType;
            float factor = MathF.Max(0f, 1f + perks.GetDamage(weapon.CurrentUsageItem, type, collisionData.IsAlternativeAttack) + perks.GetDamageTaken(weapon.CurrentUsageItem, type));
            if (attackInformation.IsHeadShot && weapon.CurrentUsageItem != null && (weapon.CurrentUsageItem.IsConsumable || weapon.CurrentUsageItem.IsRangedWeapon))
            {
                factor += perks.GetRangedHeadShotDamage();
            }

            damage *= factor;
        }

        return damage;
    }
}
