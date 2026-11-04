using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using MathF = TaleWorlds.Library.MathF;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaStrikeMagnitudeModel : MultiplayerStrikeMagnitudeModel
{
    public override float CalculateStrikeMagnitudeForSwing(in AttackInformation attackInformation, in AttackCollisionData collisionData, in MissionWeapon weapon, float swingSpeed, float impactPoint, float extraLinearSpeed)
    {
        float speed = HasMoloch(in attackInformation) ? ApplySpeedBonus(extraLinearSpeed) : extraLinearSpeed;
        return base.CalculateStrikeMagnitudeForSwing(in attackInformation, in collisionData, in weapon, swingSpeed, impactPoint, speed);
    }

    public override float CalculateStrikeMagnitudeForThrust(in AttackInformation attackInformation, in AttackCollisionData collisionData, in MissionWeapon weapon, float thrustWeaponSpeed, float extraLinearSpeed, bool isThrown = false)
    {
        float speed = HasMoloch(in attackInformation) ? ApplySpeedBonus(extraLinearSpeed) : extraLinearSpeed;
        return base.CalculateStrikeMagnitudeForThrust(in attackInformation, in collisionData, in weapon, thrustWeaponSpeed, speed, isThrown);
    }

    public override float CalculateStrikeMagnitudeForMissile(in AttackInformation attackInformation, in AttackCollisionData collisionData, in MissionWeapon weapon, float missileSpeed)
    {
        float delta = missileSpeed - collisionData.MissileStartingBaseSpeed;
        if (HasMoloch(in attackInformation) && delta > 0f)
        {
            missileSpeed += ApplySpeedBonus(delta) - delta;
        }

        return base.CalculateStrikeMagnitudeForMissile(in attackInformation, in collisionData, in weapon, missileSpeed);
    }

    internal static bool HasMoloch(in AttackInformation attackInformation)
    {
        Agent? attacker = attackInformation.AttackerAgent;
        return attacker != null && attacker != attackInformation.VictimAgent && attacker.MountAgent == null
            && attacker.Origin is AdimiToolsAiArenaEnhancedOrigin { Difficulty: 3, HardProfile.UsesMoloch: true };
    }

    private static float ApplySpeedBonus(float speed)
    {
        // Exact native Moloch SpeedBonus conversion: only positive relative
        // speed is amplified, rather than multiplying all outgoing damage.
        return speed > 0f ? speed + speed * MathF.Max(MathF.Sqrt(1f + 0.4f) - 1f, 0f) : speed;
    }
}
