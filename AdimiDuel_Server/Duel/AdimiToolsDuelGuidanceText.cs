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
        string highlightedMode = HighlightNumbers(FormatMode(modeName));
        if (AdimiToolsMessageLanguage.IsEnglish)
        {
            return $"{Highlight("【Switch Match】")}\nSwitched to {highlightedMode}.\nAll players in 1v1 or team duels must use the same format.";
        }

        return $"{Highlight("【切换对局】")}\n已切换为 {highlightedMode}。\n1v1 或多 v 多时，参战者的赛制须一致。";
    }

    // Team arenas own one shared series, so their notice must not imply that
    // each participant's individual duel selection is consulted.
    public static string GetTeamModeSelection(string modeName)
        => AdimiToolsMessageLanguage.IsEnglish
            ? $"{Highlight("【Switch Match】")}\nThis match is now {HighlightNumbers(FormatMode(modeName))}."
            : $"{Highlight("【切换对局】")}\n此对局赛制已切换为 {HighlightNumbers(FormatMode(modeName))}。";

    public static string GetModeFailure(string message)
        => $"{Highlight(AdimiToolsMessageLanguage.Select("【切换对局】", "【Switch Match】"))}\n{Escape(message)}";

    public static string GetFriendlyFireNotice(string message)
        => $"{Highlight(AdimiToolsMessageLanguage.Select("【切换队友伤害】", "【Friendly Fire】"))}\n{Escape(message)}";

    public static string GetAiSwitchNotice(string message)
        => $"{Highlight(AdimiToolsMessageLanguage.Select("【切换 AI】", "【Switch AI】"))}\n{Escape(message)}";

    public static string GetModeMismatch(string firstName, string firstMode, string secondName, string secondMode)
        => AdimiToolsMessageLanguage.IsEnglish
            ? $"{Highlight("【Formats Differ】")}\n{Escape(firstName)}: {HighlightNumbers(FormatMode(firstMode))}.\n"
                + $"{Escape(secondName)}: {HighlightNumbers(FormatMode(secondMode))}.\n{GetModeSwitchInstruction()}"
            : $"{Highlight("【赛制不一致】")}\n{Escape(firstName)}，{HighlightNumbers(FormatMode(firstMode))}。\n"
                + $"{Escape(secondName)}，{HighlightNumbers(FormatMode(secondMode))}。\n{GetModeSwitchInstruction()}";

    public static string GetModeUsage()
        => $"{Highlight(AdimiToolsMessageLanguage.Select("【赛制用法】", "【Match Formats】"))}\n{GetModeSwitchInstruction()}";

    public static string GetCurrentMode(string modeName)
        => AdimiToolsMessageLanguage.IsEnglish
            ? $"{Highlight("【Current Format】")}\nCurrent format: {HighlightNumbers(FormatMode(modeName))}.\n{GetModeSwitchInstruction()}"
            : $"{Highlight("【当前赛制】")}\n当前为 {HighlightNumbers(FormatMode(modeName))}。\n{GetModeSwitchInstruction()}";

    public static string GetTeamCountdown(int sideACount, int sideBCount, int seconds)
        => AdimiToolsMessageLanguage.IsEnglish
            ? $"Team A {Highlight(sideACount.ToString(System.Globalization.CultureInfo.InvariantCulture))} / "
                + $"Team B {Highlight(sideBCount.ToString(System.Globalization.CultureInfo.InvariantCulture))}: "
                + $"clearing the arena and raising barriers in {Highlight(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))}s; entering or leaving resets the timer."
            : $"A 队 {Highlight(sideACount.ToString(System.Globalization.CultureInfo.InvariantCulture))} 人 / "
                + $"B 队 {Highlight(sideBCount.ToString(System.Globalization.CultureInfo.InvariantCulture))} 人："
                + $"{Highlight(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))} 秒后清场并升起屏障；进出准备区会重新计时。";

    public static string GetAiSpawn(int count, int difficulty, bool spearInfantry, int kind = 0)
    {
        if (AdimiToolsMessageLanguage.IsEnglish)
        {
            return GetEnglishAiSpawn(count, difficulty, spearInfantry, kind);
        }

        string? independentTroop = spearInfantry ? "持矛步兵"
            : kind == 2 ? (difficulty == 3 ? "困难射手" : "顶级射手")
            : kind == 3 ? "骑射手" : kind == 4 ? "困难骑兵" : null;
        string troop = independentTroop ?? difficulty switch
        {
            2 => "一般 AI",
            3 => "困难 AI",
            _ => "简单 AI",
        };
        string details = independentTroop != null
            ? $"本场 AI 来自同一个随机阵营。{(difficulty == 3 ? (kind == 2 ? "\n生命上限翻倍。" : "\n熟练度 330，生命上限翻倍。") : string.Empty)}"
            : difficulty switch
            {
                2 => "兵种、装备与你当前相同。\n熟练度 +50，生命上限翻倍。",
                3 => "六国随机同阵营步兵，持巨剑。\n熟练度 330，生命上限翻倍并加强护甲。",
                _ => "兵种、装备与你当前相同。",
            };
        return $"{Highlight("【生成 AI】")}\n3 秒后生成 {Highlight(count.ToString(System.Globalization.CultureInfo.InvariantCulture))} 名{troop}。\n{details}\n"
            + $"{Highlight("继续挥砍可增加人数（最多 10）")}。";
    }

    public static string GetAiSpawnLimit(int maximum)
        => AdimiToolsMessageLanguage.IsEnglish
            ? $"{Highlight("【Summon AI】")}\nYou can summon at most {Highlight(maximum.ToString(System.Globalization.CultureInfo.InvariantCulture))} AI at once."
            : $"{Highlight("【生成 AI】")}\n一次最多生成 {Highlight(maximum.ToString(System.Globalization.CultureInfo.InvariantCulture))} 个 AI。";

    internal static string Highlight(string text)
        => $"<span style=\"{HighlightStyle}\">{Escape(text)}</span>";

    internal static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string HighlightNumbers(string text)
        => System.Text.RegularExpressions.Regex.Replace(Escape(text), "[0-9]+",
            number => $"<span style=\"{HighlightStyle}\">{number.Value}</span>");

    private static string GetModeSwitchInstruction()
        => AdimiToolsMessageLanguage.Select("挥砍木桶，或输入 !1、!3.2、!5.3 切换。",
            "Hit the barrel or type !1, !3.2 or !5.3.");

    private static string GetEnglishAiSpawn(int count, int difficulty, bool spearInfantry, int kind)
    {
        bool single = count == 1;
        string? independentTroop = spearInfantry ? (single ? "spearman" : "spearmen")
            : kind == 2 ? (difficulty == 3
                ? (single ? "hard archer" : "hard archers")
                : (single ? "elite archer" : "elite archers"))
            : kind == 3 ? (single ? "horse archer" : "horse archers")
            : kind == 4 ? (single ? "hard cavalry rider" : "hard cavalry riders") : null;
        string troop = independentTroop ?? difficulty switch
        {
            2 => "normal AI",
            3 => "hard AI",
            _ => "easy AI",
        };
        string details = independentTroop != null
            ? $"All AI use the same randomly chosen faction.{(difficulty == 3 ? (kind == 2 ? "\nDouble health." : "\nSkill 330; double health.") : string.Empty)}"
            : difficulty switch
            {
                2 => "Same troop and equipment as you.\nSkill +50; double health.",
                3 => "Same random faction from six; greatsword infantry.\nSkill 330; double health and improved armor.",
                _ => "Same troop and equipment as you.",
            };
        string verb = single ? "spawns" : "spawn";
        return $"{Highlight("【Summon AI】")}\n3 seconds until {Highlight(count.ToString(System.Globalization.CultureInfo.InvariantCulture))} {troop} {verb}.\n{details}\n"
            + $"{Highlight("Strike again to add AI (max 10)")}.";
    }

    private static string FormatMode(string modeName) => modeName switch
    {
        // Compatibility for callers holding an older Chinese display label.
        // Actual match selection always uses its numeric limit, never text.
        "1局定胜负" when AdimiToolsMessageLanguage.IsEnglish => AdimiToolsDuelModes.GetModeName(1),
        "3局2胜" when AdimiToolsMessageLanguage.IsEnglish => AdimiToolsDuelModes.GetModeName(2),
        "5局3胜" when AdimiToolsMessageLanguage.IsEnglish => AdimiToolsDuelModes.GetModeName(3),
        "1局定胜负" => "1 局定胜负",
        "3局2胜" => "3 局 2 胜",
        "5局3胜" => "5 局 3 胜",
        _ => modeName,
    };

    private static string GetContent(AdimiToolsDuelGuidanceKind kind) => AdimiToolsMessageLanguage.IsEnglish
        ? GetEnglishContent(kind)
        : kind switch
    {
        AdimiToolsDuelGuidanceKind.Ranged => "【射手区·北方】\n穿过隧道可选取远程武器。\n"
            + "也可经观众席传送门直达射手场地。\n"
            + "挥砍对应石像召唤 AI，敲击次数决定数量。\n"
            + CultureSwitchHint + "\n"
            + "区域尚未完工，敬请期待。",
        AdimiToolsDuelGuidanceKind.Cavalry => "【骑兵区·南方】\n穿过大门，外侧开阔地为骑兵区。\n"
            + "挥砍对应石像召唤 AI，敲击次数决定数量。\n"
            + CultureSwitchHint,
        AdimiToolsDuelGuidanceKind.Arena => "【场地规则】挥砍石像召唤 AI：\n次数决定数量（最多 10），颜色区分难度。\n"
            + "【多人对战】\n双方站入 A/B 发光准备区。\n每队至少 1 人，两队人数可以不同。\n"
            + "【赛制】\n木桶可以切换1局定胜负、3局2胜、5局3胜。\n"
            + "多人场地默认1局定胜负，砍场内木桶可切换本场赛制。",
        _ => $"【步兵区·中心】\n挥砍{Highlight("盾牌堆")}可切换阵营。\n"
            + "对局规则可前往任意场地查看。",
    };

    private static string GetEnglishContent(AdimiToolsDuelGuidanceKind kind) => kind switch
    {
        AdimiToolsDuelGuidanceKind.Ranged => "【Archer Area · North】\nGo through the tunnel for ranged weapons.\n"
            + "Or use the spectator portal to reach the archery range.\n"
            + "Strike the matching statue to summon AI; each hit adds one.\n"
            + "Go to the central infantry area and strike the shield pile to switch factions.\n"
            + "This area is still under construction.",
        AdimiToolsDuelGuidanceKind.Cavalry => "【Cavalry Area · South】\nGo through the gate to the open cavalry area.\n"
            + "Strike the matching statue to summon AI; each hit adds one.\n"
            + "Go to the central infantry area and strike the shield pile to switch factions.",
        AdimiToolsDuelGuidanceKind.Arena => "【Arena Rules】Strike a statue to summon AI:\n"
            + "Hits set the count (max 10); color sets difficulty.\n"
            + "【Team Battle】\nBoth teams enter their glowing A/B ready areas.\n"
            + "At least 1 player per team; teams may differ in size.\n"
            + "【Match Format】\nStrike the barrel to select 1 round, best of 3 or best of 5.\n"
            + "Team battles default to 1 round; use the arena barrel to change it.",
        _ => $"【Infantry · Center】\nStrike the {Highlight("shield pile")} to switch factions.\n"
            + "Visit any arena for match rules.",
    };
}
