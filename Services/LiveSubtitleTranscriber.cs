using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Whisper.net;

namespace TiviMateWin.Services;

/// <summary>
/// 本地语音识别：Whisper.NET（whisper.cpp），英文专用 tiny.en 模型。
/// 模型首次使用时下载到本机（不进安装包）；工厂常驻内存、每窗一个处理器、串行听写。
/// tiny.en 是默认：3 秒窗在云电脑上 base.en 要 3 秒、台式机 5–6 秒，队列一满
/// 端到端就到 10 秒；tiny.en 快 3–4 倍、广播新闻口音识别够用，base.en 仍可配回。
/// 模型路径含中文时同样先解析成纯英文路径再传给原生加载器（见 LiveSubtitleModelPath）。
/// 解码只跑一遍：温度回退关掉（WithTemperatureInc(0)）——噪声/音乐窗在默认回退下
/// 要按 0、0.2、0.4… 最多重解 6 遍，是个别窗卡 5–12 秒的主因；单段输出 +
/// 每段 64 token 上限截住噪声窗的幻觉长生成（默认上限 448 token）；再加单窗 6 秒
/// 超时兜底，超时放弃当窗、不卡喂音频循环。</summary>
public sealed class LiveSubtitleTranscriber : IDisposable
{
    /// <summary>默认听写模型文件名（英文专用 tiny.en）；可配 base.en（更准、更慢）。</summary>
    public const string ModelFileName = "ggml-tiny.en.bin";

    /// <summary>单窗听写超时（秒）：正常窗约 1–2 秒，6 秒留足余量；超时放弃当窗。</summary>
    public const int TranscribeTimeoutSeconds = 6;

    /// <summary>每段最多 token：1.5 秒窗正常约 6–15 个，64 留足余量、截住幻觉长生成。</summary>
    public const int MaxTokensPerSegment = 64;

    /// <summary>处理器创建耗时超过该值（毫秒）时记日志：区分“加载慢”还是“推理慢”。</summary>
    public const int SlowSetupThresholdMs = 500;

    // 模型下载地址：Hugging Face 主站 + 国内镜像兜底（ggerganov/whisper.cpp 仓库，
    // ggml-base.en.bin / ggml-tiny.en.bin 等都在同一仓库同一路径下）。
    private static string ModelPrimaryUrl(string fileName) =>
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + fileName;
    private static string ModelMirrorUrl(string fileName) =>
        "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/" + fileName;

    public static string DefaultModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TiviMateWin", "models", ModelFileName);

    /// <summary>指定模型文件的本机路径（与默认文件名同一目录）。</summary>
    public static string ModelPathFor(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TiviMateWin", "models", fileName);

    private readonly string _modelPath;
    private readonly string _modelFileName;
    private string? _loadPath; // 传给原生加载器的路径（中文路径解析成纯英文后缓存，避免每窗重算）
    private readonly HttpClient _http;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _setupLogged;
    private long _transcribeTimeouts;
    private bool _disposed;

    /// <summary>听写线程数：与翻译一样，流水线串行时各用全部核（留 1 核给播放与界面）。</summary>
    public static int TranscribeThreads => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    /// <summary>本实例听写用的线程数（两级流水线分核时由服务指定；不指定则用默认值）。</summary>
    public int Threads { get; }

    public bool ModelReady => File.Exists(_modelPath);

    /// <summary>最近一次听写的耗时（毫秒），供延迟实测。</summary>
    public long LastTranscribeMs { get; private set; }

    /// <summary>最近一次处理器创建的耗时（毫秒，建工厂+建处理器）：超过阈值时记日志。</summary>
    public long LastSetupMs { get; private set; }

    /// <summary>累计因单窗超时被放弃的窗口数。</summary>
    public long TranscribeTimeouts => Interlocked.Read(ref _transcribeTimeouts);

    /// <summary>模型下载进度（已下载字节、总字节；总字节未知时为 -1）。</summary>
    public event Action<long, long>? ModelDownloadProgress;

    public LiveSubtitleTranscriber(string? modelPath = null, HttpClient? http = null, int? threads = null,
        string? modelFileName = null)
    {
        // 模型文件：文件名决定下载地址（同一仓库）；不传用默认 tiny.en。
        // 传完整路径时直接用（测试用临时路径），文件名取路径里的名字。
        var fileName = string.IsNullOrWhiteSpace(modelFileName)
            ? (string.IsNullOrWhiteSpace(modelPath) ? ModelFileName : Path.GetFileName(modelPath))
            : modelFileName.Trim();
        _modelFileName = string.IsNullOrWhiteSpace(fileName) ? ModelFileName : fileName;
        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? ModelPathFor(_modelFileName) : modelPath;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Threads = threads.HasValue ? Math.Clamp(threads.Value, 1, 16) : TranscribeThreads;
    }

    /// <summary>模型已在返回 true；否则下载（主站失败走镜像），成功返回 true，失败返回 false。
    /// 模型在本地后立即建工厂（读模型 + 建原生上下文，首调用约 30 秒）：不预热的话
    /// 这 30 秒会卡在第一窗听写里，喂音频循环被堵住、通道积压溢出丢 PCM，串行模式永久掉队。</summary>
    public async Task<bool> EnsureModelAsync(CancellationToken ct)
    {
        if (!ModelReady)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            var downloaded = false;
            foreach (var url in new[] { ModelPrimaryUrl(_modelFileName), ModelMirrorUrl(_modelFileName) })
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
                        long downloadedBytes = 0;
                        int read;
                        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            downloadedBytes += read;
                            ModelDownloadProgress?.Invoke(downloadedBytes, total);
                        }
                    }
                    File.Move(tmp, _modelPath, overwrite: true);
                    AppLog.Info($"whisper model downloaded ({new FileInfo(_modelPath).Length} bytes)");
                    downloaded = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Error($"whisper model download failed [{url}]: {ex.Message}");
                    // 换下一个镜像重试
                }
            }
            if (!downloaded) return false;
        }
        return EnsureFactory();
    }

    /// <summary>建 Whisper 工厂（常驻，供每窗建处理器复用）；失败返回 false。</summary>
    private bool EnsureFactory()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            _loadPath ??= LiveSubtitleModelPath.ResolveForNativeLoad(_modelPath);
            _factory ??= WhisperFactory.FromPath(_loadPath);
            sw.Stop();
            AppLog.Info($"whisper factory ready {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"whisper factory init failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>听写一窗 16kHz 单声道音频，返回英文文本；无语音/失败/超时返回 null（不抛异常）。
    /// 单窗超过 <see cref="TranscribeTimeoutSeconds"/> 秒即放弃（噪声窗重解码/幻觉长生成
    /// 曾让个别窗卡 5–12 秒，超时后当窗跳过、喂音频循环不被堵住）。</summary>
    public async Task<string?> TranscribeAsync(short[] mono16k, CancellationToken ct)
    {
        if (_disposed) return null;
        if (!ModelReady) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TranscribeTimeoutSeconds));
        try
        {
            // 首调用建工厂+处理器（读模型、建原生上下文）：单独计时，区分“加载慢”还是“推理慢”。
            var setupSw = Stopwatch.StartNew();
            _loadPath ??= LiveSubtitleModelPath.ResolveForNativeLoad(_modelPath);
            _factory ??= WhisperFactory.FromPath(_loadPath);
            // v1.4.31：处理器必须异步释放——取消时同步 Dispose 会抛
            // "Cannot dispose while processing, please use DisposeAsync instead"；
            // await using 等在途处理收尾后再释原生状态。
            await using var processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Threads)
                // 1.5 秒窗本就是一段话：单段输出，少一次分段解码的开销。
                .WithSingleSegment()
                // 截住噪声窗的幻觉长生成（1.5 秒窗正常约 6–15 个 token）。
                .WithMaxTokensPerSegment(MaxTokensPerSegment)
                // 只跑一遍贪心解码：默认温度回退会在低置信度时按 0、0.2、0.4…
                // 最多重解 6 遍，是噪声窗卡 5–12 秒的主因；直播字幕要速度不要这点回退收益。
                .WithTemperature(0f)
                .WithTemperatureInc(0f)
                .Build();
            setupSw.Stop();
            LastSetupMs = setupSw.ElapsedMilliseconds;
            if (!_setupLogged)
            {
                _setupLogged = true;
                AppLog.Info($"whisper setup {setupSw.ElapsedMilliseconds}ms (factory+processor, first call)");
            }
            else if (setupSw.ElapsedMilliseconds > SlowSetupThresholdMs)
            {
                AppLog.Info($"whisper setup slow {setupSw.ElapsedMilliseconds}ms");
            }
            using var wav = new MemoryStream(LiveSubtitleChunker.BuildWav(mono16k));
            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wav, timeoutCts.Token).ConfigureAwait(false))
            {
                if (!LiveSubtitleChunker.IsNonSpeechMarker(segment.Text))
                    sb.Append(segment.Text).Append(' ');
            }
            sw.Stop();
            LastTranscribeMs = sw.ElapsedMilliseconds;
            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务正在停：正常退出，不记失败、不记超时。
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 服务没停，就是本窗超时：放弃当窗、记数，喂音频循环继续。
            Interlocked.Increment(ref _transcribeTimeouts);
            AppLog.Info($"whisper transcribe timed out after {TranscribeTimeoutSeconds}s, window skipped (timeouts={TranscribeTimeouts})");
            return null;
        }
        catch (Exception ex)
        {
            // 听写失败只记日志、当窗跳过，永不挡播放。
            AppLog.Error($"whisper transcribe failed: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // v1.4.31：关字幕崩进程修复——工厂释放前必须等在途听写收尾。
        // 旧逻辑在 Stop（取消）后立刻释工厂，而串行喂音频循环可能正卡在
        // ProcessAsync 里（处理器由该工厂创建）；原生上下文被提前释放，
        // 处理器随后访问已释放的 ggml 上下文 → 0xc0000005 访问违规，进程直接死。
        // _gate 在整次 TranscribeAsync 期间都被持有，拿到它即证明无在途听写；
        // 取消后听写会在 6 秒超时内收尾，+5 秒余量；超时仍释（尽力而为，不无限等）。
        try { _gate.Wait(TimeSpan.FromSeconds(TranscribeTimeoutSeconds + 5)); } catch { }
        _factory?.Dispose();
        _gate.Dispose();
    }
}
