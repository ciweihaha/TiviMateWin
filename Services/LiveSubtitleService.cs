using System.Diagnostics;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TiviMateWin.Services;

/// <summary>
/// 直播实时字幕编排：音频 → 切块 → 听写 → 翻译 → 字幕事件。
/// 默认两级流水线（听写与翻译分核并行）；串行模式（LiveSubtitleSerial）
/// 则同一线程依次跑，听写与翻译各用全部核——流水线省的是吞吐不是延迟，
/// 一窗仍要依次过听写+翻译，分核后每级都变慢时串行反而更快。
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
    private Task? _watchdogTask;
    private volatile string _serialStage = "idle"; // 串行模式当前阶段（看门狗用）：idle/transcribing/translating
    private bool _audioAttached;
    private bool _modelReady;
    private bool _translatorReady;
    private bool _disposed;

    // 串行模式的计数与音频时钟（流水线模式不用，走 LiveSubtitlePipeline 的计数）。
    private readonly bool _serialMode;
    private long _serialReceived;
    private long _serialDropped;
    private long _serialTranscribed;
    private long _serialTranslated;
    private long _serialLastTranscribeMs;
    private long _serialLastTranslateMs;
    private long _serialLastEndToEndMs;
    private DateTimeOffset? _audioStartWall; // 第一块 PCM 到达的墙钟（音频时钟起点）
    private long _windowsCompleted;          // 切块器吐出的窗口数；音频时间 ≈ 数量 × 窗长

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

    /// <summary>是否串行管线（设置改了需重建服务才生效）。</summary>
    public bool Serial => _serialMode;

    /// <summary>最近一窗听写耗时（毫秒）。</summary>
    public long LastTranscribeMs => _pipeline?.LastTranscribeMs ?? Interlocked.Read(ref _serialLastTranscribeMs);

    /// <summary>最近一窗翻译耗时（毫秒）。</summary>
    public long LastTranslateMs => _pipeline?.LastTranslateMs ?? Interlocked.Read(ref _serialLastTranslateMs);

    /// <summary>最近一窗听写+翻译合计耗时（毫秒，不含切块等待与排队）。</summary>
    public long LastPipelineMs => LastTranscribeMs + LastTranslateMs;

    /// <summary>最近一窗端到端延迟（毫秒：窗口入队/攒满 → 字幕发出，含排队等待）。</summary>
    public long LastEndToEndMs => _pipeline?.LastEndToEndMs ?? Interlocked.Read(ref _serialLastEndToEndMs);

    /// <summary>累计收到的窗口数。</summary>
    public long WindowsReceived => _pipeline?.WindowsReceived ?? Interlocked.Read(ref _serialReceived);

    /// <summary>累计丢掉的窗口数（队列满时丢最旧；串行模式为陈旧窗丢弃）。</summary>
    public long WindowsDropped => _pipeline?.WindowsDropped ?? Interlocked.Read(ref _serialDropped);

    /// <summary>累计听写出英文的窗口数。</summary>
    public long WindowsTranscribed => _pipeline?.WindowsTranscribed ?? Interlocked.Read(ref _serialTranscribed);

    /// <summary>累计发出字幕的窗口数。</summary>
    public long WindowsTranslated => _pipeline?.WindowsTranslated ?? Interlocked.Read(ref _serialTranslated);

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
        _serialMode = config.LiveSubtitleSerial;
        int transcribeThreads, translateThreads;
        if (_serialMode)
        {
            // 串行：听写与翻译不同时跑，各用全部核（留 1 核给播放与界面）。
            transcribeThreads = LiveSubtitleTranscriber.TranscribeThreads;
            translateThreads = LiveSubtitleTranslator.InferenceThreads;
        }
        else
        {
            (transcribeThreads, translateThreads) = ResolveThreads(config);
        }
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
        // 串行模式的计数与音频时钟每轮清零（流水线模式走自己的计数）。
        _audioStartWall = null;
        _windowsCompleted = 0;
        Interlocked.Exchange(ref _serialReceived, 0);
        Interlocked.Exchange(ref _serialDropped, 0);
        Interlocked.Exchange(ref _serialTranscribed, 0);
        Interlocked.Exchange(ref _serialTranslated, 0);
        Interlocked.Exchange(ref _serialLastTranscribeMs, 0);
        Interlocked.Exchange(ref _serialLastTranslateMs, 0);
        Interlocked.Exchange(ref _serialLastEndToEndMs, 0);
        if (!_serialMode)
        {
            _pipeline = new LiveSubtitlePipeline(
                transcribe: (window, ct) => _transcriber.TranscribeAsync(window, ct),
                translate: (text, ct) => _translator.TranslateAsync(text, ct),
                isTranslatorBroken: () => _translator.IsBroken);
            _pipeline.SubtitleReady += line => SubtitleReady?.Invoke(line);
            _pipeline.TranslatorBroken += OnTranslatorBroken;
            _pipeline.Start();
        }
        _feederTask = Task.Run(() => PipelineLoopAsync(_cts.Token));
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(_cts.Token));
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

    /// <summary>喂音频的循环：PCM → 重采样 → 切块 → 送进流水线；模型未就绪时直接丢窗，不积压。
    /// 等 PCM 带 1 秒超时：断流时每超时 1 秒就把音频时钟往前拨 1 秒（断流不算落后），
    /// 否则墙钟照走、音频不走，时钟落后越拉越大，重连后每窗都判陈旧、串行模式永久丢窗。</summary>
    private async Task PipelineLoopAsync(CancellationToken ct)
    {
        var reader = _capture.PcmReader;
        var stalledSeconds = 0; // 连续等不到 PCM 的秒数（断流时长，恢复后记日志）
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool hasData;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        hasData = await reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 1 秒没等到 PCM：断流，音频时钟往前拨 1 秒。
                        _audioStartWall = LiveSubtitleSerialClock.AdvanceForIdleTime(
                            _audioStartWall, TimeSpan.FromSeconds(1));
                        if (stalledSeconds == 0)
                            AppLog.Info("live subtitle: audio stalled, advancing audio clock");
                        stalledSeconds++;
                        continue;
                    }
                }
                if (!hasData) break; // 音频通道已关闭
                if (stalledSeconds > 0)
                {
                    AppLog.Info($"live subtitle: audio resumed after {stalledSeconds}s stall");
                    stalledSeconds = 0;
                }
                while (reader.TryRead(out var pcm))
                {
                    if (ct.IsCancellationRequested) return;
                    _audioStartWall ??= DateTimeOffset.UtcNow;
                    // PCM 是 48kHz 立体声 16 位
                    var shorts = new short[pcm.Length / 2];
                    Buffer.BlockCopy(pcm, 0, shorts, 0, pcm.Length);
                    var mono = LiveSubtitleChunker.ResampleStereo48kToMono16k(shorts);
                    var window = _chunker.Feed(mono);
                    if (window is null) continue;
                    // 窗计数 = 音频时钟：每出一窗即过去一窗长的音频（模型未就绪丢掉的窗也算）。
                    Interlocked.Increment(ref _windowsCompleted);
                    if (!_modelReady)
                    {
                        // 模型未就绪：直播追不上旧音频，直接丢窗，不积压。
                        continue;
                    }
                    if (_serialMode)
                    {
                        // 串行：本循环里听写 → 翻译依次跑（期间 PCM 在有界队列里等，满了丢最旧）。
                        var droppedStale = await ProcessWindowSerialAsync(window, DateTimeOffset.UtcNow, ct)
                            .ConfigureAwait(false);
                        if (droppedStale)
                        {
                            // 掉队已无法追回（堵住时通道丢过 PCM，音频时钟永久落后）：丢掉通道里
                            // 积压的旧 PCM、清空切块器队列、音频时钟重同步到当前，从直播点重新开始。
                            while (reader.TryRead(out _)) { }
                            _chunker.Clear();
                            _audioStartWall = null;
                            _windowsCompleted = 0;
                            AppLog.Info("live subtitle: resynced to live audio after stale drop");
                        }
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

    /// <summary>看门狗：每 30 秒记一次计数与当前阶段；串行卡在听写/翻译里、或喂音频循环已死，
    /// 都能从这行日志直接看出来（卡住时 stage 停在 transcribing/translating，循环死时连这行都没了）。</summary>
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                AppLog.Info($"live subtitle watchdog: stage={_serialStage} received={WindowsReceived} dropped={WindowsDropped} transcribed={WindowsTranscribed} translated={WindowsTranslated} lastTranscribe={LastTranscribeMs}ms lastTranslate={LastTranslateMs}ms lastEndToEnd={LastEndToEndMs}ms");
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    /// <summary>
    /// 串行处理一窗：听写 → 翻译依次跑（各用全部核）。陈旧窗直接丢：
    /// 窗的音频在起点后 windowsCompleted × 窗长处结束，若到现在已超过一窗长，
    /// 说明处理速度跟不上说话速度，追新不追旧（与流水线队列满丢最旧同理）。
    /// 端到端 = 窗攒满（Feed 吐出时）→ 字幕发出，含排队等待，与流水线口径一致。
    /// 日志格式与流水线相同，便于同一口径横向比。
    /// 返回 true 表示本窗因陈旧被丢弃（调用方应重同步到直播点）。
    /// </summary>
    private async Task<bool> ProcessWindowSerialAsync(short[] window, DateTimeOffset windowCompletedAt, CancellationToken ct)
    {
        Interlocked.Increment(ref _serialReceived);
        var staleness = LiveSubtitleSerialClock.WindowStalenessSeconds(
            _audioStartWall, _windowsCompleted, WindowSeconds, windowCompletedAt);
        if (LiveSubtitleSerialClock.IsStaleWindow(staleness, WindowSeconds))
        {
            Interlocked.Increment(ref _serialDropped);
            AppLog.Info($"live subtitle: stale window dropped (staleness {staleness:F1}s, window #{WindowsReceived})");
            return true;
        }
        if (LiveSubtitleChunker.IsSilent(window)) return false;
        _serialStage = "transcribing";
        var sw = Stopwatch.StartNew();
        var english = await _transcriber.TranscribeAsync(window, ct).ConfigureAwait(false);
        sw.Stop();
        var transcribeMs = sw.ElapsedMilliseconds;
        Interlocked.Exchange(ref _serialLastTranscribeMs, transcribeMs);
        if (string.IsNullOrWhiteSpace(english)) { _serialStage = "idle"; return false; }
        // 整窗只是非语音标记（如 "(upbeat music)"）：不显示、不翻译。
        if (LiveSubtitleChunker.IsNonSpeechMarker(english)) { _serialStage = "idle"; return false; }
        Interlocked.Increment(ref _serialTranscribed);
        _serialStage = "translating";
        sw.Restart();
        var chinese = await _translator.TranslateAsync(english, ct).ConfigureAwait(false);
        sw.Stop();
        var translateMs = sw.ElapsedMilliseconds;
        Interlocked.Exchange(ref _serialLastTranslateMs, translateMs);
        if (_translator.IsBroken)
        {
            // 熔断：推理连续失败，字幕停掉、播放继续。
            _serialStage = "idle";
            OnTranslatorBroken();
            return false;
        }
        Interlocked.Increment(ref _serialTranslated);
        var endToEndMs = (long)(DateTimeOffset.UtcNow - windowCompletedAt).TotalMilliseconds;
        Interlocked.Exchange(ref _serialLastEndToEndMs, endToEndMs);
        _serialStage = "idle";
        SubtitleReady?.Invoke(new LiveSubtitleLine(english, chinese ?? english, DateTimeOffset.UtcNow));
        AppLog.Info($"live subtitle: [{english}] => [{chinese ?? english}]");
        AppLog.Info($"live subtitle: transcribe {transcribeMs}ms (infer {_transcriber.LastTranscribeMs}ms) translate {translateMs}ms end-to-end {endToEndMs}ms");
        return false;
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
