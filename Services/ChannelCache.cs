using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>频道列表磁盘缓存：秒开上次结果，后台再刷新</summary>
public static class ChannelCache
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiviMateWin", "cache");

    private static string PathFor(string sourceUrl)
    {
        using var sha = SHA1.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sourceUrl)));
        return Path.Combine(Dir, hash + ".json");
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
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(sourceUrl), JsonSerializer.Serialize(channels));
        }
        catch { }
    }
}
