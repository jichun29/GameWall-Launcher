using System;
using System.Text;

namespace GameWall.Services;

/// <summary>
/// 游戏名比对。
///
/// 同一个游戏在不同来源里名字差别很大：
/// 「英雄联盟」/「League of Legends」/「英雄联盟（含经典模式）」/「LOL」，
/// 或者目录名是 JAPANSTIGMATIZED、Steam 里写的是 Japan Stigmatized。
/// 直接字符串比较对不上，所以统一走「去掉标点空格 → 算最长公共子序列相似度」。
/// </summary>
public static class NameSimilarity
{
    /// <summary>去掉空格、标点、™®© 之类，转小写，只留字母和数字。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 归一化之后的最长公共子序列相似度，取值 0~1。
    /// 相比「编辑距离」，它不会被一方多出来的后缀（如"含经典模式"）拖太低。
    /// </summary>
    public static double Score(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);

        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (left == right)
        {
            return 1;
        }

        // 系列作品靠数字区分：Sniper Elite 4 / 3、Forza Horizon 5 / 4 字面几乎一样，
        // 但完全是两款游戏。数字对不上直接判为不同，连别名表也不查 ——
        // 否则 "Forza Horizon 5" 和 "Forza Horizon 4" 会因为都命中 horizon 而被认成一款。
        var leftDigits = DigitsOf(left);
        var rightDigits = DigitsOf(right);
        if (leftDigits.Length > 0 && rightDigits.Length > 0 && leftDigits != rightDigits)
        {
            return 0;
        }

        // 再看别名表：中英对照、简称全称这类纯字面比不出来的对应关系
        if (MatchesAlias(left, right))
        {
            return 1;
        }

        // 一个完整包含另一个（"英雄联盟" ⊂ "英雄联盟含经典模式"）时给高分，
        // 但短的那个必须至少占长的 60%，且多出来那段只能是版本后缀 ——
        // 否则 "League of Legends" 会把 "League of Legends: Wild Rift" 认成同一款，
        // 那是两款完全不同的游戏，配错封面比不配更糟。
        if (left.Length >= 3 && right.Length >= 3)
        {
            if (TryContained(left, right, out var ratio) || TryContained(right, left, out ratio))
            {
                return 0.80 + 0.18 * ratio;
            }
        }

        var prev = new int[right.Length + 1];
        var curr = new int[right.Length + 1];

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                curr[j] = left[i - 1] == right[j - 1]
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);
            }

            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }

        return 2.0 * prev[right.Length] / (left.Length + right.Length);
    }

    /// <summary>
    /// <paramref name="shorter"/> 是否被 <paramref name="longer"/> 完整包含，
    /// 且长出来的部分只是版本后缀（含经典模式、决定版、GOTY 之类）。
    /// </summary>
    private static bool TryContained(string shorter, string longer, out double ratio)
    {
        ratio = 0;
        if (shorter.Length >= longer.Length)
        {
            return false;
        }

        ratio = (double)shorter.Length / longer.Length;
        if (ratio < 0.60)
        {
            return false;
        }

        var index = longer.IndexOf(shorter, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var extra = longer.Remove(index, shorter.Length);
        return IsBenignSuffix(extra);
    }

    /// <summary>取出字符串里的数字部分（"sniperelite4" → "4"）。</summary>
    private static string DigitsOf(string text)
    {
        var buffer = new StringBuilder(4);
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                buffer.Append(ch);
            }
        }

        return buffer.ToString();
    }

    private static bool MatchesAlias(string left, string right)
    {
        // 两边序号相同就先抹掉再看别名 —— 「巫师3」和「The Witcher 3」的
        // 「3」是同一款游戏的代次，不该成为别名匹配的障碍；
        // 序号不同（4 vs 3）在前面已经被判为不同游戏了，走不到这里。
        var leftDigits = DigitsOf(left);
        var rightDigits = DigitsOf(right);
        if (leftDigits.Length > 0 && leftDigits == rightDigits)
        {
            left = StripDigits(left);
            right = StripDigits(right);
        }

        foreach (var group in Aliases)
        {
            if (HitsGroup(left, group) && HitsGroup(right, group))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripDigits(string text)
    {
        var buffer = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsDigit(ch))
            {
                buffer.Append(ch);
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 名字是否落在这一组别名里。
    ///
    /// 匹配强度分三档：
    ///  • 全等 —— 一定命中；
    ///  • 中文别名（≥2 字）做前缀匹配 —— 中文表意密度高，「巫师狂猎」以「巫师」开头
    ///    基本就是同一款，而且中文副标题（狂猎 / 热度 / 夜袭）没法穷举；
    ///  • 英文别名（≥4 字母）做包含匹配，但多出来的那段必须是版本后缀白名单里的词 ——
    ///    否则 "League of Legends" 会把 "League of Legends: Wild Rift" 也认成一款，
    ///    那是两个完全不同的游戏，封面配错了比不配更糟。
    /// 三个字符以内的英文别名只认全等（"re"、"ac"、"cf" 这类短串做包含会一锅粥）。
    /// </summary>
    private static bool HitsGroup(string normalized, string[] group)
    {
        foreach (var alias in group)
        {
            if (normalized == alias)
            {
                return true;
            }

            // 中文别名：前缀匹配
            if (IsCjk(alias) && alias.Length >= 2 && normalized.StartsWith(alias, StringComparison.Ordinal))
            {
                return true;
            }

            if (alias.Length < 4 || !normalized.Contains(alias, StringComparison.Ordinal))
            {
                continue;
            }

            // 取出别名之外多出来的部分，判断它是不是无害的版本后缀
            var index = normalized.IndexOf(alias, StringComparison.Ordinal);
            var extra = normalized.Remove(index, alias.Length);
            if (IsBenignSuffix(extra))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 长出来的那一段算不算「只是个副标题」。
    ///
    /// 判据两条，命中任一即可：
    ///  • 在白名单里（决定版 / GOTY / 含经典模式 …，这类是明确的版本说明）；
    ///  • 很短（≤6 个字符，如 Heat、狂猎、夜袭 …）—— 英文副标题动辄一两个词，
    ///    没法穷举，但真正的「另一款游戏」名字通常长得更多
    ///    （Wild Rift 是 8 个字符，会被挡在门外）。
    /// </summary>
    private static bool IsBenignSuffix(string extra)
    {
        if (extra.Length == 0)
        {
            return true;
        }

        if (extra.Length <= 6)
        {
            return true;
        }

        foreach (var suffix in BenignSuffixes)
        {
            if (extra == suffix || extra.Contains(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] BenignSuffixes =
    {
        "含经典模式", "经典模式", "微game版", "wegame版", "wegame", "腾讯版", "国服", "全球版", "国际版",
        "决定版", "重制版", "复刻版", "最终版", "完全版", "终极版", "豪华版", "标准版", "年度版",
        "试玩版", "正式版", "免费版", "demo", "trial", "goty", "edition", "definitive",
        "remastered", "remake", "complete", "deluxe", "ultimate", "enhanced", "hd", "vr", "classic",
        "纪念版", "周年纪念版", "合集", "重置版",
    };

    /// <summary>
    /// 中英对照 / 常见别名的同义词组（每项都必须是 <see cref="Normalize"/> 之后的形式：
    /// 全小写、只留字母数字）。只用于「这两个名字是不是同一款游戏」的判断，
    /// 不参与任何封面抓取逻辑，纯本地查表。
    /// </summary>
    private static readonly string[][] Aliases =
    {
        new[] { "英雄联盟", "leagueoflegends", "lol", "英雄联盟微game版", "英雄联盟wegame版" },
        new[] { "穿越火线", "crossfire", "cf" },
        new[] { "地下城与勇士", "dungeonfighter", "dnf" },
        new[] { "反恐精英", "counterstrike", "csgo", "cs2" },
        new[] { "绝地求生", "pubg", "playerunknownsbattlegrounds" },
        new[] { "我的世界", "minecraft" },
        new[] { "塞尔达传说", "thelegendofzelda", "zelda" },
        new[] { "黑暗之魂", "darksouls" },
        new[] { "艾尔登法环", "eldenring" },
        new[] { "只狼", "sekiro", "sekiroshadowsdietwice" },
        new[] { "荒野大镖客", "reddeadredemption", "rdr2" },
        new[] { "侠盗猎车手", "grandtheftauto", "gta", "gta5", "gtav" },
        new[] { "巫师", "thewitcher", "witcher" },
        new[] { "赛博朋克", "cyberpunk", "cyberpunk2077" },
        new[] { "上古卷轴", "theelderscrolls", "skyrim" },
        new[] { "辐射", "fallout" },
        new[] { "文明", "civilization", "civ" },
        new[] { "模拟人生", "thesims", "sims" },
        new[] { "极品飞车", "needforspeed", "nfs" },
        new[] { "刺客信条", "assassinscreed", "ac" },
        new[] { "使命召唤", "callofduty", "cod" },
        new[] { "战地", "battlefield" },
        new[] { "孤岛惊魂", "farcry" },
        new[] { "生化危机", "residentevil", "re" },
        new[] { "古墓丽影", "tombraider" },
        new[] { "死亡搁浅", "deathstranding" },
        new[] { "战神", "godofwar" },
        new[] { "地平线", "horizon" },
        new[] { "神秘海域", "uncharted" },
        new[] { "最后生还者", "thelastofus", "tlou" },
        new[] { "动物森友会", "animalcrossing" },
        new[] { "星露谷物语", "stardewvalley" },
        new[] { "泰拉瑞亚", "terraria" },
        new[] { "饥荒", "dontstarve" },
        new[] { "双人成行", "ittakestwo" },
        new[] { "人类一败涂地", "humanfallflat" },
        new[] { "恐鬼症", "phasmophobia" },
        new[] { "黎明杀机", "deadbydaylight", "dbd" },
        new[] { "方舟", "ark", "arksurvivalevolved" },
        new[] { "求生之路", "left4dead", "l4d" },
        new[] { "传送门", "portal" },
        new[] { "半条命", "halflife" },
        new[] { "毁灭战士", "doom" },
        new[] { "光环", "halo" },
        new[] { "战争机器", "gearsofwar" },
        new[] { "帝国时代", "ageofempires", "aoe" },
        new[] { "全面战争", "totalwar" },
        new[] { "欧卡", "eurotrucksimulator" },
        new[] { "美卡", "americantrucksimulator" },
        new[] { "神力科莎", "assettocorsa" },
    };

    /// <summary>判断两个名字是不是「同一款游戏的两种写法」。</summary>
    public static bool ProbablySame(string? a, string? b, double threshold = 0.72) => Score(a, b) >= threshold;
}
