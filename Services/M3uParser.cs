using System.IO;
using System.Text.RegularExpressions;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>M3U / M3U8 播放列表解析器（支持扩展属性与简单格式）</summary>
public static class M3uParser
{
    // P5：4 个属性正则合并为 1 个，一次 Matches 扫出全部属性；
    // 逐个 Match 每行要跑 4 遍正则。命名组按属性区分，匹配顺序不影响结果。
    private static readonly Regex AttrRx = new(
        "(?:group-title\\s*=\\s*\"(?<g>[^\"]*)\"|tvg-logo\\s*=\\s*\"(?<l>[^\"]*)\"|tvg-name\\s*=\\s*\"(?<n>[^\"]*)\"|tvg-id\\s*=\\s*\"(?<i>[^\"]*)\")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ChannelItem> Parse(string text)
    {
        var result = new List<ChannelItem>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        string pendingName = "";
        string pendingGroup = "未分组";
        string? pendingLogo = null;
        string? pendingTvgId = null;

        // L8：StringReader 逐行读，不再 Replace 整段文本 + Split 建全量行数组
        //（大清单峰值内存从约 3 倍输入降到接近 1 倍）。ReadLine 识别 \r\n、\n、
        // 孤立 \r 三种换行，与旧实现“Replace 归一化 + Split('\n')”的切行口径一致。
        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            // P5：无首尾空白时跳过 Trim 分配；有空白才 Trim（与旧口径一致，
            // 前导空白的 #EXTINF 行仍能命中 StartsWith）。
            var line = raw.Length > 0 && (char.IsWhiteSpace(raw[0]) || char.IsWhiteSpace(raw[^1]))
                ? raw.Trim() : raw;
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                // 名称在第一个不在引号内的逗号之后：属性值（如 group-title="央视,卫视"）
                // 或名称本身（如 "CCTV-1, 综合"）含逗号时，LastIndexOf(',') 会切错位置。
                var commaIdx = IndexOfNameComma(line);
                pendingName = commaIdx >= 0 ? line[(commaIdx + 1)..].Trim() : "";
                pendingGroup = "未分组";
                pendingLogo = null;
                pendingTvgId = null;
                // 深查第 22 轮：无逗号时（如 #EXTINF:-1 tvg-name="CCTV-1"）名称段不存在，
                // 但属性仍在整行里；旧代码只在有逗号时解析属性，tvg-name/分组/logo 全丢、
                // 频道回落为 URL 名。属性段改为有逗号取逗号前、无逗号取整行。
                var attrs = commaIdx >= 0 ? line[..commaIdx] : line;
                // P5：一次 Matches 扫出全部属性（原 4 次 Match）；各属性语义与原来逐个
                // Match 一致（group 为空回落“未分组”；名称仅在逗号后无名时取 tvg-name；
                // 同名属性重复时只取第一次出现，与 Match 口径一致）。
                var hasGroup = false;
                var hasLogo = false;
                var hasTvgId = false;
                foreach (Match am in AttrRx.Matches(attrs))
                {
                    if (!hasGroup && am.Groups["g"].Success) { pendingGroup = am.Groups["g"].Value.Trim(); hasGroup = true; }
                    else if (!hasLogo && am.Groups["l"].Success) { pendingLogo = am.Groups["l"].Value; hasLogo = true; }
                    else if (!hasTvgId && am.Groups["i"].Success) { pendingTvgId = am.Groups["i"].Value.Trim(); hasTvgId = true; }
                    else if (am.Groups["n"].Success && string.IsNullOrEmpty(pendingName))
                        pendingName = am.Groups["n"].Value.Trim();
                }
                if (string.IsNullOrEmpty(pendingGroup)) pendingGroup = "未分组";
            }
            else if (line.StartsWith('#'))
            {
                continue; // #EXTM3U / #EXTVLCOPT 等忽略
            }
            else
            {
                var url = line;   // P5：line 已在循环头 Trim 过，此处不再重复 Trim 分配
                if (url.Contains("://") || Path.IsPathRooted(url))
                {
                    var name = pendingName.Length > 0 ? pendingName : url;
                    result.Add(new ChannelItem
                    {
                        Number = result.Count + 1,
                        Name = name,
                        Url = url,
                        Group = pendingGroup,
                        Logo = pendingLogo,
                        TvgId = pendingTvgId
                    });
                    pendingName = "";
                    pendingGroup = "未分组";
                    pendingLogo = null;
                    pendingTvgId = null;
                }
            }
        }

        return result;
    }

    /// <summary>返回分隔属性与显示名的逗号位置：第一个不在双引号内的逗号；无则 -1。</summary>
    private static int IndexOfNameComma(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) return i;
        }
        return -1;
    }
}
