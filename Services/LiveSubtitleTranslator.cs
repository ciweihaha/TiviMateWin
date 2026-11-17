using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiviMateWin.Services;

/// <summary>
/// 实时字幕翻译器：调本机 Ollama（/api/chat）把英文字幕翻成中文。
/// 思路复用 UnBox 的 translate 包：只翻短句、只输出译文、失败回退原文、FIFO 缓存。
/// 不依赖 WPF，可进单元测试工程（HttpClient 可注入假 handler）。
/// </summary>
public sealed class LiveSubtitleTranslator
{
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    public const string DefaultModel = "qwen2.5:1.5b";

    // 系统提示词：短句翻译不需要解释，解释性文字会直接混进字幕显示出来。
    private const string SystemPrompt =
        "You are a subtitle translator. Translate the following English subtitle line into natural, concise Chinese. Output ONLY the Chinese translation, no explanation, no quotes, no preamble.";

    private const int MaxCacheEntries = 500;
    private const int EvictBatch = 100;

    private readonly string _endpoint;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _cacheGate = new();

    public LiveSubtitleTranslator(string endpoint, string model, HttpClient? http = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>
    /// 把一条英文字幕翻成中文。空文本返回 ""（不发请求）；含中文返回原文（不发请求）；
    /// 翻译失败返回 null（调用方回退显示英文原文），失败永远不抛异常。
    /// </summary>
    public async Task<string?> TranslateAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        if (IsChineseText(text)) return text;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(text, out var cached)) return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/chat");
            request.Content = JsonContent.Create(new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = text },
                },
                stream = false,
                options = new { temperature = 0.2 },
            });
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            var cleaned = CleanTranslation(body?.Message?.Content ?? "");
            if (cleaned.Length == 0) return null;

            lock (_cacheGate)
            {
                if (_cache.Count >= MaxCacheEntries)
                {
                    // FIFO 淘汰最旧一批，而不是整库清空——长片看到一半时整库清空，
                    // 接下来几百条字幕要全部重新调模型。
                    for (var i = 0; i < EvictBatch && _order.Count > 0; i++)
                        _cache.Remove(_order.Dequeue());
                }
                if (!_cache.ContainsKey(text))
                {
                    _order.Enqueue(text);
                    _cache[text] = cleaned;
                }
            }
            return cleaned;
        }
        catch (Exception)
        {
            // 服务没起、模型没拉、超时：都返回 null，调用方回退英文原文。
            return null;
        }
    }

    /// <summary>检查本地 Ollama 是否已拉取当前模型：服务没起、模型没拉取都返回 false。</summary>
    public async Task<bool> CheckModelAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(_endpoint + "/api/tags", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadFromJsonAsync<TagsResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (body?.Models is null) return false;
            foreach (var m in body.Models)
            {
                if (m.Name == _model || m.Name.StartsWith(_model + ":", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>去掉模型常带的多余包装：首尾空白、整句引号（英文 " 与中文 “”）、「翻译：」前缀。</summary>
    public static string CleanTranslation(string s)
    {
        s = s.Trim();
        foreach (var prefix in new[] { "翻译：", "翻译:", "Translation:", "translation:" })
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal))
            {
                s = s[prefix.Length..].Trim();
                break;
            }
        }
        if (s.Length >= 2)
        {
            var first = s[0];
            var last = s[^1];
            if ((first == '"' && last == '"') || (first == '“' && last == '”'))
                s = s[1..^1].Trim();
        }
        return s;
    }

    /// <summary>判一段文本是否含中文：含中文就不翻译（已是中文或双语字幕直接显示原文）。</summary>
    public static bool IsChineseText(string s)
    {
        foreach (var ch in s)
        {
            // CJK 统一汉字区（含扩展 A 区首段），覆盖日常用字即可。
            if ((ch >= '\u4E00' && ch <= '\u9FFF') || (ch >= '\u3400' && ch <= '\u4DBF'))
                return true;
        }
        return false;
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagModel>? Models { get; set; }
    }

    private sealed class TagModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }
}
