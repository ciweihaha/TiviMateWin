using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>无界面自检：--selftest 验证引擎与解析器；--playtest &lt;url&gt; 验证真实播放</summary>
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

            // --- EPG 空输入 ---
            lines.Add(EpgParser.Parse("").Count == 0 ? "PASS EPG空输入" : "FAIL EPG空输入");

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
