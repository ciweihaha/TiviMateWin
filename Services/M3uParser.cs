using System.IO;
using System.Text.RegularExpressions;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>M3U / M3U8 播放列表解析器（支持扩展属性与简单格式）</summary>
public static class M3uParser
{
    private static readonly Regex GroupRx = new("group-title\\s*=\\s*\"(?<g>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LogoRx = new("tvg-logo\\s*=\\s*\"(?<l>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TvgNameRx = new("tvg-name\\s*=\\s*\"(?<n>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TvgIdRx = new("tvg-id\\s*=\\s*\"(?<i>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            var line = raw.Trim();
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
                var m = GroupRx.Match(attrs);
                if (m.Success) pendingGroup = m.Groups["g"].Value.Trim();
                if (string.IsNullOrEmpty(pendingGroup)) pendingGroup = "未分组";
                m = LogoRx.Match(attrs);
                if (m.Success) pendingLogo = m.Groups["l"].Value;
                m = TvgIdRx.Match(attrs);
                if (m.Success) pendingTvgId = m.Groups["i"].Value.Trim();
                if (string.IsNullOrEmpty(pendingName))
                {
                    m = TvgNameRx.Match(attrs);
                    if (m.Success) pendingName = m.Groups["n"].Value.Trim();
                }
            }
            else if (line.StartsWith('#'))
            {
                continue; // #EXTM3U / #EXTVLCOPT 等忽略
            }
            else
            {
                var url = line.Trim();
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
