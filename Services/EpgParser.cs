using System.IO;
using System.Xml;
using System.Xml.Linq;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>XMLTV EPG 解析器：解析节目单数据，按频道 id 索引。
/// 用 XmlReader 流式读取每个 programme 元素（XElement.ReadFrom），
/// 避免 XDocument 全量加载大 XML 造成的内存 spike。</summary>
public static class EpgParser
{
    /// <summary>解析 XMLTV 数据，返回 频道id → 节目列表（按开始时间排序）</summary>
    public static Dictionary<string, List<EpgProgram>> Parse(string xml)
    {
        var result = new Dictionary<string, List<EpgProgram>>();
        if (string.IsNullOrWhiteSpace(xml)) return result;

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore
            });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme") continue;

                // 用 ReadSubtree 只加载当前 programme 子树，reader 自动移到下一个节点
                using var subtree = reader.ReadSubtree();
                var el = (XElement)XElement.Load(subtree);

                var channelId = (string?)el.Attribute("channel") ?? "";
                if (channelId.Length == 0) continue;

                var startStr = (string?)el.Attribute("start") ?? "";
                var stopStr = (string?)el.Attribute("stop") ?? "";
                var start = ParseXmltvTime(startStr);
                var stop = ParseXmltvTime(stopStr);
                if (start == DateTime.MinValue) continue;
                if (stop == DateTime.MinValue) stop = start.AddHours(1);

                var title = el.Element("title")?.Value?.Trim() ?? "";
                if (title.Length == 0) continue;

                var prog = new EpgProgram
                {
                    ChannelId = channelId,
                    Title = title,
                    Description = el.Element("desc")?.Value?.Trim(),
                    Category = el.Element("category")?.Value?.Trim(),
                    Start = start,
                    Stop = stop
                };

                if (!result.TryGetValue(channelId, out var list))
                {
                    list = new List<EpgProgram>();
                    result[channelId] = list;
                }
                list.Add(prog);
            }

            foreach (var kv in result)
                kv.Value.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        catch (Exception ex)
        {
            // L4：畸形 XML 导致解析中途失败时，返回已解析的部分结果，但记一条日志
            //（仅异常类型与消息，不含 XML 内容），便于区分源问题与解析问题。
            AppLog.Error($"epg parse failed: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    /// <summary>解析 XMLTV 时间格式：yyyyMMddHHmmss +ZZZZ（如 20260101120000 +0800），
    /// 偏移也接受 ISO 8601 冒号写法 +08:00 与 Z 后缀（UTC）。返回本地时间：有偏移时按偏移换算为本机本地时间（调用方 ShowEpgForChannel
    /// 用 DateTime.Now 比较、TimeRange 直接显示本地时间，口径须一致）；无偏移时原样返回墙钟时间。</summary>
    private static DateTime ParseXmltvTime(string s)
    {
        if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
        s = s.Trim();
        if (s.Length < 14) return DateTime.MinValue;

        try
        {
            var y = int.Parse(s[..4]);
            var mo = int.Parse(s.Substring(4, 2));
            var d = int.Parse(s.Substring(6, 2));
            var h = int.Parse(s.Substring(8, 2));
            var mi = int.Parse(s.Substring(10, 2));
            var se = int.Parse(s.Substring(12, 2));

            var dt = new DateTime(y, mo, d, h, mi, se, DateTimeKind.Unspecified);

            // 处理时区偏移 +ZZZZ。XMLTV 标准写法在偏移前有一个空格
            //（如 20260101120000 +0800），旧实现只认 s[14] 为 +/-，
            // 标准写法 s[14] 是空格导致偏移被静默丢弃；而无空格写法
            // 又把结果换算成 UTC，与调用方的本地时间比较口径不一。
            // 现统一跳过可选空格再读偏移，并换算为本地时间返回。
            var off = 14;
            if (off < s.Length && s[off] == ' ') off++;
            // Z 后缀（ISO 8601 的 UTC 标志，XMLTV DTD 称时间格式“loosely based on
            // ISO 8601”）：与 +0000 同义，须换算为本地时间。旧实现把 Z 当作无偏移
            // 墙钟处理，同一时刻与 +0000 写法相差一个本地时区偏移（上海差 8 小时），
            // “正在播放”判断随之错位。无偏移（既无 Z 也无 +/-）仍原样返回墙钟时间，
            // 该口径是第 2–3 轮既定约定，本轮不动。
            if (off < s.Length && s[off] == 'Z')
            {
                return new DateTimeOffset(dt, TimeSpan.Zero).LocalDateTime;
            }
            if (off + 5 <= s.Length && (s[off] == '+' || s[off] == '-'))
            {
                var sign = s[off] == '+' ? 1 : -1;
                var oh = int.Parse(s.Substring(off + 1, 2));
                // 冒号偏移 +08:00（ISO 8601 写法，XMLTV DTD 称时间格式“loosely based on
                // ISO 8601”）：冒号后有两位数字时跳过冒号再读分钟；否则不跳过、
                // 仍走原有解析路径，截断偏移的行为与改前一致。
                var miOff = off + 3;
                if (miOff < s.Length && s[miOff] == ':' && miOff + 3 <= s.Length) miOff++;
                var om = int.Parse(s.Substring(miOff, 2));
                var offset = new TimeSpan(sign * oh, sign * om, 0);
                return new DateTimeOffset(dt, offset).LocalDateTime;
            }
            return dt;
        }
        catch { return DateTime.MinValue; }
    }
}
