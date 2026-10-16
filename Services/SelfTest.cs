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
