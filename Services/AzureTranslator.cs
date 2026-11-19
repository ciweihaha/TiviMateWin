using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TiviMateWin.Services;

/// <summary>
/// 免费接口翻译（v1.4.33 重构）：微软 Azure Translator 免费层 F0，
/// 每月 200 万字符免费，实时字幕这点量基本够用（每天看 2 小时英文电视约 160 万字符/月）。
/// 超出后每 100 万字符 10 美元（谷歌是 20 美元，Azure 最划算）。
/// 听写（Whisper tiny.en）仍在本地，只换翻译这步。任何失败返回 null（不抛异常），
/// 调用方只跳过该行（显示英文原文），不再回落本地。
/// Key 由用户去微软免费开、贴进设置（不收费）；区域可选，填了才发区域头。
/// </summary>
public sealed class AzureTranslator : IFreeApiTranslator
{
    /// <summary>单行接口超时（秒）：字幕是实时的，等太久不如跳过该行。</summary>
    public const int DefaultTimeoutSeconds = 8;

    private const int MaxCacheEntries = 500;
    private const int EvictBatch = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string? _region;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _cacheGate = new();
    private bool _disposed;

    public string ProviderName => "Azure";

    /// <summary>翻译地址（api-version 3.0，目标简体中文）。</summary>
    public static string BuildUrl() =>
        "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hans";

    /// <summary>请求体：[{ "Text": "Hello world" }]。</summary>
    public static string BuildRequestBody(string english) =>
        JsonSerializer.Serialize(new[] { new { Text = english } });

    /// <summary>
    /// 从响应里取译文（纯逻辑，供测试）：成功时 [0].translations[0].text；
    /// 空数组、缺字段、空译文、非法 JSON 一律返回 null。
    /// </summary>
    public static string? ParseTranslatedText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var first = root[0];
            if (!first.TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
                return null;
            if (!translations[0].TryGetProperty("text", out var text)) return null;
            var translated = text.GetString();
            return string.IsNullOrWhiteSpace(translated) ? null : translated.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public AzureTranslator(string apiKey, string? region = null, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
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
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl());
            request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
            if (_region is not null)
                request.Headers.Add("Ocp-Apim-Subscription-Region", _region);
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
            AppLog.Error($"azure translate failed: {ex.Message}");
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
