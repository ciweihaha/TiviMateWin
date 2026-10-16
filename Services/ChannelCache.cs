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
        try
        {
            var path = PathFor(sourceUrl);
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<ChannelItem>>(File.ReadAllText(path));
                if (list != null) return list;
            }
        }
        catch { }
        return new List<ChannelItem>();
    }

    public static void Save(string sourceUrl, List<ChannelItem> channels)
    {
        try
        {
            lock (Gate)   // 串行化写盘，避免并发写同一/不同缓存文件时冲突
            {
                Directory.CreateDirectory(CacheDir);
                // 原子写：先写临时文件再替换，避免写一半崩溃留下损坏缓存
                var path = PathFor(sourceUrl);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(channels));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }
        catch { }
    }
}
