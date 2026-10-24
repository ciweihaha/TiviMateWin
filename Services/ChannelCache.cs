using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>频道列表磁盘缓存：秒开上次结果，后台再刷新</summary>
public static class ChannelCache
{
    private static readonly object Gate = new();   // 写锁：多源并发保存时避免文件写冲突

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiviMateWin", "cache");

    /// <summary>测试隔离：SelfTest 可注入临时目录，避免污染真实缓存</summary>
    public static string? OverrideDir { get; set; }

    private static string CacheDir => OverrideDir ?? Dir;

    private static string PathFor(string sourceUrl)
    {
        using var sha = SHA1.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sourceUrl)));
        return Path.Combine(CacheDir, hash + ".json");
    }

    public static List<ChannelItem> Load(string sourceUrl)
    {
        var path = PathFor(sourceUrl);
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<ChannelItem>>(File.ReadAllText(path));
                if (list != null) return list;
                // JSON 为 null（如文件内容就是 null）：与损坏同处理，记日志并移为 .corrupt。
                AppLog.Error($"频道缓存加载失败（{path}，反序列化结果为空），已移为损坏缓存（不含缓存内容）。");
                MoveToCorrupt(path);
            }
        }
        catch (JsonException ex)
        {
            // 损坏缓存：记一条不含缓存内容的日志，并把损坏文件移为 *.corrupt（与配置侧保全同模式），下次不再反复解析损坏文件。
            AppLog.Error($"频道缓存加载失败（{path}）: {ex.Message}");
            MoveToCorrupt(path);
        }
        catch (Exception ex)
        {
            // 瞬时读盘失败等：只记日志（不含缓存内容），不移动原文件，下次仍可重试读取。
            AppLog.Error($"频道缓存加载失败（{path}）: {ex.Message}");
        }
        return new List<ChannelItem>();
    }

    private static void MoveToCorrupt(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var dest = path + $".corrupt-{stamp}";
            var n = 1;
            while (File.Exists(dest)) dest = path + $".corrupt-{stamp}-{n++}";
            File.Move(path, dest);
        }
        catch { }
    }

    /// <summary>
    /// 保存频道缓存。成功返回 true；失败返回 false 并写入日志，旧缓存保持原样可读。
    /// 替换走同目录临时文件 + File.Replace 原子换名：要么整体成功、要么不发生，
    /// 不存在「旧文件已删、新文件未到」的窗口；替换失败不吞异常、不动旧文件。
    /// </summary>
    public static bool Save(string sourceUrl, List<ChannelItem> channels)
    {
        lock (Gate)   // 串行化写盘，避免并发写同一/不同缓存文件时冲突
        {
            var path = PathFor(sourceUrl);
            var tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(CacheDir);
                // 先把完整内容写到同目录（同卷）临时文件，再原子替换正式文件
                File.WriteAllText(tmp, JsonSerializer.Serialize(channels));
                if (File.Exists(path))
                {
                    var bak = path + ".bak";
                    File.Replace(tmp, path, bak);
                    try { File.Delete(bak); } catch { }
                }
                else
                {
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                // 失败可观测：记录日志并向调用方返回 false；旧缓存文件不删除、不移动
                AppLog.Error($"频道缓存保存失败（{path}）: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
