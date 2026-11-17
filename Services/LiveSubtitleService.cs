using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TiviMateWin.Services;

/// <summary>
/// 直播实时字幕编排：音频 → 切块 → 两级流水线（听写与翻译并行）→ 字幕事件。
/// 听写下一窗的同时翻译上一窗，吞吐由最慢的一级决定，不再像串行那样
/// 每窗耗时 = 听写 + 翻译（3 秒窗约 3.9 秒 &gt; 窗长，越跑越掉队）。
/// 任何一步失败只记日志、当窗跳过，永不挡播放。延迟实测值暴露给调用方。
/// </summary>
public sealed class LiveSubtitleService : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly AppConfig _config;
    private readonly LiveSubtitleAudioCapture _capture;
    private readonly LiveSubtitleChunker _chunker;
    private readonly LiveSubtitleTranscriber _transcriber;
    private readonly LiveSubtitleTranslator _translator;
    private LiveSubtitlePipeline? _pipeline;
    private CancellationTokenSource? _cts;
    private Task? _feederTask;
    private bool _audioAttached;
    private bool _modelReady;
    private bool _translatorReady;
    private bool _disposed;

    /// <summary>新字幕就绪（UI 线程外触发，调用方自行切回 UI 线程）。</summary>
    public event Action<LiveSubtitleLine>? SubtitleReady;

    /// <summary>状态提示（空字符串 = 清除提示）。</summary>
    public event Action<string>? StatusChanged;

    public bool IsRunning { get; private set; }

    /// <summary>当前用的翻译模型文件名（设置改了需重建服务才生效）。</summary>
    public string Model => _config.LiveSubtitleModel;

    /// <summary>当前用的听写模型文件名（设置改了需重建服务才生效）。</summary>
    public string WhisperModel => _config.LiveSubtitleWhisperModel;

    /// <summary>当前切块窗口（秒，设置改了需重建服务才生效）。</summary>
    public int WindowSeconds => _config.LiveSubtitleWindowSeconds;

    /// <summary>听写实际用的线程数（设置改了需重建服务才生效）。</summary>
    public int TranscribeThreads { get; }

    /// <summary>翻译实际用的线程数（设置改了需重建服务才生效）。</summary>
    public int TranslateThreads { get; }

    /// <summary>最近一窗听写耗时（毫秒）。</summary>
    public long LastTranscribeMs => _pipeline?.LastTranscribeMs ?? 0;

    /// <summary>最近一窗翻译耗时（毫秒）。</summary>
    public long LastTranslateMs => _pipeline?.LastTranslateMs ?? 0;

    /// <summary>最近一窗听写+翻译合计耗时（毫秒，不含切块等待与排队）。</summary>
    public long LastPipelineMs => LastTranscribeMs + LastTranslateMs;

    /// <summary>最近一窗端到端延迟（毫秒：窗口入队 → 字幕发出）。</summary>
    public long LastEndToEndMs => _pipeline?.LastEndToEndMs ?? 0;

    /// <summary>累计收到的窗口数。</summary>
    public long WindowsReceived => _pipeline?.WindowsReceived ?? 0;

    /// <summary>累计丢掉的窗口数（队列满时丢最旧）。</summary>
    public long WindowsDropped => _pipeline?.WindowsDropped ?? 0;

    /// <summary>
    /// 两级流水线分核：留 1 核给播放与界面，其余按 2:1 分给听写与翻译
    /// （听写是更慢的一级）；配置里 &gt;0 的值手动覆盖（0=自动）。
    /// </summary>
    public static (int Transcribe, int Translate) ResolveThreads(AppConfig config)
    {
        var total = Math.Max(2, Environment.ProcessorCount - 1);
        var transcribe = config.LiveSubtitleTranscribeThreads > 0
            ? Math.Clamp(config.LiveSubtitleTranscribeThreads, 1, 16)
            : Math.Max(2, total * 2 / 3);
        var translate = config.LiveSubtitleTranslateThreads > 0
            ? Math.Clamp(config.LiveSubtitleTranslateThreads, 1, 16)
            : Math.Max(2, total - transcribe);
        return (transcribe, translate);
    }

    public LiveSubtitleService(MediaPlayer player, AppConfig config)
    {
        _player = player;
        _config = config;
        _capture = new LiveSubtitleAudioCapture(player);
        _chunker = new LiveSubtitleChunker(Math.Clamp(config.LiveSubtitleWindowSeconds, 1, 10));
        var (transcribeThreads, translateThreads) = ResolveThreads(config);
        TranscribeThreads = transcribeThreads;
        TranslateThreads = translateThreads;
        _transcriber = new LiveSubtitleTranscriber(threads: transcribeThreads,
            modelFileName: config.LiveSubtitleWhisperModel);
        _translator = new LiveSubtitleTranslator(config.LiveSubtitleModel, threads: translateThreads);
        _transcriber.ModelDownloadProgress += (downloaded, total) =>
        {
            if (total > 0)
                StatusChanged?.Invoke($"正在下载语音模型 {downloaded * 100 / total}%");
        };
        _translator.ModelDownloadProgress += (downloaded, total) =>
        {
            if (total > 0)
                StatusChanged?.Invoke($"正在下载翻译模型 {downloaded * 100 / total}%");
        };
    }

    /// <summary>
    /// 挂音频回调（幂等）。须在 MediaPlayer.Play 之前调用，由 MainWindow.PlayAsync
    /// 在拿锁后执行；播起来之后再挂要等下次 Play 才生效。
    /// </summary>
    public void EnsureAudioAttached()
    {
        if (_audioAttached || _disposed) return;
        _capture.Attach();
        _audioAttached = true;
    }

    /// <summary>启动流水线；两个模型下载/加载在后台进行，未就绪前收到的窗直接丢弃。</summary>
    public void Start()
    {
        if (IsRunning || _disposed) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _pipeline = new LiveSubtitlePipeline(
            transcribe: (window, ct) => _transcriber.TranscribeAsync(window, ct),
            translate: (text, ct) => _translator.TranslateAsync(text, ct),
            isTranslatorBroken: () => _translator.IsBroken);
        _pipeline.SubtitleReady += line => SubtitleReady?.Invoke(line);
        _pipeline.TranslatorBroken += OnTranslatorBroken;
        _pipeline.Start();
        _feederTask = Task.Run(() => PipelineLoopAsync(_cts.Token));
        _ = PrepareAsync(_cts.Token);
    }

    private void OnTranslatorBroken()
    {
        // 熔断：推理连续失败，字幕停掉、播放继续（重开开关重建服务才会再试）。
        StatusChanged?.Invoke("翻译连续失败，实时字幕已停（播放不受影响）");
        AppLog.Error("live subtitle stopped: translator broken");
        Stop();
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("正在准备语音模型…");
        _modelReady = await _transcriber.EnsureModelAsync(ct).ConfigureAwait(false);
        if (!_modelReady)
        {
            StatusChanged?.Invoke("语音模型下载失败，实时字幕暂不可用");
            return;
        }
        StatusChanged?.Invoke("正在准备翻译模型…");
        _translatorReady = await _translator.EnsureLoadedAsync(ct).ConfigureAwait(false);
        StatusChanged?.Invoke(_translatorReady ? "" : "翻译模型下载/加载失败，只显示英文原文");
    }

    /// <summary>停止流水线与音频播出。回调保持挂接（音频继续走 NAudio），下次启用无需重挂。</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _pipeline?.Stop(); } catch { }
        try { _pipeline?.Dispose(); } catch { }
        _pipeline = null;
        _capture.Stop();
    }

    /// <summary>喂音频的循环：PCM → 重采样 → 切块 → 送进流水线；模型未就绪时直接丢窗，不积压。</summary>
    private async Task PipelineLoopAsync(CancellationToken ct)
    {
        var reader = _capture.PcmReader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var pcm))
                {
                    if (ct.IsCancellationRequested) return;
                    // PCM 是 48kHz 立体声 16 位
                    var shorts = new short[pcm.Length / 2];
                    Buffer.BlockCopy(pcm, 0, shorts, 0, pcm.Length);
                    var mono = LiveSubtitleChunker.ResampleStereo48kToMono16k(shorts);
                    var window = _chunker.Feed(mono);
                    if (window is null) continue;
                    if (!_modelReady)
                    {
                        // 模型未就绪：直播追不上旧音频，直接丢窗，不积压。
                        continue;
                    }
                    _pipeline?.EnqueueWindow(window);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            AppLog.Error($"live subtitle pipeline failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _capture.Dispose();
        _transcriber.Dispose();
        _translator.Dispose();
        try { _cts?.Dispose(); } catch { }
    }
}
