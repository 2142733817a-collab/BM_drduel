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
    private const string CultureSwitchHint = "前往中央步兵区，挥砍盾牌堆可切换阵营。";

    public static string Get(AdimiToolsDuelGuidanceKind kind)
        => System.Text.RegularExpressions.Regex.Replace(GetContent(kind), "【[^】\\r\\n]+】",
            title => $"<span style=\"{HighlightStyle}\">{title.Value}</span>");

    public static string GetModeSelection(string modeName)
    {
        string highlightedMode = System.Text.RegularExpressions.Regex.Replace(modeName, "[0-9]+",
            number => $"<span style=\"{HighlightStyle}\">{number.Value}</span>");
        return $"<span style=\"{HighlightStyle}\">【切换对局】</span>{highlightedMode}，1v1或多v多时，双方对局数须保持一致。";
    }

    public static string GetModeMismatch(string firstName, string firstMode, string secondName, string secondMode)
        => $"{Highlight("【赛制不一致】")}{Highlight(firstName)}，{Highlight(firstMode)}。"
            + $"{Highlight(secondName)}，{Highlight(secondMode)}。请前往木桶挥砍或输入：{GetModeCommands()}。";

    public static string GetModeUsage()
        => $"请挥砍木桶或输入：{GetModeCommands()}。";

    public static string GetAiSpawn(int count, int difficulty, bool spearInfantry, int kind = 0)
    {
        string? independentTroop = spearInfantry ? "持矛步兵" : kind == 2 ? "顶级射手" : kind == 3 ? "骑射手" : null;
        string details = independentTroop != null
            ? $"{independentTroop}：本场同一随机国家，{Highlight("继续挥砍可增加人数（最多10）")}。"
            : difficulty switch
            {
                2 => "一般 AI：与你当前兵种、装备相同，熟练度 +50，生命上限翻倍。",
                3 => "困难 AI：六国随机的固定步兵，熟练度 330，生命上限翻倍并加强护甲。",
                _ => "简单 AI：与你当前兵种、装备相同。",
            };
        return $"{Highlight("【生成AI】")}3秒后生成{Highlight(count.ToString(System.Globalization.CultureInfo.InvariantCulture))}名{details}";
    }

    private static string GetModeCommands()
        => $"{Highlight("!1")}、{Highlight("!3.2")}、{Highlight("!5.3")}";

    private static string Highlight(string text)
        => $"<span style=\"{HighlightStyle}\">{text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}</span>";

    private static string GetContent(AdimiToolsDuelGuidanceKind kind) => kind switch
    {
        AdimiToolsDuelGuidanceKind.Ranged => "【射手区·北方】穿过隧道可选远程武器，也可经观众席传送门直达射手场地。\n"
            + "挥砍对应石像召唤 AI，敲击次数决定数量。\n"
            + CultureSwitchHint + "\n"
            + "区域尚未完工，敬请期待。",
        AdimiToolsDuelGuidanceKind.Cavalry => "【骑兵区·南方】穿过大门，外侧所有开阔地均为骑兵区域。\n"
            + "挥砍对应石像召唤 AI，敲击次数决定数量。\n"
            + CultureSwitchHint,
        AdimiToolsDuelGuidanceKind.Arena => "【场地规则】挥砍石像召唤 AI：次数决定数量（最多10），颜色区分难度。\n"
            + "【多人对战】双方站入 A/B 发光准备区；每队至少1人，人数可不等。\n"
            + "【赛制】武器旁的木桶可切换1局定胜负、3局2胜、5局3胜。\n"
            + "多人需全体参战者设置一致，否则1局定胜负。",
        _ => "【步兵区·中心】场内可自由换装；挥砍盾牌堆可切换阵营。\n"
            + "对局规则请前往任意场地查看。",
    };
}
