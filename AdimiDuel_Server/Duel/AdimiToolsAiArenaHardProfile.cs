using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal sealed class AdimiToolsAiArenaHardProfile
{
    private static readonly string[] StandardCultures = { "vlandia", "empire", "khuzait", "aserai", "sturgia", "battania" };

    private AdimiToolsAiArenaHardProfile(MultiplayerClassDivisions.MPHeroClass heroClass, IReadOnlyPerkObject selectedPerk, bool usesMoloch)
    {
        Culture = heroClass.Culture;
        Character = heroClass.HeroCharacter;
        Equipment = Character.Equipment.Clone();
        List<(EquipmentIndex, EquipmentElement)>? alternatives = selectedPerk.GetAlternativeEquipments(false, true, null!);
        if (alternatives != null)
        {
            foreach ((EquipmentIndex index, EquipmentElement item) in alternatives)
            {
                Equipment[index] = item;
            }
        }

        Equipment[EquipmentIndex.Horse] = default;
        Equipment[EquipmentIndex.HorseHarness] = default;
        BaseHealthLimit = heroClass.Health * 2f;
        HealthLimit = (heroClass.Health + selectedPerk.GetHitpoints(false, true)) * 2f;
        ArmorHead = ArmorAfterBonus(heroClass, selectedPerk, DrivenProperty.ArmorHead);
        ArmorTorso = ArmorAfterBonus(heroClass, selectedPerk, DrivenProperty.ArmorTorso);
        ArmorArms = ArmorAfterBonus(heroClass, selectedPerk, DrivenProperty.ArmorArms);
        ArmorLegs = ArmorAfterBonus(heroClass, selectedPerk, DrivenProperty.ArmorLegs);
        TroopName = Character.Name.ToString();
        TroopCost = heroClass.TroopCost;
        UsesMoloch = usesMoloch;
    }

    public BasicCultureObject Culture { get; }

    public BasicCharacterObject Character { get; }

    public Equipment Equipment { get; }

    // Both values already include the difficulty's one-time health multiplier.
    public float HealthLimit { get; }

    public float BaseHealthLimit { get; }

    public float ArmorHead { get; }

    public float ArmorTorso { get; }

    public float ArmorArms { get; }

    public float ArmorLegs { get; }

    public string TroopName { get; }

    public int TroopCost { get; }

    public bool UsesMoloch { get; }

    public static bool TryCreate(MissionPeer owner, out AdimiToolsAiArenaHardProfile? profile, out string reason)
    {
        try
        {
            string selected = StandardCultures[MBRandom.RandomInt(StandardCultures.Length)];
            BasicCultureObject? culture = MultiplayerClassDivisions.GetMPHeroClasses()
                .Select(x => x.Culture).FirstOrDefault(x => x?.StringId == selected);
            if (culture == null)
            {
                profile = null;
                reason = "随机选择的原版阵营数据不可用，无法生成困难 AI。";
                return false;
            }

            return TryCreateForCulture(culture, out profile, out reason);
        }
        catch (System.Exception exception)
        {
            Debug.Print($"[Adimi Tools]: Hard AI profile could not be created: {exception}");
            profile = null;
            reason = "困难 AI 的原版兵种数据读取失败，本次挑战未开始。";
            return false;
        }
    }

    internal static bool TryCreateForCulture(BasicCultureObject culture, out AdimiToolsAiArenaHardProfile? profile, out string reason)
    {
        profile = null;
        bool aserai = culture.StringId == "aserai";
        // Native Aserai has no Infantry_Heavy entry; its explicit original Guard
        // override uses Moloch, while the other five use their armor perk.
        IEnumerable<MultiplayerClassDivisions.MPHeroClass> classes = MultiplayerClassDivisions.GetMPHeroClasses(culture)
            .Where(x => x.HeroCharacter != null && x.HeroCharacter.IsInfantry)
            .OrderByDescending(x => x.IconType == TargetIconType.Infantry_Heavy)
            .ThenByDescending(x => x.ArmorValue)
            .ThenBy(x => x.StringId, System.StringComparer.Ordinal);
        MultiplayerClassDivisions.MPHeroClass? heroClass = aserai
            ? classes.FirstOrDefault(x => x.HeroCharacter.StringId == "mp_shock_infantry_aserai_hero")
            : classes.FirstOrDefault();
        if (heroClass == null)
        {
            reason = "所选阵营没有可用的原版近战步兵，无法生成困难 AI。";
            return false;
        }

        IReadOnlyPerkObject? selectedPerk = aserai
            ? Enumerable.Range(0, 3).SelectMany(i => heroClass.GetAllAvailablePerksForListIndex(i, "Duel"))
                .FirstOrDefault(x => x.IconId == "PerkFightingMadness")
            : FindArmorPerk(heroClass);
        if (selectedPerk == null)
        {
            reason = aserai ? "阿塞莱宫廷卫士的战场魔神技能不可用，无法生成困难 AI。" : "所选阵营的原版步兵没有可用的加强护甲技能，无法生成困难 AI。";
            return false;
        }

        profile = new AdimiToolsAiArenaHardProfile(heroClass, selectedPerk, aserai);
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyPerkObject? FindArmorPerk(MultiplayerClassDivisions.MPHeroClass heroClass)
    {
        IReadOnlyPerkObject? best = null;
        float bestBonus = 0f;
        for (int list = 0; list < 3; list++)
        {
            foreach (IReadOnlyPerkObject perk in heroClass.GetAllAvailablePerksForListIndex(list, "Duel"))
            {
                float bonus = perk.GetDrivenPropertyBonusOnSpawn(false, true, DrivenProperty.ArmorHead, 0f)
                    + perk.GetDrivenPropertyBonusOnSpawn(false, true, DrivenProperty.ArmorTorso, 0f)
                    + perk.GetDrivenPropertyBonusOnSpawn(false, true, DrivenProperty.ArmorArms, 0f)
                    + perk.GetDrivenPropertyBonusOnSpawn(false, true, DrivenProperty.ArmorLegs, 0f);
                if (bonus > bestBonus)
                {
                    best = perk;
                    bestBonus = bonus;
                }
            }
        }

        return best;
    }

    private static float ArmorAfterBonus(MultiplayerClassDivisions.MPHeroClass heroClass, IReadOnlyPerkObject armorPerk, DrivenProperty property)
    {
        // The native MP model starts these properties at zero, then adds class
        // armor and the selected spawn perk. Equipment armor is cosmetic in MP.
        float original = heroClass.ArmorValue + armorPerk.GetDrivenPropertyBonusOnSpawn(false, true, property, 0f);
        return original + (original > 50f ? 15f : 30f);
    }
}
