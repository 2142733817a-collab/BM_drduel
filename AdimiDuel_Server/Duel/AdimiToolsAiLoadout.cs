using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Diamond.Cosmetics;
using TaleWorlds.MountAndBlade.Diamond.Cosmetics.CosmeticTypes;
using TaleWorlds.ObjectSystem;

namespace AdimiDuel_Server.Duel;

internal sealed partial class AdimiToolsAiLoadout
{
    private sealed record Preset(string? Perk1, string? Perk2, string?[] Skins, (EquipmentIndex slot, string item)[] Overrides,
        string[]? Variants = null, float ExtraArmor = 0f, bool MountedArcherKeepsBow = false);

    private static readonly EquipmentIndex[] SkinSlots = { EquipmentIndex.Head, EquipmentIndex.Cape, EquipmentIndex.Body, EquipmentIndex.Gloves, EquipmentIndex.Leg };
    private static readonly EquipmentIndex[] WeaponSlots = { EquipmentIndex.Weapon0, EquipmentIndex.Weapon1, EquipmentIndex.Weapon2, EquipmentIndex.Weapon3 };
    private readonly string? _classId;

    private AdimiToolsAiLoadout(Equipment equipment, string[] perkIds, float? armor, float? health, float? baseHealth, bool mountChanged, string? classId = null)
    {
        Equipment = equipment;
        Perks = perkIds;
        Armor = armor;
        Health = health;
        BaseHealth = baseHealth;
        MountChanged = mountChanged;
        _classId = classId;
    }

    public Equipment Equipment { get; }
    public IReadOnlyList<string> Perks { get; }
    public float? Armor { get; }
    public float? Health { get; }
    public float? BaseHealth { get; }
    public bool MountChanged { get; }

    public static void Attach(Agent agent, AdimiToolsAiLoadout loadout)
    {
        if (loadout._classId is string classId)
        {
            AdimiToolsAiPerkEffects.Register(agent, classId, loadout.Perks.Contains("PerkThrowingSpear"));
        }
    }

    public static bool TryCreate(BasicCharacterObject character, Equipment existing, AdimiToolsAiArenaKind kind, int count,
        out IReadOnlyList<AdimiToolsAiLoadout>? loadouts, out string reason)
    {
        loadouts = null;
        reason = string.Empty;
        try
        {
            var heroClass = MultiplayerClassDivisions.GetMPHeroClassForCharacter(character);
            if (heroClass == null)
            {
                return true;
            }

            if (!Presets.TryGetValue(heroClass.StringId, out Preset? preset))
            {
                loadouts = CreateRandomSkins(heroClass.StringId, existing, count);
                return true;
            }

            bool spear = kind == AdimiToolsAiArenaKind.Spear;
            bool khuzaitSpear = spear && heroClass.StringId == "mp_heavy_infantry_khuzait";
            string?[] selected = { preset.Perk1, khuzaitSpear ? "PerkThrowingSpear" : preset.Perk2 };
            var perks = new List<IReadOnlyPerkObject>();
            List<(EquipmentIndex, EquipmentElement)>? replacements = null;
            for (int list = 0; list < selected.Length; list++)
            {
                if (selected[list] is not string perkId)
                {
                    continue;
                }

                IReadOnlyPerkObject? perk = heroClass.GetAllAvailablePerksForListIndex(list, "Duel")
                    .FirstOrDefault(candidate => candidate.IconId == perkId);
                if (perk == null)
                {
                    throw new InvalidOperationException($"Missing perk {perkId} for {heroClass.StringId} in list {list + 1}.");
                }

                perks.Add(perk);
                replacements = perk.GetAlternativeEquipments(false, true, replacements!);
            }

            Equipment standard = character.Equipment.Clone();
            if (replacements != null)
            {
                foreach (var (slot, item) in replacements)
                {
                    standard[slot] = item;
                }
            }

            if (khuzaitSpear)
            {
                EquipmentElement javelin = standard[EquipmentIndex.Weapon0];
                standard[EquipmentIndex.Weapon0] = standard[EquipmentIndex.Weapon1];
                standard[EquipmentIndex.Weapon1] = standard[EquipmentIndex.Weapon2];
                standard[EquipmentIndex.Weapon2] = javelin;
                standard[EquipmentIndex.Weapon3] = javelin;
            }
            else if (spear)
            {
                int free = Array.FindIndex(WeaponSlots, slot => standard[slot].IsEmpty);
                if (free >= 0)
                {
                    standard[WeaponSlots[free]] = standard[EquipmentIndex.Weapon0];
                    standard[EquipmentIndex.Weapon0] = existing[EquipmentIndex.Weapon0];
                }
                else
                {
                    throw new InvalidOperationException($"No free slot for the training spear on {heroClass.StringId}.");
                }
            }

            if (kind == AdimiToolsAiArenaKind.HorseArcher && preset.MountedArcherKeepsBow)
            {
                // Royal Archer needs the lance slot for its arrows.
                standard[EquipmentIndex.Weapon2] = existing[EquipmentIndex.Weapon2];
                standard[EquipmentIndex.Weapon3] = existing[EquipmentIndex.Weapon3];
            }

            foreach (var (slot, item) in preset.Overrides)
            {
                standard[slot] = ResolveItem(item);
            }

            if (kind is AdimiToolsAiArenaKind.Spear or AdimiToolsAiArenaKind.Archer)
            {
                standard[EquipmentIndex.Horse] = EquipmentElement.Invalid;
                standard[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
            }

            float armor = heroClass.ArmorValue + preset.ExtraArmor;
            float health = heroClass.Health;
            foreach (IReadOnlyPerkObject perk in perks)
            {
                armor += perk.GetDrivenPropertyBonusOnSpawn(false, true, DrivenProperty.ArmorHead, 0f);
                health += perk.GetHitpoints(false, true);
            }

            string[] perkIds = perks.Select(perk => perk.IconId).ToArray();
            int firstVariant = preset.Variants == null ? 0 : MBRandom.RandomInt(preset.Variants.Length);
            var results = new AdimiToolsAiLoadout[count];
            for (int i = 0; i < results.Length; i++)
            {
                Equipment equipment = standard.Clone();
                for (int slot = 0; slot < SkinSlots.Length; slot++)
                {
                    if (preset.Skins[slot] is string skin)
                    {
                        string[] choices = skin.Split('|');
                        string id = choices.Length == 1 ? choices[0] : choices[MBRandom.RandomInt(choices.Length)];
                        if (id.Length == 0)
                        {
                            equipment[SkinSlots[slot]] = EquipmentElement.Invalid;
                        }
                        else
                        {
                            ApplySkin(equipment, SkinSlots[slot], ResolveItem(id).Item!);
                        }
                    }
                }

                if (preset.Variants is string[] variants)
                {
                    int variant = i < variants.Length ? (firstVariant + i) % variants.Length : MBRandom.RandomInt(variants.Length);
                    equipment[EquipmentIndex.Weapon0] = ResolveItem(variants[variant]);
                }

                results[i] = new AdimiToolsAiLoadout(equipment, perkIds, armor, health, heroClass.Health,
                    equipment[EquipmentIndex.Horse].Item != existing[EquipmentIndex.Horse].Item, heroClass.StringId);
            }

            loadouts = results;
            return true;
        }
        catch (Exception exception)
        {
            Debug.Print($"[Adimi Tools]: AI loadout for {character.StringId} failed: {exception}");
            reason = "该兵种的 AI 配装数据不完整，本次挑战未开始。";
            return false;
        }
    }

    private static IReadOnlyList<AdimiToolsAiLoadout> CreateRandomSkins(string classId, Equipment existing, int count)
    {
        var candidates = new Dictionary<EquipmentIndex, ItemObject[]>();
        ClothingCosmeticElement[] skins = CosmeticsManager.CosmeticElementsList.OfType<ClothingCosmeticElement>().ToArray();
        foreach (EquipmentIndex slot in SkinSlots)
        {
            string? current = existing[slot].Item?.StringId;
            candidates[slot] = skins.Where(skin => current != null ? skin.ReplaceItemsId.Contains(current)
                    : skin.ReplaceItemless.Any(pair => pair.Item1 == classId
                        && Equipment.GetEquipmentIndexFromOldEquipmentIndexName(pair.Item2) == slot))
                .Select(skin => MBObjectManager.Instance.GetObject<ItemObject>(skin.Id)).OfType<ItemObject>().ToArray();
        }

        var results = new AdimiToolsAiLoadout[count];
        for (int i = 0; i < count; i++)
        {
            Equipment equipment = existing.Clone();
            foreach (var (slot, options) in candidates)
            {
                if (options.Length != 0)
                {
                    ApplySkin(equipment, slot, options[MBRandom.RandomInt(options.Length)]);
                }
            }

            results[i] = new AdimiToolsAiLoadout(equipment, Array.Empty<string>(), null, null, null, false);
        }

        return results;
    }

    private static void ApplySkin(Equipment equipment, EquipmentIndex slot, ItemObject cosmetic)
    {
        EquipmentElement element = equipment[slot];
        element.CosmeticItem = cosmetic;
        equipment[slot] = element;
    }

    private static EquipmentElement ResolveItem(string id)
    {
        if (id.Length == 0)
        {
            return EquipmentElement.Invalid;
        }

        ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(id)
            ?? throw new InvalidOperationException($"Missing equipment item {id}.");
        return new EquipmentElement(item);
    }
}
