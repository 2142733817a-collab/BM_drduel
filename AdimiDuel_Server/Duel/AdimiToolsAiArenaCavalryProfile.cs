using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// A stock mounted class and its own native equipment. This profile only
// establishes the opponent's equipment and attributes; tactics are Native.
internal sealed class AdimiToolsAiArenaCavalryProfile
{
    private static readonly (string culture, string classId)[] Selections =
    {
        ("vlandia", "mp_heavy_cavalry_vlandia"),
        ("empire", "mp_heavy_cavalry_empire"),
        ("khuzait", "mp_heavy_cavalry_khuzait"),
        ("aserai", "mp_heavy_cavalry_aserai"),
        ("sturgia", "mp_heavy_cavalry_sturgia"),
        // Native multiplayer has no Battania heavy cavalry class.
        ("battania", "mp_light_cavalry_battania"),
    };

    private AdimiToolsAiArenaCavalryProfile(MultiplayerClassDivisions.MPHeroClass heroClass, Equipment equipment)
    {
        Culture = heroClass.Culture;
        Character = heroClass.HeroCharacter;
        Equipment = equipment;
        HealthLimit = BaseHealthLimit = heroClass.Health;
        ArmorHead = ArmorTorso = ArmorArms = ArmorLegs = heroClass.ArmorValue;
        TroopName = Character.Name.ToString();
    }

    public BasicCultureObject Culture { get; }
    public BasicCharacterObject Character { get; }
    public Equipment Equipment { get; }
    public float HealthLimit { get; }
    public float BaseHealthLimit { get; }
    public float ArmorHead { get; }
    public float ArmorTorso { get; }
    public float ArmorArms { get; }
    public float ArmorLegs { get; }
    public string TroopName { get; }

    public static bool TryCreateCatalog(out IReadOnlyList<AdimiToolsAiArenaCavalryProfile>? profiles, out string reason)
    {
        profiles = null;
        reason = "原版骑兵数据读取失败，本次挑战未开始。";
        try
        {
            var classes = MultiplayerClassDivisions.GetMPHeroClasses().ToArray();
            var result = new List<AdimiToolsAiArenaCavalryProfile>();
            foreach (var selection in Selections)
            {
                BasicCultureObject? culture = classes.Select(hero => hero.Culture)
                    .FirstOrDefault(candidate => candidate?.StringId == selection.culture);
                if (culture == null || !TryCreateForCulture(culture, out var profile, out reason))
                {
                    reason = $"{selection.culture} 的原版骑兵配置不可用，本次挑战未开始。";
                    return false;
                }

                result.Add(profile!);
            }

            profiles = result.AsReadOnly();
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Debug.Print($"[Adimi Tools]: Cavalry AI catalog could not be created: {exception}");
            return false;
        }
    }

    internal static bool TryCreateForCulture(BasicCultureObject culture, out AdimiToolsAiArenaCavalryProfile? profile, out string reason)
    {
        profile = null;
        reason = "所选阵营没有完整的原版持枪骑兵配置。";
        try
        {
            var selection = Selections.FirstOrDefault(candidate => candidate.culture == culture.StringId);
            if (selection.classId == null)
            {
                return false;
            }

            var hero = MultiplayerClassDivisions.GetMPHeroClasses(culture)
                .FirstOrDefault(candidate => candidate.StringId == selection.classId && candidate.HeroCharacter != null
                    && candidate.HeroCharacter.StringId == selection.classId + "_hero");
            if (hero == null)
            {
                return false;
            }

            Equipment equipment = hero.HeroCharacter.Equipment.Clone();
            if (equipment[EquipmentIndex.Horse].IsEmpty || equipment[EquipmentIndex.HorseHarness].IsEmpty)
            {
                return false;
            }

            EquipmentIndex lanceSlot = EquipmentIndex.None;
            for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                if (equipment[slot].Item?.Weapons.Any(weapon => !weapon.IsRangedWeapon
                    && weapon.WeaponClass is WeaponClass.OneHandedPolearm or WeaponClass.TwoHandedPolearm) == true)
                {
                    lanceSlot = slot;
                    break;
                }
            }

            if (lanceSlot == EquipmentIndex.None)
            {
                return false;
            }

            if (lanceSlot != EquipmentIndex.Weapon0)
            {
                EquipmentElement previous = equipment[EquipmentIndex.Weapon0];
                equipment[EquipmentIndex.Weapon0] = equipment[lanceSlot];
                equipment[lanceSlot] = previous;
            }

            profile = new AdimiToolsAiArenaCavalryProfile(hero, equipment);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Debug.Print($"[Adimi Tools]: Cavalry AI profile could not be created: {exception}");
            return false;
        }
    }
}
