using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace TiviMateWin.Services;

/// <summary>
/// 频道 Logo 加载服务（V1.4 新增）：内存缓存 → 磁盘缓存 → 网络，三级取图。
/// - 列表虚拟化下 DataTemplate 反复实例化，靠内存缓存保证不重复下载；
/// - 磁盘缓存放 %AppData%\TiviMateWin\logos，二次启动秒出图；
/// - 失败走内存负缓存（null），同一会话内不重复请求坏地址；
/// - 返回 Freeze 后的 BitmapImage，可在后台线程解码、UI 线程直接赋值；
/// - 解码宽 40px（2x 显示密度足够 20px 列表项），大图不占内存。
/// </summary>
public static class LogoService
{
    /// <summary>SelfTest 隔离目录；null = 默认 %AppData%\TiviMateWin\logos</summary>
    public static string? OverrideDir { get; set; }

    private static string Dir => OverrideDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiviMateWin", "logos");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> Cache = new();
    private static readonly SemaphoreSlim NetGate = new(4, 4);   // 并发下载限流，列表滚动不撑满连接池

    /// <summary>取频道 Logo；失败/无地址返回 null（UI 显示首字符占位）。任务缓存放大：同 URL 并发只发一次。</summary>
    public static Task<BitmapImage?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<BitmapImage?>(null);
        return Cache.GetOrAdd(url, LoadCoreAsync);
    }

    private static async Task<BitmapImage?> LoadCoreAsync(string url)
    {
        // 1) 磁盘缓存
        var file = DiskPath(url);
        try
        {
            if (File.Exists(file))
            {
                var fromDisk = Decode(await File.ReadAllBytesAsync(file));
                if (fromDisk != null) return fromDisk;
                try { File.Delete(file); } catch { }   // 损坏缓存：删除后走网络
            }
        }
        catch { /* 读盘失败不致命，继续走网络 */ }

        // 2) 网络（限流 + 不抛出：失败返回 null 走负缓存）
        try
        {
            await NetGate.WaitAsync();
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                var bmp = Decode(bytes);
                if (bmp != null)
                {
                    try
                    {
                        Directory.CreateDirectory(Dir);
                        await File.WriteAllBytesAsync(file, bytes);
                    }
                    catch { /* 写盘失败不影响本次显示 */ }
                }
                return bmp;
            }
            finally { NetGate.Release(); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PNG/JPG 解码为 40px 宽缩略图并 Freeze（可跨线程使用）。</summary>
    private static BitmapImage? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024) return null;   // >5MB 视为异常文件
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 40;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static string DiskPath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        // 扩展名直接从 URL 尾段推断，不构造 Uri（畸形地址不能让缓存键抛异常）
        var tail = url;
        var cut = tail.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) tail = tail[..cut];
        var ext = Path.GetExtension(tail).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg")) ext = ".img";
        return Path.Combine(Dir, hash + ext);
    }
}
