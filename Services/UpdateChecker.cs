using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TiviMateWin.Services;

/// <summary>检查更新（v1.4.35：用户不知道自己在用哪个版本、也找不到更新入口）。
/// 查 GitHub ciweihaha/TiviMateWin 的最新 release，和当前版本比。
/// 查版本走 GitHub API（直连失败时依次走 gh-proxy / ghfast 镜像）；
/// 下安装包走国内友好的镜像（ghfast 优先，gh-proxy 备用，直链最后），
/// 不走家里打不开的 GitHub 直链。网络部分在内，纯规则可单测。</summary>
public static class UpdateChecker
{
    public const string RepoOwner = "ciweihaha";
    public const string RepoName = "TiviMateWin";
    public const string AssetName = "TiviMateWin-win-x64.zip";

    /// <summary>GitHub API：最新 release（含 tag_name）。</summary>
    public const string ApiUrl = "https://api.github.com/repos/ciweihaha/TiviMateWin/releases/latest";
    private const string GhProxyApiUrl = "https://gh-proxy.com/https://api.github.com/repos/ciweihaha/TiviMateWin/releases/latest";
    private const string GhFastApiUrl = "https://ghfast.top/https://api.github.com/repos/ciweihaha/TiviMateWin/releases/latest";

    private const int CheckTimeoutSeconds = 10;
    private const long MaxDownloadBytes = 600L * 1024 * 1024;

    private static readonly HttpClient CheckClient = new() { Timeout = TimeSpan.FromSeconds(CheckTimeoutSeconds + 5) };

    static UpdateChecker()
    {
        // GitHub API 必须带 User-Agent，否则 403。
        CheckClient.DefaultRequestHeaders.UserAgent.ParseAdd("TiviMateWin");
    }

    public sealed record CheckResult(string? LatestTag, bool IsUpdateAvailable, string? Error);

    /// <summary>从 releases/latest 的 JSON 里取 tag_name（如 "v1.4.36"）；取不到返回 null。</summary>
    public static string? ParseLatestTag(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
            {
                var value = tag.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>最新版是否比当前版新：tag 前的 v 去掉再按 Version 比；
    /// 任一解析失败都当作“没有新版”（不误报）。</summary>
    public static bool IsNewer(string currentDisplay, string? latestTag)
    {
        if (string.IsNullOrWhiteSpace(latestTag)) return false;
        var latest = latestTag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(currentDisplay, out var current)) return false;
        if (!Version.TryParse(latest, out var latestVersion)) return false;
        return latestVersion > current;
    }

    /// <summary>某版本的安装包下载地址：直链、ghfast、gh-proxy 三条（下载时按此顺序试）。</summary>
    public static (string Direct, string GhFast, string GhProxy) DownloadUrls(string tag)
    {
        var path = $"ciweihaha/TiviMateWin/releases/download/{tag}/{AssetName}";
        return (
            Direct: $"https://github.com/{path}",
            GhFast: $"https://ghfast.top/https://github.com/{path}",
            GhProxy: $"https://gh-proxy.com/https://github.com/{path}");
    }

    /// <summary>查最新版本：直连 API → gh-proxy → ghfast，拿到 tag 即停；
    /// 三条都失败返回 Error（界面显示“检查失败”，不弹错）。</summary>
    public static async Task<CheckResult> CheckAsync(string currentDisplay, CancellationToken ct = default)
    {
        foreach (var url in new[] { ApiUrl, GhProxyApiUrl, GhFastApiUrl })
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));
                var json = await CheckClient.GetStringAsync(url, attempt.Token).ConfigureAwait(false);
                var tag = ParseLatestTag(json);
                if (tag is null) continue;
                return new CheckResult(tag, IsNewer(currentDisplay, tag), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error($"check update failed [{url}]: {ex.Message}");
            }
        }
        return new CheckResult(null, false, "检查失败（网络不通），稍后再试");
    }

    /// <summary>下载指定版本的安装包到 destPath：ghfast → gh-proxy → 直链，
    /// 拿到完整文件即停；超 600MB 或三条都失败返回 false（残文件已删）。</summary>
    public static async Task<bool> DownloadAsync(string tag, string destPath, CancellationToken ct = default)
    {
        var urls = DownloadUrls(tag);
        foreach (var url in new[] { urls.GhFast, urls.GhProxy, urls.Direct })
        {
            try
            {
                // 下载 152MB 的包在慢网上要很久：单开一个长超时客户端，不用查版本那个 10 秒超时。
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("TiviMateWin");
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is long len && len > MaxDownloadBytes) continue;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) throw new InvalidDataException("download exceeds size limit");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                if (total == 0) throw new InvalidDataException("empty download");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error($"download update failed [{url}]: {ex.Message}");
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            }
        }
        return false;
    }
}
