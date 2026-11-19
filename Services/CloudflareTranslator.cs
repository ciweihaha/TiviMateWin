using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TiviMateWin.Services;

/// <summary>
/// 免费接口翻译（v1.4.33）：Cloudflare Workers AI 免费层，翻译模型 @cf/meta/m2m100-1.2b。
/// 选它而不是 Groq/OpenRouter：用户已有 CF 账号，不用另注册、不花钱——免费层每天
/// 10,000 neurons，一条字幕约 10–30 token，一天几千条都在免费额度内；翻译类限速
/// 720 请求/分钟，字幕 1.5 秒一窗远远够用。听写（Whisper tiny.en）仍在本地，只换翻译这步。
/// 翻译还是只翻英语（EnglishOnly 逻辑在 MainWindow 的 TranslationActive 里，见
/// ChannelLanguage；本类只负责把英文翻成中文）。任何失败返回 null（不抛异常），
/// 调用方只跳过该行（显示英文原文）。本地大模型已删掉（用户 10:36：效果不好）。
/// 注意：m2m100 对多句输入只翻其中一句——字幕行本来就是单句短句，不受影响。
/// </summary>
public sealed class CloudflareTranslator : IFreeApiTranslator
{
    /// <summary>翻译模型（Workers AI 上的 M2M-100，100+ 语言互译）。</summary>
    public const string ModelId = "@cf/meta/m2m100-1.2b";

    /// <summary>单行接口超时（秒）：字幕是实时的，等太久不如跳过该行。</summary>
    public const int DefaultTimeoutSeconds = 8;

    private const int MaxCacheEntries = 500;
    private const int EvictBatch = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _accountId;
    private readonly string _apiToken;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _cacheGate = new();
    private bool _disposed;

    public string ProviderName => "Cloudflare";

    /// <summary>Workers AI REST 地址（REST 调用无需建 Worker，直调即可）。</summary>
    public static string BuildUrl(string accountId) =>
        $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/run/{ModelId}";

    /// <summary>请求体：英翻中（source_lang/target_lang 用全名，与官方示例一致）。</summary>
    public static string BuildRequestBody(string english) =>
        JsonSerializer.Serialize(new
        {
            text = english,
            source_lang = "english",
            target_lang = "chinese",
        });

    /// <summary>
    /// 从 Workers AI 响应里取译文（纯逻辑，供测试）：成功时 result.translated_text；
    /// success=false、缺字段、空译文、非法 JSON 一律返回 null。
    /// </summary>
    public static string? ParseTranslatedText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
                return null;
            if (!root.TryGetProperty("result", out var result)) return null;
            if (!result.TryGetProperty("translated_text", out var text)) return null;
            var translated = text.GetString();
            return string.IsNullOrWhiteSpace(translated) ? null : translated.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public CloudflareTranslator(string accountId, string apiToken, HttpClient? http = null)
    {
        _accountId = accountId;
        _apiToken = apiToken;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// 英翻中：先查缓存（重复台词不重复调接口，省额度也省延迟）；8 秒超时、
    /// HTTP 失败、success=false 一律返回 null（调用方跳过该行，显示英文原文）。
    /// 空文本返回 ""，含中文返回原文（不调接口）。
    /// </summary>
    public async Task<string?> TranslateAsync(string english, CancellationToken ct)
    {
        english = english.Trim();
        if (english.Length == 0) return "";
        if (ContainsChinese(english)) return english;
        if (string.IsNullOrWhiteSpace(_accountId) || string.IsNullOrWhiteSpace(_apiToken))
            return null;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(english, out var cached)) return cached;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(_accountId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
            request.Content = new StringContent(BuildRequestBody(english), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var translated = ParseTranslatedText(json);
            if (translated is null) return null;

            lock (_cacheGate)
            {
                if (_cache.Count >= MaxCacheEntries)
                {
                    for (var i = 0; i < EvictBatch && _order.Count > 0; i++)
                        _cache.Remove(_order.Dequeue());
                }
                if (!_cache.ContainsKey(english))
                {
                    _order.Enqueue(english);
                    _cache[english] = translated;
                }
            }
            return translated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"cloudflare translate failed: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException)
        {
            // 超时/取消：当作本行失败，调用方跳过该行。
            return null;
        }
    }

    private static bool ContainsChinese(string s)
    {
        foreach (var c in s)
            if (c is >= '\u4E00' and <= '\u9FFF') return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }
}
