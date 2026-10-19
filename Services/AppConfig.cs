using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    public string? XtreamPassword { get; set; }        // Xtream 密码（DPAPI 加密存储，读取时解密）

    // DPAPI 加密/解密 Xtream 密码：避免明文存盘
    // 存储格式：Base64(DPAPI(UTF8(明文)))，带 "DPAPI:" 前缀便于识别
    // 兼容旧配置：无前缀的旧明文密码直接返回（首次保存时自动加密）
    // SECURITY(fail-closed)：加密失败时抛错，绝不以降级明文回写入配置文件
    private static string? EncryptSecret(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return "DPAPI:" + Convert.ToBase64String(enc);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("DPAPI 加密失败：拒绝以明文保存 Xtream 密码", ex);
        }
    }

    private static string? DecryptSecret(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!stored.StartsWith("DPAPI:", StringComparison.Ordinal)) return stored; // 旧明文
        try
        {
            var bytes = Convert.FromBase64String(stored[6..]);
            var dec = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch (Exception ex)
        {
            // SECURITY(fail-closed)：解密失败（如换了用户/配置被替换）时不静默回退，抛错
            throw new InvalidOperationException("DPAPI 解密失败：存储的 Xtream 密码不可用", ex);
        }
    }
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
                if (cfg != null)
                {
                    try
                    {
                        cfg.XtreamPassword = DecryptSecret(cfg.XtreamPassword); // 解密密码
                    }
                    catch (InvalidOperationException)
                    {
                        // 解密失败时清空密码（需重新输入），绝不把加密串当作密码使用
                        cfg.XtreamPassword = null;
                    }
                    return cfg;
                }
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
            // 保存前加密密码（若已是加密值则不重复加密）
            var plain = XtreamPassword;
            if (!string.IsNullOrEmpty(plain) && !plain.StartsWith("DPAPI:", StringComparison.Ordinal))
                XtreamPassword = EncryptSecret(plain);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            XtreamPassword = plain; // 恢复明文，供本次会话继续使用
        }
        catch { }
    }
}
