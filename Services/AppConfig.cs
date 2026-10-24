using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

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

    // 纵深防御：Playlists[].Url 不得把凭据带进 config.json。触发条件是 query 含 password= 时，
    // 同时剥离 username=/password= 两个凭据参数，保留 type/output 等非凭据参数；
    // 若不含 password=，原样返回（不改动普通播放列表地址）。
    internal static string StripCredentialQuery(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var query = url[(q + 1)..];
        var hash = query.IndexOf('#');
        var fragment = hash >= 0 ? query[hash..] : "";
        if (hash >= 0) query = query[..hash];
        var kept = new List<string>();
        var hasPassword = false;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq >= 0 ? part[..eq] : part;
            if (key.Equals("password", StringComparison.OrdinalIgnoreCase)) { hasPassword = true; continue; }
            if (key.Equals("username", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(part);
        }
        if (!hasPassword) return url;
        var baseUrl = url[..q];
        return kept.Count == 0 ? baseUrl + fragment : baseUrl + "?" + string.Join("&", kept) + fragment;
    }

    // 日志脱敏：只留 scheme://host[:port]+path，剥离 query 与 fragment；非绝对地址则剥离 ? 之后部分。
    internal static string RedactUrlForLog(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
            return uri.Scheme + "://" + uri.Host + port + uri.AbsolutePath;
        }
        var q = url.IndexOf('?');
        return q >= 0 ? url[..q] : url;
    }

    // 日志脱敏（自由文本版，与 F1 的 RedactUrlForLog 同规则）：把文本中出现的 http(s) 地址逐个脱敏为 scheme://host[:port]+path，
    // 剥离 query 与 fragment；不含地址的文本原样返回。供全局异常日志等自由文本场景使用
    //（直接对整段异常文本调用 RedactUrlForLog 会因非绝对地址而截断问号之后的内容，故需按地址逐个处理）。
    internal static string RedactUrlsInText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Regex.Replace(text, @"https?://[^\s""'<>]+", m => RedactUrlForLog(m.Value));
    }

    // 请求时重拼 Xtream 凭据 URL：仅当地址是与 XtreamServer 同源的 get.php 占位标识
    //（不含 username=/password=）时，才由 XtreamServer/Username/Password 实时拼出完整地址；
    // 已含凭据或与当前服务器不匹配的地址原样返回，不改动普通播放列表。
    internal static string ResolveXtreamUrl(string url, string? server, string? username, string? password)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(server) ||
            string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return url;
        if (url.Contains("username=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("password=", StringComparison.OrdinalIgnoreCase)) return url;
        var serverBase = server.TrimEnd('/') + "/get.php";
        var q = url.IndexOf('?');
        var baseUrl = q >= 0 ? url[..q] : url;
        if (!baseUrl.Equals(serverBase, StringComparison.OrdinalIgnoreCase)) return url;
        var query = q >= 0 ? url[(q + 1)..] : "";
        var hash = query.IndexOf('#');
        var fragment = hash >= 0 ? query[hash..] : "";
        if (hash >= 0) query = query[..hash];
        var rest = string.IsNullOrEmpty(query) ? "" : "&" + query;
        return baseUrl + "?username=" + Uri.EscapeDataString(username) +
            "&password=" + Uri.EscapeDataString(password) + rest + fragment;
    }

    /// <summary>测试隔离：SelfTest 可注入临时目录，避免污染真实配置</summary>
    public static string? OverrideDir { get; set; }

    private static string Dir => OverrideDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiviMateWin");
    private static string ConfigPath => Path.Combine(Dir, "config.json");

    /// <summary>
    /// 最近一次 Load 的错误说明；加载成功或文件不存在时为 null。
    /// 三种非空情形语义不同、不得混淆：
    /// （1）配置文件损坏：含保全路径 config.corrupt-…，本次以默认配置启动（见 HandleCorruptConfig）；
    /// （2）凭据解密失败：文件本身未损坏、其余设置已正常加载，仅 Xtream 密码被清空、需重新输入。
    /// 调用方可据此向用户提示，避免凭据静默丢失。提示文本不得含“配置文件损坏”或保全路径，以免与损坏分支混淆；
    /// （3）读盘失败：见 HandleReadFailure，文案为“读取失败，原文件未动，请重试”，不得含“损坏”字样，也不得含保全路径。
    /// </summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>最近一次 Load 是否因读盘失败（IO/权限）返回默认配置；为 true 时下一次 Save 前须提示用户确认，避免用默认配置覆盖好文件。</summary>
    public static bool ReadFailurePending { get; private set; }

    public static AppConfig Load()
    {
        LastLoadError = null;
        ReadFailurePending = false;
        if (!File.Exists(ConfigPath))
            return new AppConfig(); // 文件不存在：首次运行，用默认配置（不算错误）
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
            if (cfg == null)
                return HandleCorruptConfig(new JsonException("配置文件内容为空或无法解析为配置对象"));
            try
            {
                cfg.XtreamPassword = DecryptSecret(cfg.XtreamPassword); // 解密密码
            }
            catch (InvalidOperationException)
            {
                // 解密失败时清空密码（需重新输入），绝不把加密串当作密码使用；
                // 并置 LastLoadError + 记脱敏日志，让调用方得知凭据已丢。
                // 注意：此分支与配置文件损坏分支语义不同——文件本身未损坏、其余设置
                // 已正常加载，不走保全流程；提示文本不得含“配置文件损坏”或保全路径，
                // 以免与损坏分支混淆。日志为固定文本，不含密码、密文或异常详情。
                cfg.XtreamPassword = null;
                LastLoadError = "Xtream 登录凭据无法解密，已清空密码，请重新输入密码后保存；配置文件本身未损坏，其余设置已正常加载。";
                AppLog.Error("Xtream 凭据解密失败，已清空密码，需重新输入（日志中不记录密码或密文）。");
            }
            // 纵深防御：已落盘的旧配置若含 password= 的 URL，加载时同样剥离，
            // 避免它在下一次 Save 时重新写回明文；凭据改由 XtreamServer/Username/Password 在请求时重拼。
            if (cfg.Playlists != null)
            {
                foreach (var p in cfg.Playlists)
                {
                    if (!string.IsNullOrEmpty(p.Url) &&
                        p.Url.Contains("password=", StringComparison.OrdinalIgnoreCase))
                        p.Url = StripCredentialQuery(p.Url);
                }
            }
            return cfg;
        }
        catch (JsonException ex)
        {
            return HandleCorruptConfig(ex);
        }
        catch (IOException ex)
        {
            return HandleReadFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return HandleReadFailure(ex);
        }
        catch (Exception ex)
        {
            return HandleCorruptConfig(ex);
        }
    }

    /// <summary>
    /// 配置文件存在但损坏（JSON 截断/格式错误/反序列化空结果）时的兜底：先把原文件保全为
    /// 同目录 config.corrupt-&lt;时间戳&gt;.json（改名失败则复制），记录错误并置 LastLoadError，
    /// 再返回默认配置——原数据不丢，之后保存默认配置也不会把用户原有设置覆盖没。
    /// 注意：瞬时读盘异常（IOException/UnauthorizedAccessException）不走本分支，见 HandleReadFailure。
    /// </summary>
    private static AppConfig HandleCorruptConfig(Exception ex)
    {
        var preserved = "(保全失败)";
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var dest = Path.Combine(Dir, $"config.corrupt-{stamp}.json");
            var n = 1;
            while (File.Exists(dest)) // 同秒多次损坏时避免覆盖更早的保全
                dest = Path.Combine(Dir, $"config.corrupt-{stamp}-{n++}.json");
            try { File.Move(ConfigPath, dest); }
            catch { File.Copy(ConfigPath, dest); } // 改名失败则复制保全，原文件留待后续保存替换
            preserved = dest;
        }
        catch (Exception pex)
        {
            preserved = $"(保全失败: {pex.Message})";
        }
        LastLoadError = $"配置文件损坏，已保全至 {preserved}，本次以默认配置启动。原因: {ex.Message}";
        AppLog.Error($"配置加载失败: {LastLoadError}");
        return new AppConfig();
    }

    /// <summary>
    /// 读盘失败（瞬时 IO/权限异常，如文件被独占锁定）时的分支：与损坏分支严格区分——
    /// 不移动、不复制原文件，只置 LastLoadError 并返回默认配置（内存态），且置 ReadFailurePending，
    /// 下一次 Save 前须提示用户确认，避免用默认配置覆盖好文件。
    /// LastLoadError 文案不得含“损坏”字样，也不得含保全路径。
    /// </summary>
    private static AppConfig HandleReadFailure(Exception ex)
    {
        ReadFailurePending = true;
        LastLoadError = "配置读取失败，原文件未动，请重试。本次以默认配置启动（仅内存态），下一次保存前将提示确认，避免覆盖原文件。";
        AppLog.Error($"配置读取失败，原文件未动: {ex.Message}");
        return new AppConfig();
    }

    // 写锁：与 ChannelCache.Gate 同模式，串行化进程内配置保存，避免并发写同一文件冲突。
    // 边界：锁只解决进程内，跨进程仍是最后写入者胜出（字段级合并未做）；不设单实例 Mutex（需产品层面确认，不在本次授权内）。
    private static readonly object SaveGate = new();

    /// <summary>
    /// 保存配置。成功返回 true；失败返回 false 并记录日志，调用方据此感知失败（不再静默吞掉）。
    /// 内存中的 XtreamPassword 在 finally 中无条件恢复为保存前的明文——无论写盘成功、
    /// 写盘失败还是加密抛错，都不会让 DPAPI 密文串留在会话里冒充密码。
    /// 序列化前还会剥离 Playlists[].Url 中含 password= 的凭据参数（见 StripCredentialQuery），
    /// 仅影响落盘内容；finally 中把内存中的原始 Url 一并恢复。
    /// 临时文件不再用固定 config.json.tmp，改为 config.&lt;pid&gt;.&lt;guid&gt;.tmp，避免并发/残留冲突。
    /// 读盘失败待确认时（ReadFailurePending）：保存前先弹确认框，用户拒绝则不写盘且保留待确认状态。
    /// </summary>
    public bool Save()
    {
        lock (SaveGate)
        {
            if (ReadFailurePending)
            {
                var answer = MessageBox.Show(
                    "上次配置读取失败，本次以默认配置启动；保存将覆盖原配置文件。是否继续保存？\n若原文件本身无问题，请先选择“否”，检查文件后再保存。",
                    "配置保存确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return false;
                ReadFailurePending = false;
            }
            var plain = XtreamPassword;
            var originalUrls = new List<string>();
            if (Playlists != null)
                foreach (var p in Playlists) originalUrls.Add(p.Url);
            var path = ConfigPath;
            var tmp = Path.Combine(Dir, $"config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(Dir);
                // 保存前加密密码（若已是加密值则不重复加密）；加密失败由 EncryptSecret 抛错（fail-closed）
                if (!string.IsNullOrEmpty(plain) && !plain.StartsWith("DPAPI:", StringComparison.Ordinal))
                    XtreamPassword = EncryptSecret(plain);
                // 纵深防御：序列化前剥离 Playlists[].Url 中的凭据参数，仅影响落盘内容。
                if (Playlists != null)
                {
                    foreach (var p in Playlists)
                    {
                        if (!string.IsNullOrEmpty(p.Url) &&
                            p.Url.Contains("password=", StringComparison.OrdinalIgnoreCase))
                            p.Url = StripCredentialQuery(p.Url);
                    }
                }
                // 原子写：先写同目录（同卷）临时文件，再 File.Replace 原子换名——与 ChannelCache 同一模式，
                // 不存在「旧文件写一半被截断」的窗口；替换失败时旧文件保持原样。
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(path))
                {
                    var bak = path + ".bak";
                    File.Replace(tmp, path, bak);
                    try { File.Delete(bak); } catch { }
                }
                else
                {
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error($"配置保存失败: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
            finally
            {
                XtreamPassword = plain; // 恢复明文，供本次会话继续使用（成功、失败都执行）
                if (Playlists != null)
                {
                    for (var i = 0; i < Playlists.Count && i < originalUrls.Count; i++)
                        Playlists[i].Url = originalUrls[i];
                }
            }
        }
    }
}
