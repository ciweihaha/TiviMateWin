using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;   // SystemParameters（窗口位置校验自测用，无界面可访问）
using LibVLCSharp.Shared;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>无界面自检：--selftest 验证引擎与解析器；--playtest <url> 验证真实播放</summary>
public static class SelfTest
{
    private const string SampleM3u = """
        #EXTM3U
        #EXTINF:-1 tvg-id="cctv1" tvg-logo="http://example.com/cctv1.png" group-title="央视",CCTV-1 综合
        http://stream.example.com/cctv1.m3u8
        #EXTINF:-1 tvg-name="CCTV5" group-title="央视",CCTV-5 体育
        http://stream.example.com/cctv5.m3u8
        #EXTINF:-1 group-title="卫视",湖南卫视
        http://stream.example.com/hntv.m3u8
        http://bare.example.com/plain.ts
        """;

    public static void Run(string? url, bool playTest)
    {
        var log = Path.Combine(Path.GetTempPath(), "tivimate_selftest.txt");
        var lines = new List<string>();
        try
        {
            Core.Initialize();
            using var libvlc = new LibVLC();
            lines.Add($"PASS 引擎初始化 libVLC {libvlc.Version}");

            // --- M3U 解析 ---
            var channels = M3uParser.Parse(SampleM3u);
            lines.Add(channels.Count == 4
                ? "PASS M3U解析 4/4 频道"
                : $"FAIL M3U解析 期望4个 实际{channels.Count}");

            var cctv1 = channels.FirstOrDefault(c => c.Name.Contains("CCTV-1"));
            lines.Add(cctv1?.Logo == "http://example.com/cctv1.png"
                ? "PASS logo/名称解析"
                : $"FAIL logo解析 实际={cctv1?.Logo ?? "无"}");

            var groups = channels.Select(c => c.Group).Distinct().Count();
            lines.Add(groups == 3 ? "PASS 分组解析 3 组" : $"FAIL 分组 期望3 实际{groups}");

            // --- M3U 边界情况 ---
            lines.Add(M3uParser.Parse("").Count == 0 ? "PASS 空输入解析" : "FAIL 空输入");
            lines.Add(M3uParser.Parse("   \n\n  ").Count == 0 ? "PASS 空白输入解析" : "FAIL 空白输入");
            var single = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 group-title=\"直播\",测试流\nhttp://x.com/live.m3u8");
            lines.Add(single.Count == 1 && single[0].Group == "直播" && single[0].Name == "测试流"
                ? "PASS 单流解析" : "FAIL 单流解析");
            // 名称/属性值含逗号：名称取第一个不在引号内的逗号之后
            var commaName = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 group-title=\"央视\",CCTV-1, 综合\nhttp://c.m3u8");
            lines.Add(commaName.Count == 1 && commaName[0].Name == "CCTV-1, 综合" && commaName[0].Group == "央视"
                ? "PASS M3U解析-名称含逗号" : "FAIL M3U解析-名称含逗号");
            var commaAttr = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 group-title=\"央视,卫视\",CCTV-2\nhttp://c2.m3u8");
            lines.Add(commaAttr.Count == 1 && commaAttr[0].Name == "CCTV-2" && commaAttr[0].Group == "央视,卫视"
                ? "PASS M3U解析-属性值含逗号" : "FAIL M3U解析-属性值含逗号");
            // CRLF 与孤立 CR 兼容
            var crlf = M3uParser.Parse("#EXTM3U\r\n#EXTINF:-1,频道A\r\nhttp://a.m3u8\r\n");
            lines.Add(crlf.Count == 1 && crlf[0].Name == "频道A" ? "PASS CRLF兼容" : "FAIL CRLF");
            var cr = M3uParser.Parse("#EXTM3U\r#EXTINF:-1,频道B\rhttp://b.m3u8\r");
            lines.Add(cr.Count == 1 && cr[0].Name == "频道B" ? "PASS 孤立CR兼容" : "FAIL 孤立CR");

            // --- M3U tvg-id 解析 ---
            var tvg = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-id=\"cctv1\" group-title=\"央视\",CCTV-1\nhttp://c.m3u8");
            lines.Add(tvg.Count == 1 && tvg[0].TvgId == "cctv1" ? "PASS tvg-id解析" : $"FAIL tvg-id 实际={tvg.FirstOrDefault()?.TvgId}");

            // --- EPG XMLTV 解析 ---
            var epgXml = """
                <?xml version="1.0"?>
                <tv>
                  <programme start="20260101120000 +0800" stop="20260101130000 +0800" channel="cctv1">
                    <title>新闻联播</title>
                    <desc>今日新闻</desc>
                    <category>新闻</category>
                  </programme>
                  <programme start="20260101130000 +0800" stop="20260101140000 +0800" channel="cctv1">
                    <title>天气预报</title>
                  </programme>
                </tv>
                """;
            var epg = EpgParser.Parse(epgXml);
            lines.Add(epg.Count == 1 && epg["cctv1"].Count == 2 && epg["cctv1"][0].Title == "新闻联播"
                ? "PASS EPG解析 2节目" : $"FAIL EPG 频道数={epg.Count}");
            // 深查第 2 轮：时区偏移换算——标准写法 "… +0800" 须换算为本地时间，
            // 其 UTC 时刻为 04:00/05:00（与机器时区无关的断言）；无空格写法须等价。
            // 后续断言复用首个频道的节目表：首轮解析失败时记 FAIL 而不是抛
            // KeyNotFoundException 中断整个自检（其后 Logo/缓存/数字键/窗口/配置检查都不再执行）。
            var epgBase = epg.TryGetValue("cctv1", out var epgBaseList) && epgBaseList.Count >= 2 ? epgBaseList : null;
            lines.Add(epgBase != null && epgBase[0].Start.ToUniversalTime() == new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc) &&
                      epgBase[0].Stop.ToUniversalTime() == new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc)
                ? "PASS EPG解析-带空格时区偏移换算" : $"FAIL EPG时区 实际Start={epgBase?[0].Start:O}");
            var epgNospace = EpgParser.Parse("<tv><programme start=\"20260101120000+0800\" stop=\"20260101130000+0800\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            lines.Add(epgNospace.TryGetValue("cctv1", out var nsList) && nsList.Count == 1 && epgBase != null && nsList[0].Start == epgBase[0].Start
                ? "PASS EPG解析-有无空格偏移等价" : "FAIL EPG时区-有无空格偏移不等价");
            // 深查第 3 轮：冒号偏移 +08:00（ISO 8601 写法）须与 +0800 等价换算，
            // 其 UTC 时刻为 04:00/05:00（与机器时区无关的断言）；无空格冒号写法亦然。
            var epgColon = EpgParser.Parse("<tv><programme start=\"20260101120000 +08:00\" stop=\"20260101130000 +08:00\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            lines.Add(epgColon.TryGetValue("cctv1", out var coList) && coList.Count == 1 && epgBase != null && coList[0].Start == epgBase[0].Start && coList[0].Stop == epgBase[0].Stop
                ? "PASS EPG解析-冒号偏移等价" : "FAIL EPG时区-冒号偏移不等价");
            var epgColonNospace = EpgParser.Parse("<tv><programme start=\"20260101120000+08:00\" stop=\"20260101130000+08:00\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            lines.Add(epgColonNospace.TryGetValue("cctv1", out var cnList) && cnList.Count == 1 && cnList[0].Start.ToUniversalTime() == new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc) &&
                      cnList[0].Stop.ToUniversalTime() == new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc)
                ? "PASS EPG解析-冒号偏移无空格换算" : "FAIL EPG时区-冒号偏移无空格换算");
            // 深查第 4 轮：Z 后缀（ISO 8601 UTC 标志）须与 +0000 等价换算，
            // 其 UTC 时刻为 12:00/13:00（与机器时区无关的断言）；带空格写法亦然。
            var epgZ = EpgParser.Parse("<tv><programme start=\"20260101120000Z\" stop=\"20260101130000Z\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            lines.Add(epgZ.TryGetValue("cctv1", out var zList) && zList.Count == 1 && zList[0].Start.ToUniversalTime() == new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) &&
                      zList[0].Stop.ToUniversalTime() == new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc)
                ? "PASS EPG解析-Z后缀等价+0000换算" : "FAIL EPG时区-Z后缀不等价+0000");
            var epgZSpace = EpgParser.Parse("<tv><programme start=\"20260101120000 Z\" stop=\"20260101130000 Z\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            var epgZero = EpgParser.Parse("<tv><programme start=\"20260101120000 +0000\" stop=\"20260101130000 +0000\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            lines.Add(epgZSpace.TryGetValue("cctv1", out var zsList) && zsList.Count == 1 &&
                      epgZero.TryGetValue("cctv1", out var zeList) && zeList.Count == 1 &&
                      zsList[0].Start == zeList[0].Start && zsList[0].Stop == zeList[0].Stop
                ? "PASS EPG解析-Z后缀带空格等价+0000" : "FAIL EPG时区-Z后缀带空格不等价+0000");
            // 深查第 4 轮回归守卫：无偏移（既无 Z 也无 +/-）仍原样返回墙钟时间，口径不变。
            var epgPlain = EpgParser.Parse("<tv><programme start=\"20260101120000\" stop=\"20260101130000\" channel=\"cctv1\"><title>新闻联播</title></programme></tv>");
            lines.Add(epgPlain.TryGetValue("cctv1", out var pList) && pList.Count == 1 && pList[0].Start == new DateTime(2026, 1, 1, 12, 0, 0) &&
                      pList[0].Stop == new DateTime(2026, 1, 1, 13, 0, 0)
                ? "PASS EPG解析-无偏移仍为墙钟时间" : "FAIL EPG时区-无偏移墙钟改变");

            // --- EPG 空输入 ---
            lines.Add(EpgParser.Parse("").Count == 0 ? "PASS EPG空输入" : "FAIL EPG空输入");

            // --- EPG 源地址提取（深查第 10 轮；纯逻辑） ---
            // 清单首行是空行时旧实现只看 [0] 行，url-tvg 永远取不到，EPG 静默缺失。
            lines.Add(MainWindow.ExtractEpgUrl("#EXTM3U url-tvg=\"http://epg.example.com/epg.xml\"\n#EXTINF:-1,A\nhttp://a.m3u8") == "http://epg.example.com/epg.xml"
                ? "PASS EPG源提取-标准头部" : "FAIL EPG源提取-标准头部");
            lines.Add(MainWindow.ExtractEpgUrl("\n\n#EXTM3U url-tvg=\"http://epg.example.com/epg.xml\"\n#EXTINF:-1,A\nhttp://a.m3u8") == "http://epg.example.com/epg.xml"
                ? "PASS EPG源提取-前导空行仍取到" : "FAIL EPG源提取-前导空行仍取到");
            lines.Add(MainWindow.ExtractEpgUrl("\r\n#EXTM3U url-tvg=\"http://epg.example.com/epg.xml\"\n#EXTINF:-1,A\nhttp://a.m3u8") == "http://epg.example.com/epg.xml"
                ? "PASS EPG源提取-前导CRLF空行仍取到" : "FAIL EPG源提取-前导CRLF空行仍取到");
            lines.Add(MainWindow.ExtractEpgUrl("#EXTM3U\n#EXTINF:-1,A\nhttp://a.m3u8") == null
                ? "PASS EPG源提取-无url-tvg返回空" : "FAIL EPG源提取-无url-tvg返回空");
            lines.Add(MainWindow.ExtractEpgUrl("") == null && MainWindow.ExtractEpgUrl("\n\n") == null
                ? "PASS EPG源提取-空输入返回空" : "FAIL EPG源提取-空输入返回空");

            // --- 拉取限长（深查第 13 轮；纯流逻辑，不触网络） ---
            // 旧 FetchTextAsync 用 GetStringAsync 无界缓冲，失控源可撑爆内存；
            // 新实现流式限长（复用 LogoService.ReadCappedAsync），超限抛错且不重试。
            var capOk = MainWindow.ReadResponseTextCappedAsync(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:-1,测试频道\nhttp://a.m3u8")) },
                1024, CancellationToken.None).GetAwaiter().GetResult();
            lines.Add(capOk.Contains("测试频道") ? "PASS 拉取限长-小响应正常解码" : "FAIL 拉取限长-小响应正常解码");
            var capOver = false;
            try
            {
                _ = MainWindow.ReadResponseTextCappedAsync(
                    new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1025]) },
                    1024, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (MainWindow.ResponseTooLargeException) { capOver = true; }
            lines.Add(capOver ? "PASS 拉取限长-超限抛错" : "FAIL 拉取限长-超限抛错");
            // CharSet 优先（与旧 GetStringAsync 口径一致）：GBK 字节 + GBK 声明须正确解码
            var capGbkResp = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(System.Text.Encoding.GetEncoding("GBK").GetBytes("测试")) };
            capGbkResp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "GBK" };
            var capGbk = MainWindow.ReadResponseTextCappedAsync(capGbkResp, 1024, CancellationToken.None).GetAwaiter().GetResult();
            lines.Add(capGbk == "测试" ? "PASS 拉取限长-CharSet解码" : $"FAIL 拉取限长-CharSet解码 实际={capGbk}");
            // 恰达上限放行（与 Logo 限长口径一致）
            var capExact = MainWindow.ReadResponseTextCappedAsync(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024]) },
                1024, CancellationToken.None).GetAwaiter().GetResult();
            lines.Add(capExact.Length == 1024 ? "PASS 拉取限长-恰达上限放行" : "FAIL 拉取限长-恰达上限放行");

            // --- Logo 流式限长（深查第 5 轮；纯流逻辑，不触网络、不解码） ---
            // ReadCappedAsync 是 LogoService 下载路径的限长核心：旧实现 GetByteArrayAsync
            // 先整段读入内存、Decode 才在事后以 >5MB 拒收；新实现 8KB 分块读、累计超限立即停读。
            // Run 是同步方法，测试用 GetAwaiter().GetResult()；被测方法内全部 await 均
            // ConfigureAwait(false)，不回贴 UI 同步上下文，不会死锁。
            var logoSmall = LogoService.ReadCappedAsync(new MemoryStream(new byte[] { 1, 2, 3 }), LogoService.MaxLogoBytes).GetAwaiter().GetResult();
            lines.Add(logoSmall != null && logoSmall.Length == 3 && logoSmall[2] == 3
                ? "PASS Logo限长-小文件完整返回" : "FAIL Logo限长-小文件完整返回");
            var logoEmpty = LogoService.ReadCappedAsync(new MemoryStream(Array.Empty<byte>()), LogoService.MaxLogoBytes).GetAwaiter().GetResult();
            lines.Add(logoEmpty != null && logoEmpty.Length == 0
                ? "PASS Logo限长-空流返回空数组" : "FAIL Logo限长-空流返回空数组");
            var logoExact = LogoService.ReadCappedAsync(new MemoryStream(new byte[(int)LogoService.MaxLogoBytes]), LogoService.MaxLogoBytes).GetAwaiter().GetResult();
            lines.Add(logoExact != null && logoExact.Length == LogoService.MaxLogoBytes
                ? "PASS Logo限长-恰达上限放行" : "FAIL Logo限长-恰达上限放行");
            var logoOver1 = LogoService.ReadCappedAsync(new MemoryStream(new byte[(int)LogoService.MaxLogoBytes + 1]), LogoService.MaxLogoBytes).GetAwaiter().GetResult();
            lines.Add(logoOver1 == null
                ? "PASS Logo限长-超限1字节拒收" : "FAIL Logo限长-超限1字节拒收");
            // 超限提前停读：6MB 流被拒收时实际消费不得超过上限 + 一个 8KB 分块
            //（旧实现会读完全部 6MB 才拒收），以流 Position 为证。
            var logoBigStream = new MemoryStream(new byte[6 * 1024 * 1024]);
            var logoBig = LogoService.ReadCappedAsync(logoBigStream, LogoService.MaxLogoBytes).GetAwaiter().GetResult();
            lines.Add(logoBig == null && logoBigStream.Position <= LogoService.MaxLogoBytes + 8192
                ? "PASS Logo限长-超限提前停读" : $"FAIL Logo限长-超限提前停读 实际消费={logoBigStream.Position}");

            // --- 缓存读写（隔离目录，不污染真实缓存） ---
            var testCacheDir = Path.Combine(Path.GetTempPath(), "tivimate_cache_selftest");
            ChannelCache.OverrideDir = testCacheDir;
            var cacheUrl = "https://selftest.example.com/list.m3u8";
            var cacheChannels = new List<ChannelItem> {
                new() { Number = 1, Name = "缓存频道1", Url = "http://c1.m3u8", Group = "测试" }
            };
            ChannelCache.Save(cacheUrl, cacheChannels);
            var cacheLoad = ChannelCache.Load(cacheUrl);
            lines.Add(cacheLoad.Count == 1 && cacheLoad[0].Name == "缓存频道1"
                ? "PASS 缓存读写" : "FAIL 缓存读写");
            // 清理隔离目录
            try { if (Directory.Exists(testCacheDir)) Directory.Delete(testCacheDir, true); } catch { }
            ChannelCache.OverrideDir = null;

            // --- 缓存原子替换与故障注入（隔离目录）：替换失败须保留旧缓存且返回 false ---
            var atomicDir = Path.Combine(Path.GetTempPath(), "tivimate_cache_atomic_selftest");
            try { if (Directory.Exists(atomicDir)) Directory.Delete(atomicDir, true); } catch { }
            ChannelCache.OverrideDir = atomicDir;
            var atomicUrl = "https://selftest.example.com/atomic.m3u8";
            var cacheV1 = new List<ChannelItem> { new() { Number = 1, Name = "版本一", Url = "http://v1.m3u8", Group = "测试" } };
            var cacheV2 = new List<ChannelItem> { new() { Number = 2, Name = "版本二", Url = "http://v2.m3u8", Group = "测试" } };
            lines.Add(ChannelCache.Save(atomicUrl, cacheV1) && ChannelCache.Load(atomicUrl).FirstOrDefault()?.Name == "版本一"
                ? "PASS 缓存原子替换-首次保存" : "FAIL 缓存原子替换-首次保存");
            lines.Add(ChannelCache.Save(atomicUrl, cacheV2) && ChannelCache.Load(atomicUrl).FirstOrDefault()?.Name == "版本二"
                ? "PASS 缓存原子替换-覆盖保存" : "FAIL 缓存原子替换-覆盖保存");
            // 注入故障：独占锁住缓存文件，替换应失败（返回 false）、旧缓存仍可读、临时文件被清理
            var atomicFiles = Directory.GetFiles(atomicDir, "*.json");
            var lockFailOk = false; var lockKeepOk = false; var lockTmpOk = false;
            if (atomicFiles.Length == 1)
            {
                using (var fsLock = new FileStream(atomicFiles[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    lockFailOk = !ChannelCache.Save(atomicUrl, cacheV1);
                }
                lockKeepOk = ChannelCache.Load(atomicUrl).FirstOrDefault()?.Name == "版本二";
                lockTmpOk = Directory.GetFiles(atomicDir, "*.tmp").Length == 0;
            }
            lines.Add(lockFailOk ? "PASS 缓存故障注入-占用时返回失败" : "FAIL 缓存故障注入-占用时返回失败");
            lines.Add(lockKeepOk ? "PASS 缓存故障注入-旧缓存保留" : "FAIL 缓存故障注入-旧缓存保留");
            lines.Add(lockTmpOk ? "PASS 缓存故障注入-临时文件清理" : "FAIL 缓存故障注入-临时文件清理");
            try { if (Directory.Exists(atomicDir)) Directory.Delete(atomicDir, true); } catch { }
            ChannelCache.OverrideDir = null;

            // --- 缓存净化：null 元素/字段（深查第 7 轮；隔离目录） ---
            // 磁盘缓存是外部输入：[null]、{"Name": null}、{"Group": null}、无 Url 条目
            // 均合法 JSON 但会让 AdoptChannels/ApplyFilter 空引用。Load 须丢弃不可播条目、
            // 其余字段归一化，与 M3uParser 产出约定一致；正常缓存往返不受影响。
            var sanitizeDir = Path.Combine(Path.GetTempPath(), "tivimate_cache_sanitize_selftest");
            try { if (Directory.Exists(sanitizeDir)) Directory.Delete(sanitizeDir, true); } catch { }
            ChannelCache.OverrideDir = sanitizeDir;
            Directory.CreateDirectory(sanitizeDir);
            var sanitizeUrl = "https://selftest.example.com/sanitize.m3u8";
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var hash = Convert.ToHexString(sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sanitizeUrl)));
                File.WriteAllText(Path.Combine(sanitizeDir, hash + ".json"),
                    "[null, {\"Number\": 1, \"Name\": \"正常频道\", \"Url\": \"http://ok.m3u8\", \"Group\": \"测试\"}, " +
                    "{\"Number\": 2, \"Name\": null, \"Url\": \"http://noname.m3u8\", \"Group\": null}, " +
                    "{\"Number\": 3, \"Name\": \"无地址\", \"Url\": \"\", \"Group\": \"测试\"}, " +
                    "{\"Number\": 4, \"Name\": \"  \", \"Url\": \"http://blank.m3u8\", \"Group\": \"  \"}]");
            }
            var sanitized = ChannelCache.Load(sanitizeUrl);
            lines.Add(sanitized.Count == 3 && sanitized[0].Name == "正常频道" && sanitized[0].Group == "测试"
                ? "PASS 缓存净化-有效条目保留" : $"FAIL 缓存净化-有效条目保留 实际={sanitized.Count}");
            lines.Add(sanitized.Count == 3 && sanitized[1].Name == "http://noname.m3u8" && sanitized[1].Group == "未分组"
                ? "PASS 缓存净化-null名称分组归一化" : "FAIL 缓存净化-null名称分组归一化");
            lines.Add(sanitized.Count == 3 && sanitized[2].Url == "http://blank.m3u8" && sanitized[2].Name == "http://blank.m3u8" && sanitized[2].Group == "未分组"
                ? "PASS 缓存净化-空白名称分组归一化且无地址条目丢弃" : "FAIL 缓存净化-空白名称分组归一化且无地址条目丢弃");
            lines.Add(sanitized.All(c => c != null) && sanitized.All(c => !string.IsNullOrWhiteSpace(c.Url))
                ? "PASS 缓存净化-结果无null元素与无地址条目" : "FAIL 缓存净化-结果含null或无地址条目");
            try { if (Directory.Exists(sanitizeDir)) Directory.Delete(sanitizeDir, true); } catch { }
            ChannelCache.OverrideDir = null;

            // --- 数字键换台匹配逻辑（V1.4，纯逻辑） ---
            var digitList = new List<ChannelItem>
            {
                new() { Number = 1, Name = "CCTV-1", Url = "http://d1.m3u8" },
                new() { Number = 12, Name = "CCTV-12", Url = "http://d12.m3u8" },
                new() { Number = 125, Name = "卫视125", Url = "http://d125.m3u8" },
            };
            lines.Add(DigitTuner.AppendDigit("", '1') == "1" ? "PASS 数字缓冲-追加" : "FAIL 数字缓冲-追加");
            lines.Add(DigitTuner.AppendDigit("1234", '5') == "2345" ? "PASS 数字缓冲-超长截断" : "FAIL 数字缓冲-超长截断");
            lines.Add(DigitTuner.AppendDigit("12", 'x') == "12" ? "PASS 数字缓冲-非数字忽略" : "FAIL 数字缓冲-非数字忽略");
            lines.Add(DigitTuner.Match(digitList, "12")?.Name == "CCTV-12" ? "PASS 数字匹配-精确" : "FAIL 数字匹配-精确");
            lines.Add(DigitTuner.Match(digitList, "999") == null ? "PASS 数字匹配-无号返回空" : "FAIL 数字匹配-无号返回空");
            lines.Add(DigitTuner.HasLongerNumber(digitList, "1") && DigitTuner.HasLongerNumber(digitList, "12") && !DigitTuner.HasLongerNumber(digitList, "125") && !DigitTuner.HasLongerNumber(digitList, "5")
                ? "PASS 数字匹配-更长号码判定" : "FAIL 数字匹配-更长号码判定");
            lines.Add(!DigitTuner.ShouldCommit(digitList, "1") && !DigitTuner.ShouldCommit(digitList, "12") && DigitTuner.ShouldCommit(digitList, "125") && !DigitTuner.ShouldCommit(digitList, "5")
                ? "PASS 数字匹配-立即提交判定" : "FAIL 数字匹配-立即提交判定");

            // --- 窗口位置校验（V1.4，基于当前虚拟桌面相对坐标，单屏/多屏均可断言） ---
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            lines.Add(WindowPlacement.CanRestore(vl + vw / 2 - 400, vt + vh / 2 - 300, 800, 600)
                ? "PASS 窗口校验-桌面中心可恢复" : "FAIL 窗口校验-桌面中心可恢复");
            lines.Add(!WindowPlacement.CanRestore(vl - 5000, vt, 800, 600)
                ? "PASS 窗口校验-屏幕外拒绝" : "FAIL 窗口校验-屏幕外拒绝");
            lines.Add(!WindowPlacement.CanRestore(vl - 700, vt + 100, 800, 600)
                ? "PASS 窗口校验-可见面积过小拒绝" : "FAIL 窗口校验-可见面积过小拒绝");
            lines.Add(!WindowPlacement.CanRestore(double.NaN, vt, 800, 600) &&
                      !WindowPlacement.CanRestore(vl, vt, double.PositiveInfinity, 600)
                ? "PASS 窗口校验-非有限值拒绝" : "FAIL 窗口校验-非有限值拒绝");
            var clamped = WindowPlacement.ClampToVisible(vl - 100, vt - 50, 800, 600);
            lines.Add(clamped.Left == vl && clamped.Top == vt && clamped.Width == 800 && clamped.Height == 600
                ? "PASS 窗口钳制-左上出屏压回屏内" : "FAIL 窗口钳制-左上出屏压回屏内");
            var clampedR = WindowPlacement.ClampToVisible(vl + vw - 100, vt, 800, 600);
            lines.Add(clampedR.Left == vl + vw - 800 && clampedR.Top == vt
                ? "PASS 窗口钳制-右侧出屏压回屏内" : "FAIL 窗口钳制-右侧出屏压回屏内");
            lines.Add(AppConfig.StripCredentialsForCacheKey("http://user:pass@host/logo.png?username=u&password=p&type=x#frag")
                      == "http://host/logo.png?type=x"
                ? "PASS 缓存键脱敏-凭据与fragment剥离" : "FAIL 缓存键脱敏-凭据与fragment剥离");
            lines.Add(AppConfig.StripCredentialsForCacheKey("http://host/logo.png?username=u") == "http://host/logo.png" &&
                      AppConfig.StripCredentialsForCacheKey("http://host/a.png?id=1") == "http://host/a.png?id=1"
                ? "PASS 缓存键脱敏-仅用户名剥离且非凭据参数保留" : "FAIL 缓存键脱敏-仅用户名剥离且非凭据参数保留");

            // --- 配置序列化 ---
            var cfg = new AppConfig { ActivePlaylist = "测试源", Volume = 55 };
            cfg.Playlists.Add(new PlaylistSource { Name = "测试源", Url = "http://test.m3u8" });
            var cfgPath = Path.Combine(Path.GetTempPath(), "tivimate_cfg_selftest.json");
            try
            {
                File.WriteAllText(cfgPath, System.Text.Json.JsonSerializer.Serialize(cfg));
                var cfgLoad = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath));
                lines.Add(cfgLoad != null && cfgLoad.ActivePlaylist == "测试源" && cfgLoad.Volume == 55 && cfgLoad.Playlists.Count == 1
                    ? "PASS 配置序列化" : "FAIL 配置序列化");
            }
            catch (Exception ex) { lines.Add("FAIL 配置序列化: " + ex.Message); }
            finally { try { File.Delete(cfgPath); } catch { } }

            // --- 配置保存：DPAPI 落盘往返与失败恢复（隔离目录） ---
            var cfgSaveDir = Path.Combine(Path.GetTempPath(), "tivimate_cfg_save_selftest");
            try { if (Directory.Exists(cfgSaveDir)) Directory.Delete(cfgSaveDir, true); } catch { }
            AppConfig.OverrideDir = cfgSaveDir;
            var cfgSave = new AppConfig { Volume = 66, XtreamPassword = "selftest-secret" };
            var cfgSaveOk = cfgSave.Save();
            var cfgSaveFile = Path.Combine(cfgSaveDir, "config.json");
            var cfgRaw = File.Exists(cfgSaveFile) ? File.ReadAllText(cfgSaveFile) : "";
            lines.Add(cfgSaveOk && cfgRaw.Contains("DPAPI:") && !cfgRaw.Contains("selftest-secret")
                ? "PASS 配置保存-落盘为DPAPI密文" : "FAIL 配置保存-落盘为DPAPI密文");
            lines.Add(cfgSave.XtreamPassword == "selftest-secret"
                ? "PASS 配置保存-成功后内存恢复明文" : "FAIL 配置保存-成功后内存恢复明文");
            var cfgReload = AppConfig.Load();
            lines.Add(cfgReload.XtreamPassword == "selftest-secret" && cfgReload.Volume == 66
                ? "PASS 配置保存-重新加载解密一致" : "FAIL 配置保存-重新加载解密一致");
            // 注入故障：独占锁住配置文件，保存应返回 false、内存仍为明文、旧文件字节不变
            var cfgGoodBytes = File.ReadAllBytes(cfgSaveFile);
            var cfgFailOk = false; var cfgMemOk = false; var cfgFileOk = false;
            using (var fsLock2 = new FileStream(cfgSaveFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                cfgFailOk = !cfgSave.Save();
                cfgMemOk = cfgSave.XtreamPassword == "selftest-secret";
            }
            cfgFileOk = File.ReadAllBytes(cfgSaveFile).SequenceEqual(cfgGoodBytes);
            lines.Add(cfgFailOk ? "PASS 配置保存-占用时返回失败" : "FAIL 配置保存-占用时返回失败");
            lines.Add(cfgMemOk ? "PASS 配置保存-失败后内存仍为明文" : "FAIL 配置保存-失败后内存仍为明文");
            lines.Add(cfgFileOk ? "PASS 配置保存-失败不破坏旧文件" : "FAIL 配置保存-失败不破坏旧文件");
            try { if (Directory.Exists(cfgSaveDir)) Directory.Delete(cfgSaveDir, true); } catch { }
            AppConfig.OverrideDir = null;

            // --- 配置加载损坏保全与原子保存（隔离目录） ---
            var cfgBadDir = Path.Combine(Path.GetTempPath(), "tivimate_cfg_corrupt_selftest");
            try { if (Directory.Exists(cfgBadDir)) Directory.Delete(cfgBadDir, true); } catch { }
            Directory.CreateDirectory(cfgBadDir);
            AppConfig.OverrideDir = cfgBadDir;
            var cfgBadPath = Path.Combine(cfgBadDir, "config.json");
            // 无文件：返回默认配置且不记错误
            var cfgNone = AppConfig.Load();
            lines.Add(cfgNone.Volume == 80 && AppConfig.LastLoadError == null
                ? "PASS 配置加载-无文件返回默认且无错误" : "FAIL 配置加载-无文件返回默认且无错误");
            // 写入被截断的 JSON（内含 Volume=42，若被误当有效配置会暴露），Load 必须走损坏路径
            var corruptText = "{\"Volume\": 42, \"Playlists\": [{\"Name\": \"x\"";
            File.WriteAllText(cfgBadPath, corruptText);
            var cfgBad = AppConfig.Load();
            lines.Add(cfgBad.Volume == 80 && cfgBad.Playlists.Count == 0
                ? "PASS 配置加载-损坏返回默认配置" : "FAIL 配置加载-损坏返回默认配置");
            var preservedFiles = Directory.GetFiles(cfgBadDir, "config.corrupt-*.json");
            lines.Add(preservedFiles.Length == 1 && File.ReadAllText(preservedFiles[0]) == corruptText
                ? "PASS 配置加载-损坏原文件被保全" : "FAIL 配置加载-损坏原文件被保全");
            lines.Add(!string.IsNullOrEmpty(AppConfig.LastLoadError) && AppConfig.LastLoadError.Contains("config.corrupt-")
                ? "PASS 配置加载-损坏错误可见" : "FAIL 配置加载-损坏错误可见");
            // 损坏后保存默认配置：保全文件必须原样保留，新配置可正常往返且错误状态清除
            cfgBad.Volume = 81;
            var cfgBadSaveOk = cfgBad.Save();
            lines.Add(cfgBadSaveOk && preservedFiles.Length == 1 && File.Exists(preservedFiles[0]) && File.ReadAllText(preservedFiles[0]) == corruptText
                ? "PASS 配置加载-损坏后保存不丢保全" : "FAIL 配置加载-损坏后保存不丢保全");
            var cfgAfterBad = AppConfig.Load();
            lines.Add(cfgAfterBad.Volume == 81 && AppConfig.LastLoadError == null
                ? "PASS 配置保存-损坏恢复后往返一致" : "FAIL 配置保存-损坏恢复后往返一致");
            // 原子替换：对已存在的 config.json 再保存（走 File.Replace 路径），且无 config.*.tmp 临时残留与 .bak 残留
            // F3：保存临时名已改为 config.<pid>.<guid>.tmp，故按通配符检查，不再断言固定 config.json.tmp 不存在。
            cfgAfterBad.Volume = 82;
            var cfgAtomicOk = cfgAfterBad.Save();
            var cfgAtomicReload = AppConfig.Load();
            lines.Add(cfgAtomicOk && cfgAtomicReload.Volume == 82
                && Directory.GetFiles(cfgBadDir, "config.*.tmp").Length == 0 && !File.Exists(cfgBadPath + ".bak")
                ? "PASS 配置保存-原子替换往返无残留" : "FAIL 配置保存-原子替换往返无残留");
            try { if (Directory.Exists(cfgBadDir)) Directory.Delete(cfgBadDir, true); } catch { }
            AppConfig.OverrideDir = null;

            // --- 配置读盘失败（F4，隔离目录；与损坏分支分开断言） ---
            // 独占锁定 config.json 后调 Load()：必须走读盘失败分支——不产生保全、
            // 原文件字节不变、LastLoadError 非空且不含“损坏”字样、返回默认配置；
            // 释放锁后再次 Load() 能读回原值且错误清除，证明原文件未被破坏、可恢复。
            // 注意：锁定期间不调 Save()（ReadFailurePending 为真时 Save 会弹确认框阻塞自检）。
            var cfgReadDir = Path.Combine(Path.GetTempPath(), "tivimate_cfg_readfail_selftest");
            try { if (Directory.Exists(cfgReadDir)) Directory.Delete(cfgReadDir, true); } catch { }
            Directory.CreateDirectory(cfgReadDir);
            AppConfig.OverrideDir = cfgReadDir;
            var cfgReadPath = Path.Combine(cfgReadDir, "config.json");
            File.WriteAllText(cfgReadPath, "{\"Volume\": 43}");
            var cfgReadBytes = File.ReadAllBytes(cfgReadPath);
            AppConfig cfgRead;
            using (var fsLock3 = new FileStream(cfgReadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                cfgRead = AppConfig.Load();
            }
            var readErr = AppConfig.LastLoadError ?? "";
            lines.Add(cfgRead.Volume == 80
                ? "PASS 配置读盘失败-返回默认配置" : "FAIL 配置读盘失败-返回默认配置");
            lines.Add(!string.IsNullOrEmpty(readErr) && !readErr.Contains("损坏")
                ? "PASS 配置读盘失败-错误可见且不与损坏混淆" : "FAIL 配置读盘失败-错误可见且不与损坏混淆");
            lines.Add(Directory.GetFiles(cfgReadDir, "config.corrupt-*.json").Length == 0
                ? "PASS 配置读盘失败-不产生损坏保全文件" : "FAIL 配置读盘失败-不产生损坏保全文件");
            lines.Add(File.ReadAllBytes(cfgReadPath).SequenceEqual(cfgReadBytes)
                ? "PASS 配置读盘失败-原文件字节不变" : "FAIL 配置读盘失败-原文件字节不变");
            var cfgReadOk = AppConfig.Load();
            lines.Add(cfgReadOk.Volume == 43 && AppConfig.LastLoadError == null
                ? "PASS 配置读盘失败-释放锁后可恢复读回原值" : "FAIL 配置读盘失败-释放锁后可恢复读回原值");
            try { if (Directory.Exists(cfgReadDir)) Directory.Delete(cfgReadDir, true); } catch { }
            AppConfig.OverrideDir = null;

            // --- 凭据解密失败提示（隔离目录；与损坏分支分开断言） ---
            // 写入合法 JSON 但含无法 Base64 解码的 DPAPI 串：Load 必须走解密失败分支，
            // 而非损坏分支——其余设置保留、密码清空、LastLoadError 非空且不提损坏/保全、
            // 不产生 config.corrupt-* 保全文件；成功加载后错误状态清除。
            var cfgDecDir = Path.Combine(Path.GetTempPath(), "tivimate_cfg_decrypt_selftest");
            try { if (Directory.Exists(cfgDecDir)) Directory.Delete(cfgDecDir, true); } catch { }
            Directory.CreateDirectory(cfgDecDir);
            AppConfig.OverrideDir = cfgDecDir;
            var cfgDecPath = Path.Combine(cfgDecDir, "config.json");
            File.WriteAllText(cfgDecPath, "{\"Volume\": 77, \"XtreamPassword\": \"DPAPI:!!!not-base64!!!\"}");
            var cfgDec = AppConfig.Load();
            var decErr = AppConfig.LastLoadError ?? "";
            lines.Add(cfgDec.Volume == 77 && cfgDec.XtreamPassword == null
                ? "PASS 凭据解密失败-其余设置保留且密码清空" : "FAIL 凭据解密失败-其余设置保留且密码清空");
            lines.Add(!string.IsNullOrEmpty(decErr) && decErr.Contains("重新输入")
                    && !decErr.Contains("配置文件损坏") && !decErr.Contains("config.corrupt-")
                ? "PASS 凭据解密失败-错误可见且不与损坏混淆" : "FAIL 凭据解密失败-错误可见且不与损坏混淆");
            lines.Add(Directory.GetFiles(cfgDecDir, "config.corrupt-*.json").Length == 0
                ? "PASS 凭据解密失败-不产生损坏保全文件" : "FAIL 凭据解密失败-不产生损坏保全文件");
            File.WriteAllText(cfgDecPath, "{\"Volume\": 78}");
            var cfgDecOk = AppConfig.Load();
            lines.Add(cfgDecOk.Volume == 78 && AppConfig.LastLoadError == null
                ? "PASS 凭据解密失败-后续成功加载清除错误" : "FAIL 凭据解密失败-后续成功加载清除错误");
            try { if (Directory.Exists(cfgDecDir)) Directory.Delete(cfgDecDir, true); } catch { }
            AppConfig.OverrideDir = null;

            // --- 配置加载 null 集合归一化（深查第 6 轮；隔离目录） ---
            // JSON 显式 null 会覆盖属性初始化器：{"Playlists": null} 反序列化后
            // Playlists 即 null，而调用方均未判空，启动即 NullReferenceException。
            // Load 须把三个集合字段的 null 归一化为空集合，与字段缺失语义一致，
            // 且不走损坏保全分支（文件本身合法）；其余设置保留、保存后可往返。
            var cfgNullDir = Path.Combine(Path.GetTempPath(), "tivimate_cfg_nullcoll_selftest");
            try { if (Directory.Exists(cfgNullDir)) Directory.Delete(cfgNullDir, true); } catch { }
            Directory.CreateDirectory(cfgNullDir);
            AppConfig.OverrideDir = cfgNullDir;
            var cfgNullPath = Path.Combine(cfgNullDir, "config.json");
            File.WriteAllText(cfgNullPath, "{\"Volume\": 43, \"Playlists\": null, \"Favorites\": null, \"HiddenChannels\": null}");
            var cfgNull = AppConfig.Load();
            lines.Add(cfgNull.Volume == 43 && cfgNull.Playlists != null && cfgNull.Playlists.Count == 0 &&
                      cfgNull.Favorites != null && cfgNull.Favorites.Count == 0 &&
                      cfgNull.HiddenChannels != null && cfgNull.HiddenChannels.Count == 0
                ? "PASS 配置加载-null集合归一化为空且其余设置保留" : "FAIL 配置加载-null集合归一化为空且其余设置保留");
            lines.Add(AppConfig.LastLoadError == null && Directory.GetFiles(cfgNullDir, "config.corrupt-*.json").Length == 0
                ? "PASS 配置加载-null集合不记错误且不产生损坏保全" : "FAIL 配置加载-null集合不记错误且不产生损坏保全");
            // 归一化后保存可往返：再加载仍为有效配置、Volume 保留、集合仍为空
            cfgNull.Volume = 44;
            var cfgNullSaveOk = cfgNull.Save();
            var cfgNullReload = AppConfig.Load();
            lines.Add(cfgNullSaveOk && cfgNullReload.Volume == 44 && cfgNullReload.Playlists != null &&
                      cfgNullReload.Playlists.Count == 0 && AppConfig.LastLoadError == null
                ? "PASS 配置加载-null集合保存后往返一致" : "FAIL 配置加载-null集合保存后往返一致");
            // 回归守卫：正常非空集合不受归一化影响
            File.WriteAllText(cfgNullPath, "{\"Volume\": 45, \"Playlists\": [{\"Name\": \"x\", \"Url\": \"http://x.m3u8\"}], \"Favorites\": [\"http://f.m3u8\"], \"HiddenChannels\": [\"http://h.m3u8\"]}");
            var cfgKeep = AppConfig.Load();
            lines.Add(cfgKeep.Volume == 45 && cfgKeep.Playlists.Count == 1 && cfgKeep.Playlists[0].Name == "x" &&
                      cfgKeep.Favorites.Count == 1 && cfgKeep.HiddenChannels.Count == 1
                ? "PASS 配置加载-正常集合不受归一化影响" : "FAIL 配置加载-正常集合不受归一化影响");
            // --- 配置加载 null 元素归一化（深查第 8 轮；复用同一隔离目录） ---
            // {"Playlists": [null]} 的 null 元素会让随后的 p.Url 空引用，落进通用 catch
            // 被误判为“配置文件损坏”（原文件被移走、当轮设置全丢）；空 Url 的源还会在
            // ChannelCache.Load 的 GetBytes(null) 抛错且缓存采纳在 try 之外，启动即弹框。
            // Load 须丢弃 null 元素、把 null Name/Url 归一化为空串（保留条目），
            // Favorites/HiddenChannels 的 null 串同样丢弃，且不记错误、不产生损坏保全。
            File.WriteAllText(cfgNullPath, "{\"Volume\": 46, \"Playlists\": [null, {\"Name\": null, \"Url\": null}, {\"Name\": \"ok\", \"Url\": \"http://ok.m3u8\"}], \"Favorites\": [\"http://f.m3u8\", null], \"HiddenChannels\": [null]}");
            var cfgNullEl = AppConfig.Load();
            lines.Add(cfgNullEl.Volume == 46 && cfgNullEl.Playlists.Count == 2 &&
                      cfgNullEl.Playlists[0].Name == "" && cfgNullEl.Playlists[0].Url == "" &&
                      cfgNullEl.Playlists[1].Name == "ok" && cfgNullEl.Playlists[1].Url == "http://ok.m3u8" &&
                      cfgNullEl.Favorites.Count == 1 && cfgNullEl.HiddenChannels.Count == 0
                ? "PASS 配置加载-null元素丢弃且null字段归一化" : "FAIL 配置加载-null元素丢弃且null字段归一化");
            lines.Add(AppConfig.LastLoadError == null && Directory.GetFiles(cfgNullDir, "config.corrupt-*.json").Length == 0
                ? "PASS 配置加载-null元素不记错误且不产生损坏保全" : "FAIL 配置加载-null元素不记错误且不产生损坏保全");
            try { if (Directory.Exists(cfgNullDir)) Directory.Delete(cfgNullDir, true); } catch { }
            AppConfig.OverrideDir = null;

            // --- 播放测试 ---
            if (playTest)
            {
                if (string.IsNullOrEmpty(url))
                {
                    lines.Add("SKIP 播放测试（未提供URL）");
                }
                else
                {
                    using var mp = new MediaPlayer(libvlc);
                    using var media = url.Contains("://")
                        ? new Media(libvlc, new Uri(url))
                        : new Media(libvlc, url, FromType.FromPath);
                    media.AddOption(":network-caching=800");
                    mp.Play(media);

                    var sw = Stopwatch.StartNew();
                    var played = false;
                    while (sw.Elapsed < TimeSpan.FromSeconds(9))
                    {
                        Thread.Sleep(300);
                        if (mp.IsPlaying) { played = true; break; }
                    }
                    Thread.Sleep(2000);
                    lines.Add(played && mp.Time >= 0
                        ? $"PASS 播放测试 已推流 {mp.Time}ms"
                        : $"FAIL 播放测试 IsPlaying={played} Time={mp.Time}ms");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("FAIL 异常: " + ex.Message);
        }
        lines.Add("DONE");
        File.WriteAllLines(log, lines);
    }
}
