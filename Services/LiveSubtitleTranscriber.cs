using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Whisper.net;

namespace TiviMateWin.Services;

/// <summary>
/// 本地语音识别：Whisper.NET（whisper.cpp），英文专用 base.en 模型。
/// 模型首次使用时下载到本机（不进安装包）；工厂常驻内存、每窗一个处理器、串行听写。
/// </summary>
public sealed class LiveSubtitleTranscriber : IDisposable
{
    /// <summary>默认听写模型文件名（英文专用 base.en）；可配 tiny.en（更快、识别稍差）。</summary>
    public const string ModelFileName = "ggml-base.en.bin";

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
    private readonly HttpClient _http;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _setupLogged;
    private bool _disposed;

    /// <summary>听写线程数：与翻译一样，流水线串行时各用全部核（留 1 核给播放与界面）。</summary>
    public static int TranscribeThreads => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    /// <summary>本实例听写用的线程数（两级流水线分核时由服务指定；不指定则用默认值）。</summary>
    public int Threads { get; }

    public bool ModelReady => File.Exists(_modelPath);

    /// <summary>最近一次听写的耗时（毫秒），供延迟实测。</summary>
    public long LastTranscribeMs { get; private set; }

    /// <summary>模型下载进度（已下载字节、总字节；总字节未知时为 -1）。</summary>
    public event Action<long, long>? ModelDownloadProgress;

    public LiveSubtitleTranscriber(string? modelPath = null, HttpClient? http = null, int? threads = null,
        string? modelFileName = null)
    {
        // 模型文件：文件名决定下载地址（同一仓库）；不传用默认 base.en。
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
            _factory ??= WhisperFactory.FromPath(_modelPath);
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

    /// <summary>听写一窗 16kHz 单声道音频，返回英文文本；无语音/失败返回 null（不抛异常）。</summary>
    public async Task<string?> TranscribeAsync(short[] mono16k, CancellationToken ct)
    {
        if (!ModelReady) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 首调用建工厂+处理器（读模型、建原生上下文）：单独计时，区分“加载慢”还是“推理慢”。
            var setupSw = Stopwatch.StartNew();
            _factory ??= WhisperFactory.FromPath(_modelPath);
            using var processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Threads)
                .Build();
            setupSw.Stop();
            if (!_setupLogged)
            {
                _setupLogged = true;
                AppLog.Info($"whisper setup {setupSw.ElapsedMilliseconds}ms (factory+processor, first call)");
            }
            using var wav = new MemoryStream(LiveSubtitleChunker.BuildWav(mono16k));
            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
            {
                if (!LiveSubtitleChunker.IsNonSpeechMarker(segment.Text))
                    sb.Append(segment.Text).Append(' ');
            }
            sw.Stop();
            LastTranscribeMs = sw.ElapsedMilliseconds;
            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
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
        _factory?.Dispose();
        _gate.Dispose();
    }
}
