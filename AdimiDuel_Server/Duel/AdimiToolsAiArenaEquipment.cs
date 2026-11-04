using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsAiArenaEquipment
{
    public static void Restore(Agent agent, Equipment snapshot, Banner? banner)
    {
        var failures = new List<Exception>();
        for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
        {
            try
            {
                EquipmentElement saved = snapshot[slot];
                if (saved.IsEmpty)
                {
                    if (!agent.Equipment[slot].IsEmpty)
                    {
                        agent.RemoveEquippedWeapon(slot);
                    }
                }
                else
                {
                    var weapon = new MissionWeapon(saved.Item, saved.ItemModifier, banner);
                    agent.EquipWeaponWithNewEntity(slot, ref weapon);
                }
            }
            catch (Exception exception)
            {
                // Give every independent slot a restoration attempt. The
                // caller retains the normal cleanup/retry and logging policy.
                failures.Add(exception);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("Could not restore every pre-challenge weapon slot.", failures);
        }

        // WieldInitialWeapons reads SpawnEquipment. Update only its weapon
        // metadata after native restoration succeeds, without changing shared
        // equipment, armor, mount slots, visuals, or the living agent itself.
        Equipment updated = agent.SpawnEquipment.Clone();
        for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
        {
            updated[slot] = snapshot[slot];
        }

        agent.InitializeSpawnEquipment(updated);
        agent.WieldInitialWeapons();
    }
}
