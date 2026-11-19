using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TiviMateWin.Services;

/// <summary>
/// 免费接口翻译（v1.4.33 重构）：谷歌 Cloud Translation 免费层，
/// 每月前 50 万字符免费，超出后每 100 万字符 20 美元（比微软 Azure 贵一倍，
/// Azure 是每月 200 万字符免费、超出后 10 美元/100 万字符）。
/// 听写（Whisper tiny.en）仍在本地，只换翻译这步。任何失败返回 null（不抛异常），
/// 调用方只跳过该行（显示英文原文），不再回落本地。
/// Key 由用户去谷歌免费开、贴进设置（不收费）。
/// 注意：谷歌返回的译文是 HTML 转义过的（如 &#39;），解析后要 HtmlDecode。
/// </summary>
public sealed class GoogleTranslator : IFreeApiTranslator
{
    /// <summary>单行接口超时（秒）：字幕是实时的，等太久不如跳过该行。</summary>
    public const int DefaultTimeoutSeconds = 8;

    private const int MaxCacheEntries = 500;
    private const int EvictBatch = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _cacheGate = new();
    private bool _disposed;

    public string ProviderName => "Google";

    /// <summary>翻译地址（Key 放 query，v2 接口）。</summary>
    public static string BuildUrl(string apiKey) =>
        "https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(apiKey);

    /// <summary>请求体：{ q, target: zh-CN, format: text }。</summary>
    public static string BuildRequestBody(string english) =>
        JsonSerializer.Serialize(new
        {
            q = english,
            target = "zh-CN",
            format = "text",
        });

    /// <summary>
    /// 从响应里取译文（纯逻辑，供测试）：成功时 data.translations[0].translatedText，
    /// 取后 HtmlDecode（谷歌返回 HTML 转义）；缺字段、空译文、非法 JSON 一律返回 null。
    /// </summary>
    public static string? ParseTranslatedText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
                return null;
            if (!translations[0].TryGetProperty("translatedText", out var text)) return null;
            var translated = text.GetString();
            if (string.IsNullOrWhiteSpace(translated)) return null;
            var decoded = WebUtility.HtmlDecode(translated);
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public GoogleTranslator(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// 英翻中：先查缓存；8 秒超时、HTTP 失败、解析失败一律返回 null（调用方跳过该行）。
    /// 空文本返回 ""，含中文返回原文（不调接口）。Key 为空直接返回 null（不发请求）。
    /// </summary>
    public async Task<string?> TranslateAsync(string english, CancellationToken ct)
    {
        english = english.Trim();
        if (english.Length == 0) return "";
        if (ContainsChinese(english)) return english;
        if (string.IsNullOrWhiteSpace(_apiKey)) return null;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(english, out var cached)) return cached;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(_apiKey));
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
            AppLog.Error($"google translate failed: {ex.Message}");
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
