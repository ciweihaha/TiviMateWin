using System.IO;
using System.Text.Json;

namespace TiviMateWin.Services;

public class PlaylistSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>应用配置：多播放列表、收藏、音量、播放进度（存于 %AppData%\TiviMateWin）</summary>
public class AppConfig
{
    public List<PlaylistSource> Playlists { get; set; } = new();
    public string? ActivePlaylist { get; set; }
    public string? ActiveChannelUrl { get; set; }
    public int Volume { get; set; } = 80;
    public List<string> Favorites { get; set; } = new();

    // 播放器高级设置
    public int NetworkCache { get; set; } = 800;      // 网络缓存 ms（libVLC network-caching）
    public bool HardwareDecode { get; set; } = true;  // 硬件解码开关
    public string AspectRatio { get; set; } = "";      // 画面比例（空=原始，16:9/4:3/拉伸）
    public double PlaybackRate { get; set; } = 1.0;    // 倍速
    public string Theme { get; set; } = "深色";         // 主题：深色/浅色
    public List<string> HiddenChannels { get; set; } = new();  // 隐藏的频道 URL
    public string? XtreamServer { get; set; }          // Xtream 服务器地址
    public string? XtreamUsername { get; set; }        // Xtream 用户名
    public string? XtreamPassword { get; set; }        // Xtream 密码

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiviMateWin");
    private static string ConfigPath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
