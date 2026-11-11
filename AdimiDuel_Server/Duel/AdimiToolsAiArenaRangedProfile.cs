using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaRangedProfile
{
    private static readonly (string culture, string classId)[] ArcherSelections =
    {
        ("vlandia", "mp_heavy_ranged_vlandia"),
        ("empire", "mp_heavy_ranged_empire"),
        ("khuzait", "mp_heavy_ranged_khuzait"),
        ("aserai", "mp_heavy_ranged_aserai"),
        ("sturgia", "mp_light_ranged_sturgia"),
        ("battania", "mp_heavy_ranged_battania"),
    };

    private static readonly (string culture, string classId)[] MountedSelections =
    {
        ("khuzait", "mp_horse_archer_khuzait"),
        ("aserai", "mp_heavy_cavalry_aserai"),
    };

    private AdimiToolsAiArenaRangedProfile(MultiplayerClassDivisions.MPHeroClass heroClass, Equipment equipment, bool mounted)
    {
        Culture = heroClass.Culture;
        Character = heroClass.HeroCharacter;
        Equipment = equipment;
        Mounted = mounted;
        HealthLimit = BaseHealthLimit = heroClass.Health;
        ArmorHead = ArmorTorso = ArmorArms = ArmorLegs = heroClass.ArmorValue;
        var troopName = Character.Name;
        TroopName = AdimiToolsMessageLanguage.NativeName(troopName.Value, troopName.ToString());
        if (!mounted && heroClass.StringId == "mp_heavy_ranged_battania")
        {
            BowSkillOverride = TwoHandedSkillOverride = 220;
        }
    }

    public BasicCultureObject Culture { get; }

    public BasicCharacterObject Character { get; }

    public Equipment Equipment { get; }

    public bool Mounted { get; }

    public int? BowSkillOverride { get; }

    public int? TwoHandedSkillOverride { get; }

    public float HealthLimit { get; }

    public float BaseHealthLimit { get; }

    public float ArmorHead { get; }

    public float ArmorTorso { get; }

    public float ArmorArms { get; }

    public float ArmorLegs { get; }

    public string TroopName { get; }

    public static bool TryCreateCatalog(bool mounted, out IReadOnlyList<AdimiToolsAiArenaRangedProfile>? profiles, out string reason)
    {
        profiles = null;
        reason = AdimiToolsMessageLanguage.Select("原版射手数据读取失败，本次挑战未开始。",
            "Could not read native archer data. The challenge was not started.");
        try
        {
            var classes = MultiplayerClassDivisions.GetMPHeroClasses().ToArray();
            var result = new List<AdimiToolsAiArenaRangedProfile>();
            foreach (var selection in mounted ? MountedSelections : ArcherSelections)
            {
                BasicCultureObject? culture = classes.Select(hero => hero.Culture)
                    .FirstOrDefault(candidate => candidate?.StringId == selection.culture);
                if (culture == null || !TryCreateForCulture(culture, mounted, out AdimiToolsAiArenaRangedProfile? profile, out reason))
                {
                    reason = AdimiToolsMessageLanguage.Select(
                        $"{selection.culture} 的原版{(mounted ? "骑射" : "顶级射手")}配置不可用，本次挑战未开始。",
                        $"The native {(mounted ? "horse archer" : "elite archer")} configuration for {char.ToUpperInvariant(selection.culture[0])}{selection.culture.Substring(1)} is unavailable. The challenge was not started.");
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
            Debug.Print($"[Adimi Tools]: Ranged AI catalog could not be created: {exception}");
            reason = AdimiToolsMessageLanguage.Select("原版射手数据读取失败，本次挑战未开始。",
                "Could not read native archer data. The challenge was not started.");
            return false;
        }
    }

    internal static bool TryCreateForCulture(BasicCultureObject culture, bool mounted, out AdimiToolsAiArenaRangedProfile? profile, out string reason)
    {
        profile = null;
        reason = AdimiToolsMessageLanguage.Select("所选阵营没有完整的原版射手配置。",
            "The selected faction has no complete native archer configuration.");
        try
        {
            var selection = (mounted ? MountedSelections : ArcherSelections)
                .FirstOrDefault(candidate => candidate.culture == culture.StringId);
            if (selection.classId == null)
            {
                return false;
            }

            var heroClass = MultiplayerClassDivisions.GetMPHeroClasses(culture)
                .FirstOrDefault(hero => hero.StringId == selection.classId && hero.HeroCharacter != null
                    && hero.HeroCharacter.StringId == selection.classId + "_hero");
            if (heroClass == null)
            {
                return false;
            }

            Equipment equipment = heroClass.HeroCharacter.Equipment.Clone();
            if (mounted && culture.StringId == "aserai")
            {
                // Mamluke becomes the stock mounted archer through Royal Archer.
                // Apply both slot replacements together, retaining its sword,
                // shield, warhorse and harness. No reactive perk bonus is added.
                IReadOnlyPerkObject? perk = Enumerable.Range(0, 3)
                    .SelectMany(index => heroClass.GetAllAvailablePerksForListIndex(index, "Duel"))
                    .FirstOrDefault(candidate => candidate.IconId == "PerkRecurveBows");
                var alternatives = perk?.GetAlternativeEquipments(false, true, null!);
                if (alternatives == null || !alternatives.Any(item => item.Item2.Item?.StringId == "mp_short_bow")
                    || !alternatives.Any(item => item.Item2.Item?.StringId == "mp_arrows_barbed_mounted"))
                {
                    reason = AdimiToolsMessageLanguage.Select("阿塞莱马穆鲁克的原版御用射手装备技能不可用，本次挑战未开始。",
                        "The native Aserai Mamluke's Royal Archer equipment perk is unavailable. The challenge was not started.");
                    return false;
                }

                foreach (var alternative in alternatives)
                {
                    equipment[alternative.Item1] = alternative.Item2;
                }
            }

            if (!mounted)
            {
                equipment[EquipmentIndex.Horse] = default;
                equipment[EquipmentIndex.HorseHarness] = default;
            }

            bool hasBow = false;
            bool hasCrossbow = false;
            bool hasArrows = false;
            bool hasBolts = false;
            for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                if (equipment[slot].IsEmpty)
                {
                    continue;
                }

                foreach (WeaponComponentData weapon in equipment[slot].Item.Weapons)
                {
                    hasBow |= weapon.WeaponClass == WeaponClass.Bow;
                    hasCrossbow |= weapon.WeaponClass == WeaponClass.Crossbow;
                    hasArrows |= weapon.WeaponClass == WeaponClass.Arrow && weapon.MaxDataValue > 1;
                    hasBolts |= weapon.WeaponClass == WeaponClass.Bolt && weapon.MaxDataValue > 1;
                }
            }

            if ((!hasBow && !hasCrossbow) || (hasBow && !hasArrows) || (hasCrossbow && !hasBolts)
                || (mounted && (!hasBow || equipment[EquipmentIndex.Horse].IsEmpty || equipment[EquipmentIndex.HorseHarness].IsEmpty)))
            {
                reason = AdimiToolsMessageLanguage.Select("原版射手的远程武器、弹药或坐骑不完整，本次挑战未开始。",
                    "The native archer's ranged weapon, ammunition, or mount is incomplete. The challenge was not started.");
                return false;
            }

            profile = new AdimiToolsAiArenaRangedProfile(heroClass, equipment, mounted);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Debug.Print($"[Adimi Tools]: Ranged AI profile could not be created: {exception}");
            reason = AdimiToolsMessageLanguage.Select("原版射手数据读取失败，本次挑战未开始。",
                "Could not read native archer data. The challenge was not started.");
            return false;
        }
    }
}
