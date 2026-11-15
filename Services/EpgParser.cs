using System.IO;
using System.Xml;
using System.Xml.Linq;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>XMLTV EPG 解析器：解析节目单数据，按频道 id 索引。
/// 用 XmlReader 流式读取每个 programme 元素（XElement.ReadFrom），
/// 避免 XDocument 全量加载大 XML 造成的内存 spike。
/// 同一遍扫描里也收 &lt;channel id&gt; 下的 display-name（名称回退用）：
/// 很多 M3U 的 tvg-id 为空、只有频道名，节目单里频道只有 id 没有名，
/// 不收 display-name 时名称回退永远查不到；id 字典用 OrdinalIgnoreCase，
/// M3U 的 tvg-id 与 EPG 的 channel id 大小写不一致时仍能匹配。</summary>
public static class EpgParser
{
    /// <summary>解析 XMLTV 数据，返回 频道id → 节目列表（按开始时间排序）</summary>
    public static Dictionary<string, List<EpgProgram>> Parse(string xml) => Parse(xml, out _);

    /// <summary>解析 XMLTV 数据，返回 频道id → 节目列表；另出 display-name → 频道id
    ///（同一遍扫描收集，一个频道可有多个 display-name，先到先得；无 &lt;channel&gt;
    /// 元素时为空字典）。调用方用它建“频道名 → 节目列表”的回退索引。</summary>
    public static Dictionary<string, List<EpgProgram>> Parse(string xml, out Dictionary<string, string> channelDisplayNames)
    {
        var result = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
        channelDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (reader.NodeType != XmlNodeType.Element) continue;

                // &lt;channel id="cctv1"&gt;&lt;display-name&gt;CCTV-1 综合&lt;/display-name&gt;…：只收名字，
                // 不建节目；programme 仍按 channel 属性归组，顺序无关（调用方在解析完成后建索引）。
                if (reader.Name == "channel")
                {
                    using var channelSubtree = reader.ReadSubtree();
                    var cel = (XElement)XElement.Load(channelSubtree);
                    var id = ((string?)cel.Attribute("id"))?.Trim() ?? "";
                    if (id.Length == 0) continue;
                    foreach (var dn in cel.Elements("display-name"))
                    {
                        var name = dn.Value?.Trim() ?? "";
                        if (name.Length > 0) channelDisplayNames.TryAdd(name, id);
                    }
                    continue;
                }

                if (reader.Name != "programme") continue;

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
            // P6：用 ReadOnlySpan 切片 + int.Parse(span) 零分配解析，
            // 原 6 次 int.Parse(Substring) + 2 次偏移 Substring 全是短命字符串。
            ReadOnlySpan<char> span = s.AsSpan();
            var y = int.Parse(span[..4]);
            var mo = int.Parse(span.Slice(4, 2));
            var d = int.Parse(span.Slice(6, 2));
            var h = int.Parse(span.Slice(8, 2));
            var mi = int.Parse(span.Slice(10, 2));
            var se = int.Parse(span.Slice(12, 2));

            var dt = new DateTime(y, mo, d, h, mi, se, DateTimeKind.Unspecified);

            // 处理时区偏移 +ZZZZ。XMLTV 标准写法在偏移前有一个空格
            //（如 20260101120000 +0800），旧实现只认 s[14] 为 +/-，
            // 标准写法 s[14] 是空格导致偏移被静默丢弃；而无空格写法
            // 又把结果换算成 UTC，与调用方的本地时间比较口径不一。
            // 现统一跳过可选空格再读偏移，并换算为本地时间返回。
            var off = 14;
            if (off < span.Length && span[off] == ' ') off++;
            // Z 后缀（ISO 8601 的 UTC 标志，XMLTV DTD 称时间格式“loosely based on
            // ISO 8601”）：与 +0000 同义，须换算为本地时间。旧实现把 Z 当作无偏移
            // 墙钟处理，同一时刻与 +0000 写法相差一个本地时区偏移（上海差 8 小时），
            // “正在播放”判断随之错位。无偏移（既无 Z 也无 +/-）仍原样返回墙钟时间，
            // 该口径是第 2–3 轮既定约定，本轮不动。
            if (off < span.Length && span[off] == 'Z')
            {
                return new DateTimeOffset(dt, TimeSpan.Zero).LocalDateTime;
            }
            if (off + 5 <= span.Length && (span[off] == '+' || span[off] == '-'))
            {
                var sign = span[off] == '+' ? 1 : -1;
                var oh = int.Parse(span.Slice(off + 1, 2));
                // 冒号偏移 +08:00（ISO 8601 写法，XMLTV DTD 称时间格式“loosely based on
                // ISO 8601”）：冒号后有两位数字时跳过冒号再读分钟；否则不跳过、
                // 仍走原有解析路径，截断偏移的行为与改前一致。
                var miOff = off + 3;
                if (miOff < span.Length && span[miOff] == ':' && miOff + 3 <= span.Length) miOff++;
                var om = int.Parse(span.Slice(miOff, 2));
                var offset = new TimeSpan(sign * oh, sign * om, 0);
                return new DateTimeOffset(dt, offset).LocalDateTime;
            }
            return dt;
        }
        catch { return DateTime.MinValue; }
    }
}
