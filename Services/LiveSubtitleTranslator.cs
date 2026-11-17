using System.IO;
using System.Net.Http;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace TiviMateWin.Services;

/// <summary>
/// 实时字幕翻译器：进程内推理（LLamaSharp + llama.cpp CPU 后端），加载
/// Qwen2.5-1.5B-Instruct GGUF（Q4_K_M）把英文字幕翻成中文。
/// 模型首次使用时下载到本机（不进安装包）；思路复用 UnBox 的 translate 包：
/// 只翻短句、只输出译文、失败回退原文、FIFO 缓存。
/// 推理失败永不抛异常；连续失败触发熔断（IsBroken），调用方停掉字幕、播放继续。
/// 注意：llama.cpp 是原生代码，进程内段错误无法用 try/catch 兜住——那是改成
/// 后台子进程方案的信号，不是靠这里加 try 能解决的。
/// </summary>
public sealed class LiveSubtitleTranslator : IDisposable
{
    public const string DefaultModelFileName = "qwen2.5-1.5b-instruct-q4_k_m.gguf";

    // 模型下载地址：Hugging Face 主站 + 国内镜像兜底（Qwen 官方仓库）。
    // 文件 1,117,320,736 字节（约 1.04 GiB，HF 实测 Content-Length）。
    private const string ModelPrimaryUrl =
        "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf";
    private const string ModelMirrorUrl =
        "https://hf-mirror.com/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf";

    // 系统提示词：短句翻译不需要解释，解释性文字会直接混进字幕显示出来。
    private const string SystemPrompt =
        "You are a subtitle translator. Translate the following English subtitle line into natural, concise Chinese. Output ONLY the Chinese translation, no explanation, no quotes, no preamble.";

    private const int MaxCacheEntries = 500;
    private const int EvictBatch = 100;
    private const int MaxConsecutiveFailures = 3;   // 熔断阈值：连续失败这么多次即停字幕
    private const int InferenceTimeoutSeconds = 8;  // 单行推理超时（毫秒级行一般 1–2 秒）
    private const int MaxTokens = 64;               // 字幕行短，64 token 够译两三句话，同时截住模型跑题续写
    private const float Temperature = 0.2f;
    private const int ContextSize = 1024;           // 提示词约 100 token，1024 绰绰有余

    /// <summary>模型存放目录（与 Whisper 模型同一目录）。</summary>
    public static string DefaultModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TiviMateWin", "models");

    public static string DefaultModelPath => Path.Combine(DefaultModelDir, DefaultModelFileName);

    /// <summary>
    /// 推理线程数：流水线串行（听写与翻译不同时跑），各用全部核才最快；
    /// 留 1 核给播放与界面。8 核云电脑上是 7 线程，4 核机器上是 3 线程。
    /// </summary>
    public static int InferenceThreads => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    private readonly string _modelPath;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string, CancellationToken, Task<string?>>? _generateOverride;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private int _consecutiveFailures;
    private bool _loadFailed;
    private bool _disposed;

    /// <summary>模型文件是否已下载到本机。</summary>
    public bool ModelReady => File.Exists(_modelPath);

    /// <summary>熔断：连续失败达到阈值，翻译已不可用（调用方应停掉字幕）。</summary>
    public bool IsBroken => _consecutiveFailures >= MaxConsecutiveFailures;

    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>模型下载进度（已下载字节、总字节；总字节未知时为 -1）。</summary>
    public event Action<long, long>? ModelDownloadProgress;

    public LiveSubtitleTranslator(string? modelPath = null, HttpClient? http = null,
        Func<string, CancellationToken, Task<string?>>? generateOverride = null)
    {
        // 配置里存的是文件名（如 qwen2.5-1.5b-instruct-q4_k_m.gguf），拼到模型目录下；
        // 传完整路径时直接用（测试用临时路径）。
        _modelPath = string.IsNullOrWhiteSpace(modelPath)
            ? DefaultModelPath
            : Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(DefaultModelDir, modelPath);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _generateOverride = generateOverride;
    }

    /// <summary>模型已在返回 true；否则下载（主站失败走镜像），成功返回 true，失败返回 false。</summary>
    public async Task<bool> EnsureModelAsync(CancellationToken ct)
    {
        if (ModelReady) return true;
        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        foreach (var url in new[] { ModelPrimaryUrl, ModelMirrorUrl })
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                var tmp = _modelPath + ".tmp";
                await using (var file = File.Create(tmp))
                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        downloaded += read;
                        ModelDownloadProgress?.Invoke(downloaded, total);
                    }
                }
                File.Move(tmp, _modelPath, overwrite: true);
                AppLog.Info($"translate model downloaded ({new FileInfo(_modelPath).Length} bytes)");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"translate model download failed [{url}]: {ex.Message}");
                // 换下一个镜像重试
            }
        }
        return false;
    }

    /// <summary>
    /// 下载（如需）并加载模型到内存。失败返回 false，不抛异常；加载失败会被记住，
    /// 同一服务实例内不再重试（重开开关重建服务才会再试）。
    /// </summary>
    public async Task<bool> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_executor is not null) return true;
        if (_loadFailed) return false;
        if (_generateOverride != null) return true; // 测试注入：不需要真模型
        if (!await EnsureModelAsync(ct).ConfigureAwait(false)) return false;
        try
        {
            await _engineGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_executor is not null) return true;
                var parameters = new ModelParams(_modelPath)
                {
                    ContextSize = ContextSize,
                    Threads = InferenceThreads,
                };
                _weights = await LLamaWeights.LoadFromFileAsync(parameters, ct, new Progress<float>())
                    .ConfigureAwait(false);
                _executor = new StatelessExecutor(_weights, parameters);
                AppLog.Info($"translate model loaded ({DefaultModelFileName}, threads {InferenceThreads})");
                return true;
            }
            finally
            {
                _engineGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loadFailed = true;
            AppLog.Error($"translate model load failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把一条英文字幕翻成中文。空文本返回 ""（不推理）；含中文返回原文（不推理）；
    /// 翻译失败返回 null（调用方回退显示英文原文），失败永远不抛异常。
    /// 模型文件缺失时直接返回 null（Prepare 阶段已试过下载，不再每窗重试）。
    /// </summary>
    public async Task<string?> TranslateAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        if (IsChineseText(text)) return text;
        if (IsBroken) return null;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(text, out var cached)) return cached;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(InferenceTimeoutSeconds));
            string? raw;
            if (_generateOverride != null)
            {
                raw = await _generateOverride(text, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                if (!ModelReady || _loadFailed) return null;
                if (!await EnsureEngineAsync(timeout.Token).ConfigureAwait(false)) return null;
                raw = await GenerateAsync(text, timeout.Token).ConfigureAwait(false);
            }
            var cleaned = CleanTranslation(raw ?? "");
            if (cleaned.Length == 0)
            {
                NoteFailure();
                return null;
            }
            NoteSuccess();
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
        catch (OperationCanceledException)
        {
            // 超时（推理 8 秒未出结果）记一次失败；服务停止时的取消也走这里，
            // 此时服务正在关，计数无所谓。
            NoteFailure();
            return null;
        }
        catch (Exception ex)
        {
            // 推理异常只记日志、当行跳过，永不挡播放。
            AppLog.Error($"subtitle translate failed: {ex.Message}");
            NoteFailure();
            return null;
        }
    }

    /// <summary>确保推理引擎就绪（已加载返回 true）。加载失败返回 false，不抛异常。</summary>
    private async Task<bool> EnsureEngineAsync(CancellationToken ct)
    {
        if (_executor is not null) return true;
        return await EnsureLoadedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>调模型生成译文（调用方保证引擎已就绪；推理串行，一次只跑一行）。</summary>
    private async Task<string?> GenerateAsync(string text, CancellationToken ct)
    {
        await _engineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_executor is null) return null;
            var inferenceParams = new InferenceParams
            {
                MaxTokens = MaxTokens,
                AntiPrompts = new List<string> { "<|im_end|>" },
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = Temperature },
            };
            var sb = new StringBuilder();
            await foreach (var token in _executor
                .InferAsync(BuildPrompt(text), inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(token);
            }
            return sb.ToString();
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>
    /// 手动拼 ChatML 提示词（不依赖模型的 chat template 解析，行为确定）：
    /// system 写翻译要求，user 放英文行，assistant 处开始生成。
    /// </summary>
    public static string BuildPrompt(string englishLine)
    {
        return "<|im_start|>system\n" + SystemPrompt + "<|im_end|>\n" +
               "<|im_start|>user\n" + englishLine + "<|im_end|>\n" +
               "<|im_start|>assistant\n";
    }

    /// <summary>去掉模型常带的多余包装：首尾空白、整句引号（英文 " 与中文 “”）、「翻译：」前缀、残留的特殊 token。</summary>
    public static string CleanTranslation(string s)
    {
        s = s.Trim();
        foreach (var token in new[] { "<|im_end|>", "<|endoftext|>" })
        {
            var idx = s.IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0) s = s[..idx].Trim();
        }
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

    private void NoteFailure()
    {
        var n = Interlocked.Increment(ref _consecutiveFailures);
        if (n == MaxConsecutiveFailures)
            AppLog.Error("subtitle translator broken after 3 consecutive failures; subtitles will stop");
    }

    private void NoteSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // StatelessExecutor 没有 Dispose（上下文由它内部持有，随权重释放由终结器回收）；
        // 只释放权重与同步对象。
        _weights?.Dispose();
        _engineGate.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
