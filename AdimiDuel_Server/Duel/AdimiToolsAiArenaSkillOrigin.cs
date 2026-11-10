using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Keep selected AI skill values private to this actor. The shared multiplayer
// character remains unchanged for players and every other training profile.
internal sealed class AdimiToolsAiArenaSkillOrigin : AdimiToolsAiArenaOrigin
{
    public AdimiToolsAiArenaSkillOrigin(BasicCharacterObject character, int? bowSkill, int? twoHandedSkill)
        : base(character)
    {
        if (bowSkill < 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(bowSkill));
        }

        if (twoHandedSkill < 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(twoHandedSkill));
        }

        BowSkill = bowSkill;
        TwoHandedSkill = twoHandedSkill;
    }

    public int? BowSkill { get; }

    public int? TwoHandedSkill { get; }

    public int? GetSkillOverride(SkillObject? skill)
    {
        if (skill == null)
        {
            return null;
        }

        return skill == DefaultSkills.Bow ? BowSkill
            : skill == DefaultSkills.TwoHanded ? TwoHandedSkill : null;
    }
}
