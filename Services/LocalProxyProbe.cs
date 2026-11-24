using System.Net.Sockets;

namespace TiviMateWin.Services;

/// <summary>
/// 本机代理探测（v1.4.39）：笔记本直连 Cloudflare 极慢（直播分片 8.4 KB/s），
/// 本机 127.0.0.1:10809（v2rayN）能到 164 KB/s。10809 在监听才在每次建 Media 时
/// 加 :http-proxy 媒体选项，没有就直连；什么都没改时行为与以前完全一致。
/// 只做 TCP 连通探测，不发任何数据；代理本身好不好用不由这里判断。
/// v1.4.41：引擎级 --http-proxy 已去掉（见下）——引擎级参数一旦带上就改不了，
/// 代理退出后只能重启才直连；媒体选项是每次播放现探的，代理出现/消失都不用重启。
/// </summary>
public static class LocalProxyProbe
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 10809;

    /// <summary>本机指定端口有没有人在监听（默认 300ms 超时；连不上/超时都算没有）。</summary>
    public static bool IsListening(int port = DefaultPort, int timeoutMs = 300)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(DefaultHost, port);
            if (!connect.Wait(TimeSpan.FromMilliseconds(timeoutMs))) return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>代理在监听时返回 LibVLC 媒体选项 :http-proxy=127.0.0.1:10809，否则返回 null（直连）。
    /// VLC 3.0 的 http 接入模块用 var_InheritString 从输入对象继承 http-proxy（modules/access/http.c，
    /// 与 :network-caching 同款机制），所以每次建 Media 时现探现加才生效；引擎创建时的一次性
    /// --http-proxy 只管启动那一刻，代理后启动就赶不上（v1.4.40）。</summary>
    public static string? GetHttpProxyMediaOption(int port = DefaultPort)
        => IsListening(port) ? $":http-proxy={DefaultHost}:{port}" : null;
}
