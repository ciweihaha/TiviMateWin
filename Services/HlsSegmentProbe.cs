using System.Net.Http;
using System.Threading;

namespace TiviMateWin.Services;

/// <summary>分片探针（v1.4.36）：直播流秒断时，亲自取一次播放列表 + 首个分片，
/// 把真实返回码/耗时打出来——播放列表 200 说明不了什么，分片 403 才是 demuxer
/// 建不出的直接原因（2026-11-19 CCTV-1 事故：列表 200，分片 403，VLC 报
/// [adaptive] Failed to create demuxer，0–2 秒就 EndReached）。
/// 只诊断、不替播放器拿分片；从不抛异常。分片只读前 64KB 验证能出数据，
/// 不用下完整分片。</summary>
public static class HlsSegmentProbe
{
    public sealed record ProbeResult(
        int? PlaylistStatus, int? SegmentStatus, long SegmentMs,
        long SegmentBytes, string? Error, string? SegmentUrl);

    /// <summary>取播放列表里第一个分片地址：跳过 # 开头的标签行；相对地址按播放列表地址解析。</summary>
    public static bool TryGetFirstSegmentUrl(string playlistText, string playlistUrl, out string segmentUrl)
    {
        segmentUrl = "";
        if (string.IsNullOrWhiteSpace(playlistText)) return false;
        Uri? baseUri = null;
        try { baseUri = new Uri(playlistUrl); } catch { }
        foreach (var raw in playlistText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                segmentUrl = line;
                return true;
            }
            // 相对分片地址：按播放列表地址解析
            if (baseUri is not null)
            {
                try { segmentUrl = new Uri(baseUri, line).ToString(); return true; }
                catch { return false; }
            }
            return false;
        }
        return false;
    }

    public static async Task<ProbeResult> ProbeFirstSegmentAsync(HttpClient http, string m3u8Url, CancellationToken ct)
    {
        try
        {
            using var plCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plCts.CancelAfter(TimeSpan.FromSeconds(15));
            string playlist;
            int? plStatus;
            using (var resp = await http.GetAsync(m3u8Url, HttpCompletionOption.ResponseHeadersRead, plCts.Token))
            {
                plStatus = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode)
                    return new ProbeResult(plStatus, null, 0, 0, $"playlist HTTP {(int)resp.StatusCode}", null);
                playlist = await resp.Content.ReadAsStringAsync(plCts.Token);
            }
            if (!TryGetFirstSegmentUrl(playlist, m3u8Url, out var segUrl))
                return new ProbeResult(plStatus, null, 0, 0, "playlist 里没有分片地址", null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                segCts.CancelAfter(TimeSpan.FromSeconds(15));
                using var segResp = await http.GetAsync(segUrl, HttpCompletionOption.ResponseHeadersRead, segCts.Token);
                var segStatus = (int)segResp.StatusCode;
                long bytes = 0;
                if (segResp.IsSuccessStatusCode)
                {
                    await using var s = await segResp.Content.ReadAsStreamAsync(segCts.Token);
                    var buf = new byte[64 * 1024];
                    int n;
                    while (bytes < buf.Length && (n = await s.ReadAsync(buf.AsMemory((int)bytes, buf.Length - (int)bytes), segCts.Token)) > 0)
                        bytes += n;
                }
                sw.Stop();
                return new ProbeResult(plStatus, segStatus, sw.ElapsedMilliseconds, bytes, null, segUrl);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeResult(plStatus, null, sw.ElapsedMilliseconds, 0, ex.Message, segUrl);
            }
        }
        catch (Exception ex)
        {
            return new ProbeResult(null, null, 0, 0, ex.Message, null);
        }
    }
}
