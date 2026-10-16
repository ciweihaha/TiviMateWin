using System.Xml.Linq;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>XMLTV EPG 解析器：解析节目单数据，按频道 id 索引</summary>
public static class EpgParser
{
    /// <summary>解析 XMLTV 数据，返回 频道id → 节目列表（按开始时间排序）</summary>
    public static Dictionary<string, List<EpgProgram>> Parse(string xml)
    {
        var result = new Dictionary<string, List<EpgProgram>>();
        if (string.IsNullOrWhiteSpace(xml)) return result;

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var p in doc.Descendants("programme"))
            {
                var channelId = (string?)p.Attribute("channel") ?? "";
                if (channelId.Length == 0) continue;

                var startStr = (string?)p.Attribute("start") ?? "";
                var stopStr = (string?)p.Attribute("stop") ?? "";
                var start = ParseXmltvTime(startStr);
                var stop = ParseXmltvTime(stopStr);
                if (start == DateTime.MinValue) continue;
                if (stop == DateTime.MinValue) stop = start.AddHours(1);

                var prog = new EpgProgram
                {
                    ChannelId = channelId,
                    Title = p.Element("title")?.Value?.Trim() ?? "",
                    Description = p.Element("desc")?.Value?.Trim(),
                    Category = p.Element("category")?.Value?.Trim(),
                    Start = start,
                    Stop = stop
                };
                if (prog.Title.Length == 0) continue;

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
        catch { }
        return result;
    }

    /// <summary>解析 XMLTV 时间格式：yyyyMMddHHmmss +ZZZZ（如 20260101120000 +0800）</summary>
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

            // 处理时区偏移 +ZZZZ
            if (s.Length >= 19 && (s[14] == '+' || s[14] == '-'))
            {
                var sign = s[14] == '+' ? 1 : -1;
                var oh = int.Parse(s.Substring(15, 2));
                var om = int.Parse(s.Substring(17, 2));
                var offset = TimeSpan.FromHours(oh) + TimeSpan.FromMinutes(om);
                dt = dt.Add(-sign * offset);
            }
            return dt;
        }
        catch { return DateTime.MinValue; }
    }
}