using System.Net;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>秒断恢复测试（v1.4.36）：CCTV-1 事故——播放列表 200 但首个分片 403，
/// VLC 建不出 demuxer，0–2 秒就 EndReached；重连上限必须管住所有路径，
/// 触顶后秒断死流自动跳下一台（每轮最多 2 次），再不行明确显示“无法播放”。</summary>
public class StreamRecoveryTests
{
    [Fact]
    public void IsFastFail_Within3s_IsFastFail()
    {
        Assert.True(MainWindow.IsFastFail(TimeSpan.FromSeconds(0.5)));
        Assert.True(MainWindow.IsFastFail(TimeSpan.FromSeconds(2)));
        Assert.True(MainWindow.IsFastFail(TimeSpan.FromSeconds(2.9)));
    }

    [Fact]
    public void IsFastFail_After3s_NotFastFail()
    {
        Assert.False(MainWindow.IsFastFail(TimeSpan.FromSeconds(3)));
        Assert.False(MainWindow.IsFastFail(TimeSpan.FromSeconds(10)));
        Assert.False(MainWindow.IsFastFail(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ShouldAutoSkip_FastFail3Times_FirstSkip_Allowed()
    {
        Assert.True(MainWindow.ShouldAutoSkip(fastFailStreak: 3, autoSkipsDone: 0));
        Assert.True(MainWindow.ShouldAutoSkip(fastFailStreak: 5, autoSkipsDone: 1));
    }

    [Fact]
    public void ShouldAutoSkip_TooFewFastFails_NoSkip()
    {
        Assert.False(MainWindow.ShouldAutoSkip(fastFailStreak: 0, autoSkipsDone: 0));
        Assert.False(MainWindow.ShouldAutoSkip(fastFailStreak: 2, autoSkipsDone: 0));
    }

    [Fact]
    public void ShouldAutoSkip_AlreadySkippedTwice_Stops()
    {
        Assert.False(MainWindow.ShouldAutoSkip(fastFailStreak: 3, autoSkipsDone: 2));
        Assert.False(MainWindow.ShouldAutoSkip(fastFailStreak: 9, autoSkipsDone: 2));
    }

    [Fact]
    public void CannotPlayText_ContainsCannotPlay()
    {
        Assert.Contains("无法播放", MainWindow.CannotPlayText);
    }

    [Fact]
    public void TryGetFirstSegmentUrl_AbsoluteFirstSegment()
    {
        const string playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:100\n#EXTINF:5.36,\nhttps://cdn.example.com/a/1.ts?svrtime=1&pid=2\n#EXTINF:5.36,\nhttps://cdn.example.com/a/2.ts?svrtime=1&pid=2\n";
        Assert.True(HlsSegmentProbe.TryGetFirstSegmentUrl(playlist, "https://relay.example.com/ysp/cctv1.m3u8", out var url));
        Assert.Equal("https://cdn.example.com/a/1.ts?svrtime=1&pid=2", url);
    }

    [Fact]
    public void TryGetFirstSegmentUrl_RelativeSegment_ResolvedAgainstPlaylist()
    {
        const string playlist = "#EXTM3U\n#EXTINF:5.0,\nseg-100.ts\n";
        Assert.True(HlsSegmentProbe.TryGetFirstSegmentUrl(playlist, "https://relay.example.com/ysp/cctv1.m3u8", out var url));
        Assert.Equal("https://relay.example.com/ysp/seg-100.ts", url);
    }

    [Fact]
    public void TryGetFirstSegmentUrl_NoSegments_ReturnsFalse()
    {
        Assert.False(HlsSegmentProbe.TryGetFirstSegmentUrl("#EXTM3U\n#EXT-X-ENDLIST\n", "https://h/p.m3u8", out _));
        Assert.False(HlsSegmentProbe.TryGetFirstSegmentUrl("", "https://h/p.m3u8", out _));
        Assert.False(HlsSegmentProbe.TryGetFirstSegmentUrl("   \n\n  ", "https://h/p.m3u8", out _));
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }

    [Fact]
    public async Task ProbeFirstSegmentAsync_Segment403_Reports403()
    {
        const string playlist = "#EXTM3U\n#EXTINF:5.36,\nhttps://cdn.example.com/a/1.ts?svrtime=1\n";
        var handler = new CannedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(".m3u8"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playlist) };
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>forbidden</html>") };
        });
        using var http = new HttpClient(handler);
        var r = await HlsSegmentProbe.ProbeFirstSegmentAsync(http, "https://relay.example.com/ysp/cctv1.m3u8", CancellationToken.None);
        Assert.Equal(200, r.PlaylistStatus);
        Assert.Equal(403, r.SegmentStatus);
        Assert.Null(r.Error);
        Assert.EndsWith("/a/1.ts?svrtime=1", r.SegmentUrl);
    }

    [Fact]
    public async Task ProbeFirstSegmentAsync_SegmentOk_ReportsBytes()
    {
        const string playlist = "#EXTM3U\n#EXTINF:5.36,\nhttps://cdn.example.com/a/1.ts\n";
        var segBytes = new byte[1000];
        segBytes[0] = 0x47; // TS sync byte
        var handler = new CannedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(".m3u8"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playlist) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(segBytes) };
        });
        using var http = new HttpClient(handler);
        var r = await HlsSegmentProbe.ProbeFirstSegmentAsync(http, "https://relay.example.com/ysp/cctv1.m3u8", CancellationToken.None);
        Assert.Equal(200, r.PlaylistStatus);
        Assert.Equal(200, r.SegmentStatus);
        Assert.Equal(1000, r.SegmentBytes);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task ProbeFirstSegmentAsync_Playlist404_ReportsPlaylistError()
    {
        var handler = new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var r = await HlsSegmentProbe.ProbeFirstSegmentAsync(http, "https://relay.example.com/ysp/cctv1.m3u8", CancellationToken.None);
        Assert.Equal(404, r.PlaylistStatus);
        Assert.Null(r.SegmentStatus);
        Assert.NotNull(r.Error);
    }
}
