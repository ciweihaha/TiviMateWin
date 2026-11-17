using System.Diagnostics;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TiviMateWin.Services;

/// <summary>一条实时字幕：英文原文 + 中文译文（翻译失败时中文即英文原文）。</summary>
public sealed record LiveSubtitleLine(string English, string Chinese, DateTimeOffset At);

/// <summary>
/// 直播实时字幕编排：音频 → 切块 → 听写 → 翻译 → 字幕事件。
/// 流水线单线程串行（Whisper 处理器一次只跑一窗）；任何一步失败只记日志、
/// 当窗跳过，永不挡播放。延迟实测值（听写/翻译/合计）暴露给调用方。
/// </summary>
public sealed class LiveSubtitleService : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly AppConfig _config;
    private readonly LiveSubtitleAudioCapture _capture;
    private readonly LiveSubtitleChunker _chunker;
    private readonly LiveSubtitleTranscriber _transcriber;
    private readonly LiveSubtitleTranslator _translator;
    private CancellationTokenSource? _cts;
    private Task? _pipelineTask;
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

    /// <summary>当前切块窗口（秒，设置改了需重建服务才生效）。</summary>
    public int WindowSeconds => _config.LiveSubtitleWindowSeconds;

    /// <summary>最近一窗听写耗时（毫秒）。</summary>
    public long LastTranscribeMs => _transcriber.LastTranscribeMs;

    /// <summary>最近一窗翻译耗时（毫秒）。</summary>
    public long LastTranslateMs { get; private set; }

    /// <summary>最近一窗听写+翻译合计耗时（毫秒，不含切块等待）。</summary>
    public long LastPipelineMs { get; private set; }

    public LiveSubtitleService(MediaPlayer player, AppConfig config)
    {
        _player = player;
        _config = config;
        _capture = new LiveSubtitleAudioCapture(player);
        _chunker = new LiveSubtitleChunker(Math.Clamp(config.LiveSubtitleWindowSeconds, 1, 10));
        _transcriber = new LiveSubtitleTranscriber();
        _translator = new LiveSubtitleTranslator(config.LiveSubtitleModel);
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
        _pipelineTask = Task.Run(() => PipelineLoopAsync(_cts.Token));
        _ = PrepareAsync(_cts.Token);
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
        _capture.Stop();
    }

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
                    await ProcessWindowAsync(window, ct).ConfigureAwait(false);
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

    private async Task ProcessWindowAsync(short[] window, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!_modelReady)
        {
            // 模型未就绪：直播追不上旧音频，直接丢窗，不积压。
            return;
        }
        if (LiveSubtitleChunker.IsSilent(window)) return;
        var english = await _transcriber.TranscribeAsync(window, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(english)) return;
        var translateSw = Stopwatch.StartNew();
        var chinese = await _translator.TranslateAsync(english, ct).ConfigureAwait(false);
        translateSw.Stop();
        LastTranslateMs = translateSw.ElapsedMilliseconds;
        sw.Stop();
        LastPipelineMs = sw.ElapsedMilliseconds;
        if (_translator.IsBroken)
        {
            // 熔断：推理连续失败，字幕停掉、播放继续（重开开关重建服务才会再试）。
            StatusChanged?.Invoke("翻译连续失败，实时字幕已停（播放不受影响）");
            AppLog.Error("live subtitle stopped: translator broken");
            Stop();
            return;
        }
        SubtitleReady?.Invoke(new LiveSubtitleLine(english, chinese ?? english, DateTimeOffset.UtcNow));
        AppLog.Info($"live subtitle: [{english}] => [{chinese ?? english}]");
        AppLog.Info($"live subtitle: transcribe {_transcriber.LastTranscribeMs}ms " +
                    $"translate {LastTranslateMs}ms window {window.Length / 16000}s");
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
