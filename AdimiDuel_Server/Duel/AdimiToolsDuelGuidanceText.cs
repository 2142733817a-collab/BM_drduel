namespace AdimiDuel_Server.Duel;

internal enum AdimiToolsDuelGuidanceKind
{
    Infantry,
    Ranged,
    Cavalry,
    Arena,
}

internal static class AdimiToolsDuelGuidanceText
{
    // This named style is shipped in the stock MP.ChatLog.Text brush. Unlike
    // DefaultStyle, it is not overwritten by the chat line's white base color.
    public const string HighlightStyle = "HeadshotTitle.Text";

    public static string Get(AdimiToolsDuelGuidanceKind kind)
        => System.Text.RegularExpressions.Regex.Replace(GetContent(kind), "【[^】\\r\\n]+】",
            title => $"<span style=\"{HighlightStyle}\">{title.Value}</span>");

    private static string GetContent(AdimiToolsDuelGuidanceKind kind) => kind switch
    {
        AdimiToolsDuelGuidanceKind.Ranged => "【射手区·北方】穿过隧道可选远程武器，也可经观众席传送门直达射手场地。\n"
            + "挥砍对应石像召唤 AI，敲击次数决定数量。\n"
            + "区域尚未完工，敬请期待。",
        AdimiToolsDuelGuidanceKind.Cavalry => "【骑兵区·南方】穿过大门，外侧所有开阔地均为骑兵区域。\n"
            + "挥砍对应石像召唤 AI，敲击次数决定数量。",
        AdimiToolsDuelGuidanceKind.Arena => "【场地规则】挥砍石像召唤 AI：次数决定数量（最多10），颜色区分难度。\n"
            + "【多人对战】双方站入 A/B 发光准备区；每队至少1人，人数可不等。\n"
            + "【赛制】武器旁的木桶可切换1局定胜负、3局2胜、5局3胜。\n"
            + "多人需全体参战者设置一致，否则1局定胜负。",
        _ => "【步兵区·中心】场内可自由换装；挥砍盾牌堆可切换阵营。\n"
            + "对局规则请前往任意场地查看。",
    };
}
