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

    public static List<ChannelItem> Parse(string text)
    {
        var result = new List<ChannelItem>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        // 换行符归一化：兼容 \r\n、\n 以及孤立 \r（老式 Mac 风格/部分采集器输出）
        if (text.Contains('\r')) text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        string pendingName = "";
        string pendingGroup = "未分组";
        string? pendingLogo = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var commaIdx = line.LastIndexOf(',');
                pendingName = commaIdx >= 0 ? line[(commaIdx + 1)..].Trim() : "";
                pendingGroup = "未分组";
                pendingLogo = null;
                if (commaIdx > 0)
                {
                    var attrs = line[..commaIdx];
                    var m = GroupRx.Match(attrs);
                    if (m.Success) pendingGroup = m.Groups["g"].Value.Trim();
                    if (string.IsNullOrEmpty(pendingGroup)) pendingGroup = "未分组";
                    m = LogoRx.Match(attrs);
                    if (m.Success) pendingLogo = m.Groups["l"].Value;
                    if (string.IsNullOrEmpty(pendingName))
                    {
                        m = TvgNameRx.Match(attrs);
                        if (m.Success) pendingName = m.Groups["n"].Value.Trim();
                    }
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
                        Logo = pendingLogo
                    });
                    pendingName = "";
                    pendingGroup = "未分组";
                    pendingLogo = null;
                }
            }
        }

        return result;
    }
}
