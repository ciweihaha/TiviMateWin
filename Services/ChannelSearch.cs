using System;
using System.Collections.Generic;

namespace TiviMateWin.Services;

/// <summary>
/// 频道搜索匹配（v1.4.33，纯逻辑）：名字/分组子串 + 台号 + 央视别名。
/// 修之前版本的问题：① 纯数字搜不到台号（只在名字里找数字，如搜 "1" 出空结果）；
/// ② "CCTV1"/"CCTV-1"/"CCTV 1"/"央视一套" 这类写法搜不到（源里名字是 "CCTV综合"
/// 这种中文后缀写法）；③ 分组选中后搜索被分组卡住（分组还是"央视频道"时搜"湖南"
/// 出空结果）——第 ③ 条由 ApplyFilter 在 query 非空时忽略分组来修，本文件只管单条匹配。
/// </summary>
public static class ChannelSearch
{
    // 央视官方编号 → 源里中文名（CCTV-1=综合 … CCTV-17=农业农村；5+ 是体育赛事）。
    private static readonly Dictionary<string, string> CctvNumberToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = "CCTV综合", ["2"] = "CCTV财经", ["3"] = "CCTV综艺", ["4"] = "CCTV中文国际",
        ["5"] = "CCTV体育", ["5+"] = "CCTV体育赛事", ["6"] = "CCTV电影", ["7"] = "CCTV国防军事",
        ["8"] = "CCTV电视剧", ["9"] = "CCTV纪录", ["10"] = "CCTV科教", ["11"] = "CCTV戏曲",
        ["12"] = "CCTV社会与法", ["13"] = "CCTV新闻", ["14"] = "CCTV少儿", ["15"] = "CCTV音乐",
        ["16"] = "CCTV奥林匹克", ["17"] = "CCTV农业农村",
    };

    // 中文数字套数 → 编号（一套=1 … 十七套=17）。
    private static readonly Dictionary<string, string> ChineseNumeralToNumber = new()
    {
        ["一"] = "1", ["二"] = "2", ["三"] = "3", ["四"] = "4", ["五"] = "5",
        ["六"] = "6", ["七"] = "7", ["八"] = "8", ["九"] = "9", ["十"] = "10",
        ["十一"] = "11", ["十二"] = "12", ["十三"] = "13", ["十四"] = "14", ["十五"] = "15",
        ["十六"] = "16", ["十七"] = "17",
    };

    /// <summary>单条匹配（query 已去空白；空 query 全匹配）。</summary>
    public static bool Matches(string name, string group, int number, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        // 纯数字：按台号精确匹配（不混进名字里的数字，如 "8" 不应命中 "CCTV8K超高清"）。
        if (int.TryParse(q, out var num)) return number == num;
        // 央视别名：CCTV1 / CCTV-1 / CCTV 1 / 央视1 / 央视一套 → 中文名。
        var cctvTarget = ExpandCctvAlias(q);
        if (cctvTarget != null)
            return name.Contains(cctvTarget, StringComparison.OrdinalIgnoreCase);
        // "央视"：名字或分组含 CCTV/央视。
        if (q == "央视")
            return name.Contains("CCTV", StringComparison.OrdinalIgnoreCase)
                || group.Contains("央视", StringComparison.OrdinalIgnoreCase);
        return name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || group.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 央视别名展开：返回源里对应的中文台名（如 "CCTV综合"），不是央视写法返回 null。
    /// 接受：CCTV1、CCTV-1、CCTV 1、CCTV5+、央视1、央视一套、央视1套。
    /// </summary>
    public static string? ExpandCctvAlias(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace("·", "");
        string? num = null;
        if (q.StartsWith("CCTV", StringComparison.Ordinal))
        {
            var rest = q["CCTV".Length..];
            if (rest.Length == 0) return null; // 光 "CCTV" 不是别名，走普通子串匹配
            num = rest;
        }
        else if (q.StartsWith("央视", StringComparison.Ordinal))
        {
            var rest = q["央视".Length..].Replace("套", "").Replace("台", "");
            if (rest.Length == 0) return null; // 光 "央视" 走普通匹配
            if (ChineseNumeralToNumber.TryGetValue(rest, out var mapped)) num = mapped;
            else num = rest;
        }
        else return null;
        return num != null && CctvNumberToName.TryGetValue(num, out var target) ? target : null;
    }
}
