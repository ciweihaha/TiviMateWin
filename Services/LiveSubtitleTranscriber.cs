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
    public const string ModelFileName = "ggml-base.en.bin";

    // 模型下载地址：Hugging Face 主站 + 国内镜像兜底。
    private const string ModelPrimaryUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
    private const string ModelMirrorUrl =
        "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    public static string DefaultModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TiviMateWin", "models", ModelFileName);

    private readonly string _modelPath;
    private readonly HttpClient _http;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public bool ModelReady => File.Exists(_modelPath);

    /// <summary>最近一次听写的耗时（毫秒），供延迟实测。</summary>
    public long LastTranscribeMs { get; private set; }

    /// <summary>模型下载进度（已下载字节、总字节；总字节未知时为 -1）。</summary>
    public event Action<long, long>? ModelDownloadProgress;

    public LiveSubtitleTranscriber(string? modelPath = null, HttpClient? http = null)
    {
        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? DefaultModelPath : modelPath;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
                AppLog.Info($"whisper model downloaded ({new FileInfo(_modelPath).Length} bytes)");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"whisper model download failed [{url}]: {ex.Message}");
                // 换下一个镜像重试
            }
        }
        return false;
    }

    /// <summary>听写一窗 16kHz 单声道音频，返回英文文本；无语音/失败返回 null（不抛异常）。</summary>
    public async Task<string?> TranscribeAsync(short[] mono16k, CancellationToken ct)
    {
        if (!ModelReady) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _factory ??= WhisperFactory.FromPath(_modelPath);
            using var processor = _factory.CreateBuilder().WithLanguage("en").Build();
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
