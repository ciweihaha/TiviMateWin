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
            // 原子替换：对已存在的 config.json 再保存（走 File.Replace 路径），且无 .tmp/.bak 残留
            cfgAfterBad.Volume = 82;
            var cfgAtomicOk = cfgAfterBad.Save();
            var cfgAtomicReload = AppConfig.Load();
            lines.Add(cfgAtomicOk && cfgAtomicReload.Volume == 82
                && !File.Exists(cfgBadPath + ".tmp") && !File.Exists(cfgBadPath + ".bak")
                ? "PASS 配置保存-原子替换往返无残留" : "FAIL 配置保存-原子替换往返无残留");
            try { if (Directory.Exists(cfgBadDir)) Directory.Delete(cfgBadDir, true); } catch { }
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
