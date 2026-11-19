using System;
using System.Collections.Generic;

namespace TiviMateWin.Services;

/// <summary>
/// 频道语种判断（v1.4.33）：实时翻译只对英语音频开（Whisper 用 tiny.en，本来也只认英语；
/// 法语/德语/日语/韩语等一律不翻译，不要按"非中文"一刀切）。
/// 判定只看台名（EPG 语言不可靠）：已知英语台优先命中 → 含中文/日文/韩文字符 →
/// 已知中文台的拉丁写法（CCTV/TVB）→ 已知非英语拉丁台 → 其余纯拉丁名按英语算。
/// </summary>
public static class ChannelLanguage
{
    /// <summary>翻译模式（AppConfig.LiveSubtitleTranslationMode 的取值）：关闭 / 仅英语台 / 全部台。</summary>
    public const string ModeOff = "Off";
    public const string ModeEnglishOnly = "EnglishOnly";
    public const string ModeAll = "All";

    /// <summary>默认翻译模式：仅英语台。</summary>
    public const string DefaultMode = ModeEnglishOnly;

    // 已知英语频道（子串匹配，大写比对）："BBC 新闻" 这种中英混写的名字也要翻；
    // 这些串 distinctive，不会误中中文台名。
    private static readonly string[] EnglishSubstrings =
    {
        "BBC", "CNN", "MSNBC", "CNBC", "AL JAZEERA", "EURONEWS", "BLOOMBERG",
        "SKY NEWS", "FOX NEWS", "NHK WORLD", "DW ENGLISH", "FRANCE 24 ENGLISH",
        "NATIONAL GEOGRAPHIC", "NAT GEO", "DISCOVERY", "HISTORY", "ESPN", "HBO", "TLC", "CGTN",
    };

    // 已知英语频道（按词匹配）：短代码不能子串匹配（会误中别的词）。
    private static readonly string[] EnglishWords = { "ABC", "NBC", "CBS", "PBS" };

    // 已知非英语拉丁台（子串匹配）：TV5Monde、France 24（不带 ENGLISH 的）、德国之声等。
    private static readonly string[] NonEnglishSubstrings =
    {
        "TV5MONDE", "FRANCE 24", "DEUTSCHE WELLE",
    };

    // 已知非英语拉丁台（按词匹配）：多语种台的 plain 写法（DW、NHK 不带 ENGLISH/WORLD 时按母语算）。
    private static readonly string[] NonEnglishWords =
    {
        "TV5", "ARTE", "ZDF", "ARD", "RTL", "RAI", "TVE", "RTVE", "RTP", "TVP",
        "SVT", "NRK", "YLE", "NOS", "DW", "NHK",
    };

    /// <summary>台名是否含中文/日文/韩文字符（CJK 统一表意文字、假名、谚文）。</summary>
    public static bool ContainsCjk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if ((c >= '\u4E00' && c <= '\u9FFF') ||   // CJK 统一表意文字
                (c >= '\u3400' && c <= '\u4DBF') ||   // CJK 扩展 A
                (c >= '\u3040' && c <= '\u309F') ||   // 平假名
                (c >= '\u30A0' && c <= '\u30FF') ||   // 片假名
                (c >= '\uAC00' && c <= '\uD7AF') ||   // 谚文音节
                (c >= '\u1100' && c <= '\u11FF'))     // 谚文字母
                return true;
        }
        return false;
    }

    /// <summary>台名是否英语台：只看台名（见类注释的判定顺序）。</summary>
    public static bool IsEnglishChannel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var upper = name.ToUpperInvariant();
        // 1. 已知英语台先命中（"BBC 新闻" 这种中英混写也要翻）。
        foreach (var en in EnglishSubstrings)
            if (upper.Contains(en, StringComparison.Ordinal)) return true;
        foreach (var en in EnglishWords)
            if (ContainsToken(upper, en)) return true;
        // 2. 含 CJK → 中/日/韩台，不翻。
        if (ContainsCjk(name)) return false;
        // 3. 已知中文台的拉丁写法（CCTV/TVB）：不翻。
        if (upper.Contains("CCTV", StringComparison.Ordinal) ||
            upper.Contains("TVB", StringComparison.Ordinal)) return false;
        // 4. 已知非英语拉丁台：不翻。
        foreach (var non in NonEnglishSubstrings)
            if (upper.Contains(non, StringComparison.Ordinal)) return false;
        foreach (var non in NonEnglishWords)
            if (ContainsToken(upper, non)) return false;
        // 5. 兜底：其余纯拉丁字母名按英语算（当前源里除中文台外基本只有英语台）。
        return IsLatinOnly(name);
    }

    /// <summary>是否只含拉丁字母/数字/空白/常用标点（无其他语种文字）。</summary>
    public static bool IsLatinOnly(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var c in s)
        {
            if (c < '\u0080') continue; // ASCII：字母/数字/空白/标点都算拉丁
            if (c >= '\u00C0' && c <= '\u024F') continue; // 拉丁字母扩展（法/德变音字母等）
            return false;
        }
        return true;
    }

    /// <summary>按词匹配：把名字按非字母数字切词，任一词等于 token。避免 "ABC" 误中
    /// "ABCDE" 这类子串，也避免 "DW" 误中 "EDWARD"。</summary>
    internal static bool ContainsToken(string upper, string token)
    {
        foreach (var word in SplitWords(upper))
            if (word == token) return true;
        return false;
    }

    private static IEnumerable<string> SplitWords(string upper)
    {
        var start = -1;
        for (var i = 0; i < upper.Length; i++)
        {
            if (char.IsLetterOrDigit(upper[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return upper[start..i];
                start = -1;
            }
        }
        if (start >= 0) yield return upper[start..];
    }

    /// <summary>
    /// 翻译是否对当前台开启（纯逻辑，供 MainWindow 与测试共用）：
    /// 总开关关 → 不开；手动强制（切台后清零）优先；模式 Off → 不开；
    /// 模式 All → 全开；默认 EnglishOnly → 仅英语台开。
    /// </summary>
    public static bool ResolveTranslationActive(bool masterEnabled, string? mode, string? channelName, bool? forceOverride)
    {
        if (!masterEnabled) return false;
        if (forceOverride.HasValue) return forceOverride.Value;
        return mode switch
        {
            ModeOff => false,
            ModeAll => true,
            _ => IsEnglishChannel(channelName),
        };
    }

    /// <summary>模式字符串归一化：非法/空值回落默认（仅英语台）。</summary>
    public static string NormalizeMode(string? mode) => mode switch
    {
        ModeOff => ModeOff,
        ModeAll => ModeAll,
        _ => ModeEnglishOnly,
    };
}
