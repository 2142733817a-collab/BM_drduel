using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaSpearProfile
{
    private static readonly (string culture, string classId, string perkId, string spearId, string sidearmId)[] Selections =
    {
        ("vlandia", "mp_heavy_infantry_vlandia", "PerkHorseHunters", "mp_western_long_spear", "mp_vlandian_long_sword"),
        ("empire", "mp_heavy_infantry_empire", "PerkHorseHunters", "mp_western_long_spear", "mp_empire_spatha"),
        ("khuzait", "mp_heavy_infantry_khuzait", "PerkThrowingSpear", "mp_khuzait_throwing_spear", "mp_khuzait_sabre"),
        ("aserai", "mp_skirmisher_aserai", "PerkExtraTroop", "mp_aserai_spear", "mp_aserai_sword"),
        ("sturgia", "mp_heavy_infantry_sturgia", "PerkHorseHunters", "mp_sturgia_long_infantry_spear", "mp_sturgia_heavy_axe"),
        ("battania", "mp_heavy_infantry_battania", "PerkHorseHunters", "mp_battania_long_infantry_spear", "mp_battania_long_sword"),
    };

    private AdimiToolsAiArenaSpearProfile(MultiplayerClassDivisions.MPHeroClass heroClass, Equipment equipment)
    {
        Culture = heroClass.Culture;
        Character = heroClass.HeroCharacter;
        Equipment = equipment;
        HealthLimit = heroClass.Health;
        BaseHealthLimit = heroClass.Health;
        ArmorHead = ArmorTorso = ArmorArms = ArmorLegs = heroClass.ArmorValue;
        var troopName = Character.Name;
        TroopName = AdimiToolsMessageLanguage.NativeName(troopName.Value, troopName.ToString());
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

    public static bool TryCreateCatalog(out IReadOnlyList<AdimiToolsAiArenaSpearProfile>? profiles, out string reason)
    {
        profiles = null;
        try
        {
            var classes = MultiplayerClassDivisions.GetMPHeroClasses().ToArray();
            var result = new List<AdimiToolsAiArenaSpearProfile>(Selections.Length + 1);
            foreach (var selection in Selections)
            {
                BasicCultureObject? culture = classes.Select(hero => hero.Culture)
                    .FirstOrDefault(candidate => candidate?.StringId == selection.culture);
                if (culture == null || !TryCreateForCulture(culture, out AdimiToolsAiArenaSpearProfile? profile, out reason))
                {
                    reason = AdimiToolsMessageLanguage.Select(
                        $"{selection.culture} 的原版持矛步兵数据不可用，本次挑战未开始。",
                        $"Native spear infantry data for {char.ToUpperInvariant(selection.culture[0])}{selection.culture.Substring(1)} is unavailable. The challenge was not started.");
                    return false;
                }

                result.Add(profile!);
            }

            AdimiToolsAiArenaSpearProfile? voulgier = CreateVlandianVoulgier(result.First(profile => profile.Culture.StringId == "vlandia"));
            if (voulgier == null)
            {
                reason = AdimiToolsMessageLanguage.Select("瓦兰迪亚的原版斧枪兵数据不可用，本次挑战未开始。",
                    "Native Vlandian Voulgier data is unavailable. The challenge was not started.");
                return false;
            }

            result.Add(voulgier);
            profiles = result.AsReadOnly();
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Debug.Print($"[Adimi Tools]: Spear AI catalog could not be created: {exception}");
            reason = AdimiToolsMessageLanguage.Select("持矛步兵的原版数据读取失败，本次挑战未开始。",
                "Could not read native spear infantry data. The challenge was not started.");
            return false;
        }
    }

    internal static bool TryCreateForCulture(BasicCultureObject culture, out AdimiToolsAiArenaSpearProfile? profile, out string reason)
    {
        profile = null;
        reason = AdimiToolsMessageLanguage.Select("所选阵营没有完整的原版持矛步兵配置。",
            "The selected faction has no complete native spear infantry configuration.");
        try
        {
            var selection = Selections.FirstOrDefault(candidate => candidate.culture == culture.StringId);
            if (selection.classId == null)
            {
                return false;
            }

            var heroClass = MultiplayerClassDivisions.GetMPHeroClasses(culture)
                .FirstOrDefault(hero => hero.StringId == selection.classId && hero.HeroCharacter != null
                    && (hero.HeroCharacter.IsInfantry || selection.culture == "aserai")
                    && hero.HeroCharacter.StringId == selection.classId + "_hero");
            if (heroClass == null)
            {
                return false;
            }

            IReadOnlyPerkObject? spearPerk = Enumerable.Range(0, 3)
                .SelectMany(index => heroClass.GetAllAvailablePerksForListIndex(index, "Duel"))
                .FirstOrDefault(perk => perk.IconId == selection.perkId);
            var alternatives = spearPerk?.GetAlternativeEquipments(false, true, null!);
            EquipmentElement spear = alternatives?.Select(alternative => alternative.Item2)
                .FirstOrDefault(element => element.Item?.StringId == selection.spearId) ?? default;
            bool throwing = selection.culture == "khuzait";
            bool validWeapon = spear.Item != null && (throwing
                ? spear.Item.Weapons.Any(weapon => weapon.IsRangedWeapon && weapon.WeaponClass == WeaponClass.Javelin)
                : spear.Item.Weapons.Any(weapon => !weapon.IsRangedWeapon
                    && weapon.WeaponClass is WeaponClass.OneHandedPolearm or WeaponClass.TwoHandedPolearm));
            if (!validWeapon)
            {
                reason = AdimiToolsMessageLanguage.Select("所选阵营的原版步战矛不可用，本次挑战未开始。",
                    "The selected faction's native infantry spear is unavailable. The challenge was not started.");
                return false;
            }

            Equipment equipment = heroClass.HeroCharacter.Equipment.Clone();
            if (selection.culture == "aserai")
            {
                // The native skirmisher's Shield and Spear perk preserves its
                // jereed and sword, and supplies the spear and stronger shield.
                foreach (var alternative in alternatives!)
                {
                    equipment[alternative.Item1] = alternative.Item2;
                }

                for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
                {
                    if (equipment[slot].Item?.Weapons.Any(weapon => weapon.WeaponClass is WeaponClass.Bow
                        or WeaponClass.Crossbow or WeaponClass.Arrow or WeaponClass.Bolt) == true)
                    {
                        reason = AdimiToolsMessageLanguage.Select("阿塞莱散兵配置包含弓弩装备，本次挑战未开始。",
                            "The Aserai skirmisher configuration contains a bow or crossbow. The challenge was not started.");
                        return false;
                    }
                }

                equipment[EquipmentIndex.Horse] = default;
                equipment[EquipmentIndex.HorseHarness] = default;
                profile = new AdimiToolsAiArenaSpearProfile(heroClass, equipment);
                reason = string.Empty;
                return true;
            }

            EquipmentElement shield = default;
            EquipmentElement sidearm = default;
            for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                if (shield.IsEmpty && equipment[slot].Item?.ItemType == ItemObject.ItemTypeEnum.Shield)
                {
                    shield = equipment[slot];
                }

                if (equipment[slot].Item?.StringId == selection.sidearmId)
                {
                    sidearm = equipment[slot];
                }

                equipment[slot] = default;
            }

            if (sidearm.IsEmpty)
            {
                reason = AdimiToolsMessageLanguage.Select("所选阵营的原版近战副武器不可用，本次挑战未开始。",
                    "The selected faction's native melee sidearm is unavailable. The challenge was not started.");
                return false;
            }

            // Keep the selected spear in the first slot and retain the
            // native shield and sidearm from this troop's own loadout.
            equipment[EquipmentIndex.Weapon0] = spear;
            equipment[EquipmentIndex.Weapon1] = shield;
            equipment[EquipmentIndex.Weapon2] = sidearm;
            equipment[EquipmentIndex.Horse] = default;
            equipment[EquipmentIndex.HorseHarness] = default;
            profile = new AdimiToolsAiArenaSpearProfile(heroClass, equipment);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Debug.Print($"[Adimi Tools]: Spear AI profile could not be created: {exception}");
            reason = AdimiToolsMessageLanguage.Select("持矛步兵的原版数据读取失败，本次挑战未开始。",
                "Could not read native spear infantry data. The challenge was not started.");
            return false;
        }
    }

    private static AdimiToolsAiArenaSpearProfile? CreateVlandianVoulgier(AdimiToolsAiArenaSpearProfile spearman)
    {
        const string classId = "mp_shock_infantry_vlandia";
        var heroClass = MultiplayerClassDivisions.GetMPHeroClasses(spearman.Culture)
            .FirstOrDefault(hero => hero.StringId == classId && hero.HeroCharacter != null
                && hero.HeroCharacter.IsInfantry && hero.HeroCharacter.StringId == classId + "_hero");
        if (heroClass == null)
        {
            return null;
        }

        Equipment equipment = heroClass.HeroCharacter.Equipment.Clone();
        ItemObject? voulge = equipment[EquipmentIndex.Weapon0].Item;
        if (voulge?.StringId != "mp_vlandian_polearm"
            || !voulge.Weapons.Any(weapon => !weapon.IsRangedWeapon && weapon.WeaponClass == WeaponClass.TwoHandedPolearm))
        {
            return null;
        }

        equipment[EquipmentIndex.Weapon2] = spearman.Equipment[EquipmentIndex.Weapon2];
        equipment[EquipmentIndex.Horse] = default;
        equipment[EquipmentIndex.HorseHarness] = default;
        return new AdimiToolsAiArenaSpearProfile(heroClass, equipment);
    }
}
