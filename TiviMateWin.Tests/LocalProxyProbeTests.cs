using System.Net;
using System.Net.Sockets;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>本机代理探测（v1.4.39）：监听返回 --http-proxy 参数，不监听返回 null。</summary>
public class LocalProxyProbeTests
{
    [Fact]
    public void GetHttpProxyArg_Listening_ReturnsArg()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(LocalProxyProbe.IsListening(port));
        Assert.Equal($"--http-proxy=127.0.0.1:{port}", LocalProxyProbe.GetHttpProxyArg(port));
    }

    [Fact]
    public void GetHttpProxyArg_NotListening_ReturnsNull()
    {
        var port = GetFreePort();
        Assert.False(LocalProxyProbe.IsListening(port));
        Assert.Null(LocalProxyProbe.GetHttpProxyArg(port));
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
