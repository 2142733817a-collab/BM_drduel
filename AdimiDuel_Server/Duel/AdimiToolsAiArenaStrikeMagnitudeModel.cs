using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using MathF = TaleWorlds.Library.MathF;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaStrikeMagnitudeModel : MultiplayerStrikeMagnitudeModel
{
    public override float CalculateStrikeMagnitudeForSwing(in AttackInformation attackInformation, in AttackCollisionData collisionData, in MissionWeapon weapon, float swingSpeed, float impactPoint, float extraLinearSpeed)
    {
        float speed = ApplySpeedBonus(extraLinearSpeed, GetAiSpeedBonus(in attackInformation));
        return base.CalculateStrikeMagnitudeForSwing(in attackInformation, in collisionData, in weapon, swingSpeed, impactPoint, speed);
    }

    public override float CalculateStrikeMagnitudeForThrust(in AttackInformation attackInformation, in AttackCollisionData collisionData, in MissionWeapon weapon, float thrustWeaponSpeed, float extraLinearSpeed, bool isThrown = false)
    {
        float speed = ApplySpeedBonus(extraLinearSpeed, GetAiSpeedBonus(in attackInformation));
        return base.CalculateStrikeMagnitudeForThrust(in attackInformation, in collisionData, in weapon, thrustWeaponSpeed, speed, isThrown);
    }

    public override float CalculateStrikeMagnitudeForMissile(in AttackInformation attackInformation, in AttackCollisionData collisionData, in MissionWeapon weapon, float missileSpeed)
    {
        float delta = missileSpeed - collisionData.MissileStartingBaseSpeed;
        if (delta > 0f)
        {
            missileSpeed += ApplySpeedBonus(delta, GetAiSpeedBonus(in attackInformation)) - delta;
        }

        return base.CalculateStrikeMagnitudeForMissile(in attackInformation, in collisionData, in weapon, missileSpeed);
    }

    internal static bool HasMoloch(in AttackInformation attackInformation)
    {
        Agent? attacker = attackInformation.AttackerAgent;
        return attacker != null && attacker != attackInformation.VictimAgent && attacker.MountAgent == null
            && attacker.Origin is AdimiToolsAiArenaEnhancedOrigin { Difficulty: 3, HardProfile.UsesMoloch: true };
    }

    private static float GetAiSpeedBonus(in AttackInformation attackInformation)
    {
        Agent? attacker = attackInformation.AttackerAgent;
        if (attacker == null || attacker == attackInformation.VictimAgent)
        {
            return 0f;
        }

        return AdimiToolsAiPerkEffects.Find(attacker)?.RelativeSpeedBonus(attacker)
            ?? (HasMoloch(in attackInformation) ? 0.4f : 0f);
    }

    private static float ApplySpeedBonus(float speed, float bonus)
    {
        // Exact native Moloch SpeedBonus conversion: only positive relative
        // speed is amplified, rather than multiplying all outgoing damage.
        return speed > 0f && bonus > 0f ? speed * MathF.Sqrt(1f + bonus) : speed;
    }
}
