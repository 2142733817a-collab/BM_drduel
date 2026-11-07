using System;
using System.Collections.Generic;
using TaleWorlds.Core;

namespace AdimiDuel_Server.Duel;

internal sealed partial class AdimiToolsAiLoadout
{
    private static readonly IReadOnlyDictionary<string, Preset> Presets = new Dictionary<string, Preset>(StringComparer.Ordinal)
    {
        ["mp_heavy_infantry_empire"] = new(
            "PerkHoldTheLine", "PerkArmorUpgrade",
            new[] { "mp_empire_battle_crown_west", "mp_pauldron_cape_a", "mp_lamellar_with_scale_skirt", "mp_decorated_imperial_gauntlets", "mp_decorated_imperial_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_ranged_empire"] = new(
            "PerkToughness", "PerkPolearms",
            new[] { "mp_empire_lord_helmet", "mp_pauldron_cape_a", "mp_imperial_scale_armor", "mp_lamellar_plate_gauntlets", "mp_decorated_imperial_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_cavalry_empire"] = new(
            "PerkToughness", "PerkSpearhead",
            new[] { "mp_empire_battle_crown_north", "mp_varangian_bra_royal", "mp_lamellar_with_scale_skirt", "mp_decorated_imperial_gauntlets", "mp_decorated_imperial_boots" },
            new[] { (EquipmentIndex.Horse, "mp_empire_horse_war_agile") }),

        ["mp_heavy_infantry_vlandia"] = new(
            "PerkToughness", "PerkMeleeExpert",
            new[] { "mp_full_helm_over_padded_cap", "mp_pauldron_cape_b", "mp_white_coat_over_mail", "mp_reinforced_mail_mitten", "mp_plated_strip_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_ranged_vlandia"] = new(
            "PerkToughness", "PerkPaviseShield",
            new[] { "mp_kettle_hat_over_mail_coif", "mp_pauldron_cape_b", "mp_sturgian_fortified_armor", "mp_mail_mitten", "mp_mail_chausses" },
            new[] { (EquipmentIndex.Weapon0, "mp_heavier_crossbow") }),
        ["mp_heavy_cavalry_vlandia"] = new(
            "PerkToughness", "PerkShieldUpgrade",
            new[] { "mp_vlandian_faceguard_helmet_b", "mp_pauldron_cape_b", "mp_coat_of_plates_over_mail", "mp_reinforced_mail_mitten", "mp_strapped_mail_chausses" },
            new[] { (EquipmentIndex.Horse, "mp_vlandia_horse_war_faster") }),

        ["mp_shock_infantry_battania"] = new(
            "PerkToughness", "PerkBloodRage",
            new[] { "mp_bearhead", "mp_battania_cloak_furr", "mp_sleeveless_studded_fur_armor", "mp_guarded_armwraps", "mp_battania_woodland_boots" },
            Array.Empty<(EquipmentIndex, string)>(),
            Variants: new[] { "mp_battania_polearm_falx", "mp_battania_twohanded_hammer" }),
        ["mp_heavy_infantry_battania"] = new(
            "PerkToughness", "PerkMeleeExpert",
            new[] { "mp_battanian_noble_helmet_with_feather", "mp_battanian_chainmail_shoulder_b", "mp_battanian_scale_armor_a", "mp_battania_noble_bracers", "mp_rough_tied_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_ranged_battania"] = new(
            "PerkToughness", "PerkMarksman",
            new[] { "mp_battania_battle_crown", "mp_battania_cloak_furr", "mp_battania_noble_armor", "mp_battania_noble_bracers", "mp_rough_tied_boots" },
            new[]
            {
                (EquipmentIndex.Weapon0, "mp_yeow_long_bow"),
                (EquipmentIndex.Weapon2, "mp_battania_noble_two_handed_sword"),
            },
            ExtraArmor: 20f),
        ["mp_light_cavalry_battania"] = new(
            "PerkArmorUpgrade", "PerkLongSpear",
            new[] { "mp_bearhead", "mp_battania_civil_cape", "mp_northern_lamellar_armor", "mp_guarded_armwraps", "mp_battania_fur_boots" },
            new[]
            {
                (EquipmentIndex.Horse, "mp_battania_pony_agile"),
                // Half barding and the long spear share a perk slot. Equip the barding directly.
                (EquipmentIndex.HorseHarness, "mp_battania_horse_harness_halfscaled"),
            }),

        ["mp_shock_infantry_sturgia"] = new(
            "PerkToughness", "PerkShieldSplitterAxes",
            new[] { "mp_northern_fur_cap|mp_fur_hat", "mp_rough_bearskin", "mp_armored_baggy_trunks", string.Empty, "mp_battania_woodland_boots" },
            Array.Empty<(EquipmentIndex, string)>(),
            Variants: new[] { "mp_sturgia_long_twohanded_axe", "mp_sturgia_twohanded_hammer" }),
        ["mp_heavy_infantry_sturgia"] = new(
            "PerkArmorUpgrade", "PerkFightingMadness",
            new[] { "mp_nordic_helmet", "mp_rough_bearskin", "mp_northern_coat_of_plates", "mp_northern_plated_gloves", "mp_northern_plated_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_light_ranged_sturgia"] = new(
            "PerkLongBows", "PerkHorseHunters",
            new[] { "mp_nordic_fur_cap", string.Empty, "mp_northern_leather_tabard", "mp_buttoned_leather_bracers", "mp_battania_leather_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_cavalry_sturgia"] = new(
            "PerkFullBarding", "PerkShieldUpgrade",
            new[] { "mp_lendman_helmet_over_full_mail", "mp_rough_bearskin", "mp_sturgian_lamellar_fortified", "mp_northern_plated_gloves", "mp_northern_plated_boots" },
            new[] { (EquipmentIndex.Horse, "mp_sturgia_horse_agile") }),

        ["mp_heavy_infantry_khuzait"] = new(
            "PerkToughness", "PerkSteppeInfantry",
            new[] { "mp_plumed_lamellar_helmet", "mp_eastern_studded_shoulders", "mp_eastern_stitched_leather_coat", "mp_guarded_padded_vambrace", "mp_steppe_leather_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_ranged_khuzait"] = new(
            "PerkPolearms", "PerkMarksman",
            new string?[] { null, null, null, null, null },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_heavy_cavalry_khuzait"] = new(
            "PerkCouchedHeavySpears", "PerkAgileHorses",
            new[] { "mp_spiked_helmet_with_facemask", "mp_brass_lamellar_shoulder", "mp_brass_lamellar_over_mail", "mp_steppe_leather_vambraces", "mp_khuzait_curved_boots" },
            Array.Empty<(EquipmentIndex, string)>()),
        ["mp_horse_archer_khuzait"] = new(
            "PerkFasterHorses", "PerkMountedArchery",
            new[] { "mp_plumed_nomad_helmet", "mp_leather_lamellar_shoulders", "mp_leather_lamellar_armor", "mp_steppe_leather_vambraces", "mp_khuzait_curved_boots" },
            new[]
            {
                (EquipmentIndex.Weapon2, "mp_arrows_steppe_extra_mounted"),
                (EquipmentIndex.Weapon3, "mp_steppe_guardian_shield"),
            }),

        ["mp_shock_infantry_aserai"] = new(
            "PerkToughness", "PerkFightingMadness",
            new[] { "mp_emirs_helmet|mp_closed_desert_helmet_with_mail", "mp_desert_scale_shoulders", "mp_aserai_armor_02_b", "mp_plated_strip_gauntlets", "mp_plated_strip_boots" },
            Array.Empty<(EquipmentIndex, string)>(),
            Variants: new[] { "mp_aserai_strong_twohanded_axe", "mp_aserai_two_handed_sword" }),
        ["mp_heavy_ranged_aserai"] = new(
            "PerkToughness", "PerkMarksman",
            new[] { "mp_southern_lord_helmet", "mp_a_aserai_scale_b_shoulder_d", "mp_aserai_scale_armor_on_cloth", "mp_plated_strip_gauntlets", "mp_plated_strip_boots" },
            Array.Empty<(EquipmentIndex, string)>(),
            Variants: new[] { "mp_strong_recurve_bow", "mp_tribesman_long_bow" }),
        ["mp_heavy_cavalry_aserai"] = new(
            "PerkFullBarding", "PerkGoliath",
            new[] { "mp_aserai_battle_crown", "mp_wrapped_scarf", "mp_desert_scale_armor", "mp_plated_strip_gauntlets", "mp_plated_strip_boots" },
            new[] { (EquipmentIndex.Horse, "mp_aserai_horse_war_faster") },
            MountedArcherKeepsBow: true),
    };
}
