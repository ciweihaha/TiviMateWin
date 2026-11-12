using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TiviMateWin.Services;

/// <summary>OpenList API 客户端：登录取 token、列目录、取文件直链（raw_url）。
/// 只走 OpenList 官方 HTTP API（/api/auth/login、/api/fs/list、/api/fs/get），
/// 不实现各网盘官方 API；直链自带签名，直接交给播放器即可。</summary>
public sealed class OpenListService : IDisposable
{
    /// <summary>目录里的一项（文件夹或文件）</summary>
    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public string Path { get; set; } = "";   // 完整路径（/ 开头）
    }

    private readonly HttpClient _http;
    private readonly string _server;   // 已规范化（去尾部 /）
    private readonly string _username;
    private readonly string _password;
    private string? _token;

    public OpenListService(string server, string username, string password)
    {
        _server = NormalizeServerUrl(server);
        _username = username;
        _password = password;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>服务器地址规范化：去空白、去尾部 /</summary>
    public static string NormalizeServerUrl(string? server)
        => (server ?? "").Trim().TrimEnd('/');

    /// <summary>拼接目录与文件名（/ 电影 + a.mp4 → /电影/a.mp4）</summary>
    public static string JoinPath(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || dir == "/") return "/" + name.TrimStart('/');
        return dir.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    /// <summary>上级目录（/电影/动作 → /电影；根目录仍为 /）</summary>
    public static string ParentPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    /// <summary>登录取 token；失败返回 false（不抛错，调用方显示提示即可）</summary>
    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        _token = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _server + "/api/auth/login");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { username = _username, password = _password }),
                Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return false;
            var text = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            // OpenList 返回 {code:200, message:"success", data:{token:"..."}}
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
            if (!data.TryGetProperty("token", out var tok)) return false;
            _token = tok.GetString();
            return !string.IsNullOrEmpty(_token);
        }
        catch { return false; }
    }

    // 带 token 的 POST：401 时重新登录一次再试；网络/解析失败返回 null（不抛错）
    private async Task<JsonDocument?> PostApiAsync(string path, object payload, CancellationToken ct, bool retryLogin = true)
    {
        try
        {
            if (string.IsNullOrEmpty(_token) && !await LoginAsync(ct)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Post, _server + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Authorization", _token);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && retryLogin)
            {
                if (await LoginAsync(ct)) return await PostApiAsync(path, payload, ct, false);
                return null;
            }
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(text);   // 调用方负责 Dispose
        }
        catch { return null; }
    }

    /// <summary>列目录：返回子文件夹与文件（失败返回空列表）</summary>
    public async Task<List<Entry>> ListAsync(string path, CancellationToken ct = default)
    {
        var result = new List<Entry>();
        using var doc = await PostApiAsync("/api/fs/list",
            new { path, password = "", page = 1, per_page = 200, refresh = false }, ct);
        if (doc is null) return result;
        if (!doc.RootElement.TryGetProperty("data", out var data)) return result;
        if (!data.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            var isDir = item.TryGetProperty("is_dir", out var d) && d.GetBoolean();
            var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            result.Add(new Entry { Name = name, IsDir = isDir, Size = size, Path = JoinPath(path, name) });
        }
        return result;
    }

    /// <summary>取文件直链（raw_url，自带签名）；失败返回 null</summary>
    public async Task<string?> GetRawUrlAsync(string path, CancellationToken ct = default)
    {
        using var doc = await PostApiAsync("/api/fs/get", new { path, password = "" }, ct);
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("raw_url", out var raw)) return null;
        var url = raw.GetString();
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    public void Dispose() => _http.Dispose();
}
