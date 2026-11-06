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

    /// <summary>单张 Logo 解码/落盘上限（与 Decode 的拒收口径同一值）。</summary>
    internal const long MaxLogoBytes = 5 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> Cache = new();
    private static readonly SemaphoreSlim NetGate = new(4, 4);   // 并发下载限流，列表滚动不撑满连接池

    /// <summary>取频道 Logo；失败/无地址返回 null（UI 显示首字符占位）。任务缓存放大：同 URL 并发只发一次。</summary>
    public static Task<BitmapImage?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<BitmapImage?>(null);
        // 内存缓存键用脱敏地址：原 URL 仍用于实际请求（LoadCoreAsync 的 url 参数），
        // 但键本身不再含 username=/password=/userinfo；同图在不同凭据下共享同一缓存项。
        var key = AppConfig.StripCredentialsForCacheKey(url);
        return Cache.GetOrAdd(key, _ => LoadCoreAsync(url));
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
                // 流式限长下载：旧实现 GetByteArrayAsync 先把整段响应体读进内存，
                // Decode 的 5MB 检查才在事后拒收；恶意/失控源（如超大图、gzip 炸弹）
                // 会在拒收前先占满内存。改为按响应头返回后分块读，累计超限立即停读。
                // 超限返回 null，与旧实现“下载完再拒收”的可见结果一致（不解码、不落盘），
                // 且同一 URL 的负缓存语义不变。
                // 注意：ResponseHeadersRead 在收到响应头时即完成，HttpClient.Timeout
                // 此后不再约束读体；故另建一个与 HttpClient 超时同为 8 秒的 CTS，
                // 从请求开始覆盖“响应头 + 读体”全程，保留旧实现的总耗时上限。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var bytes = await ReadCappedAsync(stream, MaxLogoBytes, cts.Token).ConfigureAwait(false);
                if (bytes == null) return null;
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

    /// <summary>
    /// 流式限长读体：8KB 分块读入，累计超过 <paramref name="maxBytes"/> 立即停读并返回 null
    /// （不再消费剩余体）；读完未超限返回完整字节；空体返回长度 0 的数组。
    /// 按解压后字节计数：HttpClient 开了 AutomaticDecompression，交出的流已是解压后形态，
    /// 故 Content-Length（压缩后线长）不能作为放行依据，也不以它提前拒收。
    /// 恰达上限放行（与 Decode 的 <c>bytes.Length &gt; MaxLogoBytes</c> 拒收口径一致）。
    /// </summary>
    internal static async Task<byte[]?> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (total + read > maxBytes) return null;
            await ms.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }
        return ms.ToArray();
    }

    /// <summary>PNG/JPG 解码为 40px 宽缩略图并 Freeze（可跨线程使用）。</summary>
    private static BitmapImage? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxLogoBytes) return null;   // >5MB 视为异常文件
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
        // 磁盘缓存键先脱敏再哈希：不再用带凭据的原始 URL 做 SHA256 输入。
        // 脱敏规则见 AppConfig.StripCredentialsForCacheKey；扩展名推断同样走脱敏地址（行为不变）。
        var keyUrl = AppConfig.StripCredentialsForCacheKey(url);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keyUrl)))[..24].ToLowerInvariant();
        // 扩展名直接从 URL 尾段推断，不构造 Uri（畸形地址不能让缓存键抛异常）
        var tail = keyUrl;
        var cut = tail.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) tail = tail[..cut];
        var ext = Path.GetExtension(tail).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg")) ext = ".img";
        return Path.Combine(Dir, hash + ext);
    }
}
