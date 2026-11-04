using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsDuelAppearance
{
    public static void ApplyToBuild(AgentBuildData buildData, BasicCultureObject? culture)
    {
        if (culture == null)
        {
            return;
        }

        // Duel teams select opponents and arena flags, not a player's livery.
        // A weapon's explicit banner is sent by the native equipment packets.
        buildData.ClothingColor1(culture.Color)
            .ClothingColor2(culture.Color2)
            .Banner(CreateBanner(culture));
    }

    public static Banner? CreateBanner(BasicCultureObject? culture)
    {
        // Keep the selected country's symbol and palette. Do not share its
        // mutable Banner with agents or recolor a personal/arena banner.
        return culture?.Banner == null ? null : new Banner(culture.Banner);
    }
}
