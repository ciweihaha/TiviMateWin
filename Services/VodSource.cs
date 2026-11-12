using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TiviMateWin.Services;

/// <summary>点播源解析：vod.json 站点表 + CMS JSON API 的影片列表/播放地址。纯逻辑，便于 SelfTest 验证。
/// 仅支持 type=1 的 JSON 接口；type=0 的 XML 接口本版不支持（站点会标为不支持）。</summary>
public static class VodSource
{
    /// <summary>vod.json 里的一个点播站</summary>
    public sealed class Site
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Api { get; set; } = "";
        public int Type { get; set; } = 1;   // 1=JSON，0=XML
        public bool Supported => Type == 1 && !string.IsNullOrWhiteSpace(Api);
    }

    /// <summary>CMS 返回的一部影片</summary>
    public sealed class Video
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Pic { get; set; }
        public string? PlayUrl { get; set; }   // 原始 vod_play_url（多源/多集格式，播前经 ExtractPlayUrl 提取）
        public string? PlayFrom { get; set; }  // 原始 vod_play_from：$$$ 分隔的线路名，与 vod_play_url 的线路一一对应
        public string? Remarks { get; set; }   // 备注（如“更新至 12 集”）
    }

    /// <summary>一条候选播放地址：线路名 + 集名 + 直链 m3u8/mp4</summary>
    public sealed class PlayCandidate
    {
        public required string Source { get; init; }   // 线路名（如“光速”）
        public required string Episode { get; init; }  // 集名（如“正片”“第1集”）
        public required string Url { get; init; }
    }

    /// <summary>视频文件扩展名（点播直链与网盘浏览共用）</summary>
    public static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".m3u8", ".ts", ".webm", ".mov", ".flv", ".avi", ".rmvb", ".mpg", ".mpeg", ".wmv", ".m4v" };

    /// <summary>文件名/链接是否像视频文件（按扩展名判断，忽略 query 与 fragment）</summary>
    public static bool IsVideoFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var clean = name.Split('?')[0].Split('#')[0];
        var ext = Path.GetExtension(clean);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>解析 vod.json 的 sites 数组；JSON 非法或无 sites 时返回空列表（不抛错）</summary>
    public static List<Site> ParseSites(string json)
    {
        var result = new List<Site>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("sites", out var sites) ||
                sites.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in sites.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var api = GetString(item, "api");
                if (string.IsNullOrWhiteSpace(api)) continue;   // 无接口地址的站点无法使用
                result.Add(new Site
                {
                    Key = GetString(item, "key") ?? "",
                    Name = GetString(item, "name") ?? api,
                    Api = api.Trim(),
                    Type = GetInt(item, "type", 1),
                });
            }
        }
        catch (JsonException) { return result; }
        return result;
    }

    /// <summary>解析 CMS 影片列表响应；返回影片列表与总页数（解析失败时列表为空、页数为 1）</summary>
    public static (List<Video> Videos, int PageCount) ParseVideoList(string json)
    {
        var videos = new List<Video>();
        var pageCount = 1;
        if (string.IsNullOrWhiteSpace(json)) return (videos, pageCount);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            pageCount = Math.Max(1, GetInt(root, "pagecount", 1));
            if (!root.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array) return (videos, pageCount);
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = GetString(item, "vod_id");
                var name = GetString(item, "vod_name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                videos.Add(new Video
                {
                    Id = id,
                    Name = name,
                    Pic = GetString(item, "vod_pic"),
                    PlayUrl = GetString(item, "vod_play_url"),
                    PlayFrom = GetString(item, "vod_play_from"),
                    Remarks = GetString(item, "vod_remarks"),
                });
            }
        }
        catch (JsonException) { return (videos, pageCount); }
        return (videos, pageCount);
    }

    /// <summary>
    /// 从 vod_play_url 提取一条可播地址：多播放源以 $$$ 分隔取第一个源，
    /// 源内多集以 # 分隔取第一集，每集“名称$地址”取 $ 之后的部分，无 $ 时整体即地址。
    /// </summary>
    public static string? ExtractPlayUrl(string? vodPlayUrl)
    {
        if (string.IsNullOrWhiteSpace(vodPlayUrl)) return null;
        var firstSource = vodPlayUrl.Split("$$$", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstSource is null) return null;
        var firstEp = firstSource.Split('#', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstEp is null) return null;
        var dollar = firstEp.IndexOf('$');
        var url = dollar >= 0 ? firstEp[(dollar + 1)..] : firstEp;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    }

    /// <summary>
    /// 展开全部候选播放地址：每条线路 × 每一集。线路名取 vod_play_from（$$$ 分隔，
    /// 与 vod_play_url 的线路按序一一对应；缺失时用“线路1/线路2…”），集名取每集 $ 之前
    /// 的部分（无 $ 时为“正片”）。顺序与 ExtractPlayUrl 一致：首个候选即其返回值。
    /// </summary>
    public static List<PlayCandidate> ExtractAllPlayUrls(string? vodPlayUrl, string? playFrom)
    {
        var result = new List<PlayCandidate>();
        if (string.IsNullOrWhiteSpace(vodPlayUrl)) return result;
        var froms = (playFrom ?? "").Split("$$$", StringSplitOptions.None);
        var sources = vodPlayUrl.Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < sources.Length; i++)
        {
            var sourceName = i < froms.Length && !string.IsNullOrWhiteSpace(froms[i])
                ? froms[i].Trim()
                : $"线路{i + 1}";
            var eps = sources[i].Split('#', StringSplitOptions.RemoveEmptyEntries);
            foreach (var ep in eps)
            {
                var dollar = ep.IndexOf('$');
                var epName = dollar >= 0 ? ep[..dollar].Trim() : "";
                var url = dollar >= 0 ? ep[(dollar + 1)..].Trim() : ep.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;
                result.Add(new PlayCandidate
                {
                    Source = sourceName,
                    Episode = string.IsNullOrWhiteSpace(epName) ? "正片" : epName,
                    Url = url,
                });
            }
        }
        return result;
    }

    /// <summary>最新影片列表地址（CMS videolist，pg 从 1 开始）</summary>
    public static string BuildListUrl(string api, int page)
        => AppendQuery(api, $"ac=videolist&pg={Math.Max(1, page)}");

    /// <summary>影片详情地址（取播放地址用）</summary>
    public static string BuildDetailUrl(string api, string id)
        => AppendQuery(api, $"ac=videolist&ids={Uri.EscapeDataString(id)}");

    /// <summary>搜索地址</summary>
    public static string BuildSearchUrl(string api, string keyword, int page)
        => AppendQuery(api, $"ac=videolist&wd={Uri.EscapeDataString(keyword)}&pg={Math.Max(1, page)}");

    private static string AppendQuery(string api, string query)
    {
        if (string.IsNullOrWhiteSpace(api)) return "";
        var sep = api.Contains('?') ? "&" : "?";
        return api + sep + query;
    }

    // JSON 字段取值：字符串与数字两种形态都接受（CMS 的 vod_id/type 常为数字）
    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static int GetInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return fallback;
    }
}
