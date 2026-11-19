using System.Diagnostics;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TiviMateWin.Services;

/// <summary>
/// 直播实时字幕编排：音频 → 切块 → 听写 → 翻译 → 字幕事件。
/// 默认串行模式（LiveSubtitleSerial）：同一线程依次跑，听写与翻译各用全部核——
/// 流水线省的是吞吐不是延迟，一窗仍要依次过听写+翻译，分核后每级都变慢时串行反而更快；
/// 且串行没有级间队列，处理跟不上时只丢陈旧窗、不积压出越来越慢的字幕。
/// 任何一步失败只记日志、当窗跳过，永不挡播放。延迟实测值暴露给调用方。
/// 默认 tiny.en + 1.5 秒窗：base.en + 3 秒窗在两台机器上端到端约 11 秒（目标 6 秒内），
/// 瓶颈是 CPU 听写吞吐（云电脑听写 3 秒，台式机听写 5–6 秒）；翻译走免费接口（8 秒超时，
/// 失败只跳过该行）。v1.4.33 重构后本地大模型已删掉（用户 10:36：效果不好），翻译只走
/// 微软 Azure / Cloudflare 免费接口。
/// </summary>
public sealed class LiveSubtitleService : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly AppConfig _config;
    private readonly LiveSubtitleAudioCapture _capture;
    private readonly LiveSubtitleChunker _chunker;
    private readonly LiveSubtitleTranscriber _transcriber;
    private readonly IFreeApiTranslator? _apiTranslator;
    private LiveSubtitlePipeline? _pipeline;
    private CancellationTokenSource? _cts;
    private Task? _feederTask;
    private Task? _watchdogTask;

    /// <summary>当前台是否开翻译（v1.4.33）：false 时只丢窗、不听写不翻译、不出字幕；
    /// 音频捕获与模型保持加载，切回英语台立刻恢复。由 MainWindow 按翻译模式+台语种+手动强制决定。</summary>
    public volatile bool TranslationActive = true;
    private volatile string _serialStage = "idle"; // 串行模式当前阶段（看门狗用）：idle/transcribing/translating
    private bool _audioAttached;
    private bool _modelReady;
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

    /// <summary>当前用的听写模型文件名（设置改了需重建服务才生效）。</summary>
    public string WhisperModel => _config.LiveSubtitleWhisperModel;

    /// <summary>当前切块窗口（秒，设置改了需重建服务才生效）。</summary>
    public double WindowSeconds => _config.LiveSubtitleWindowSeconds;

    /// <summary>听写实际用的线程数（设置改了需重建服务才生效）。</summary>
    public int TranscribeThreads { get; }

    /// <summary>是否串行管线（设置改了需重建服务才生效）。</summary>
    public bool Serial => _serialMode;

    /// <summary>当前用的免费接口服务商（Azure/Cloudflare；设置改了需重建服务）。</summary>
    public string FreeApiProvider => _config.FreeApiProvider;

    /// <summary>免费接口 Key（Azure 用；设置改了需重建服务；日志里绝不出现）。</summary>
    public string? FreeApiKey => _config.FreeApiKey;

    /// <summary>Azure 区域（可选；设置改了需重建服务）。</summary>
    public string? AzureRegion => _config.AzureRegion;

    /// <summary>免费接口用的 CF 账号 ID（Cloudflare 服务商用；设置改了需重建服务）。</summary>
    public string? CloudflareAccountId => _config.CloudflareAccountId;

    /// <summary>免费接口用的 CF API 令牌（Cloudflare 服务商用；设置改了需重建服务；日志里绝不出现）。</summary>
    public string? CloudflareApiToken => _config.CloudflareApiToken;

    /// <summary>免费接口连续失败次数（到阈值后冷却 5 分钟不再每行重试，见 TranslateAsync）。</summary>
    public int ApiFailureStreak => _apiFailureStreak;

    // 免费接口翻译（v1.4.33 重构）：微软 Azure / Cloudflare 免费层；
    // 连续失败 5 次后冷却 5 分钟，期间直接跳过该行（显示英文原文），不再每行等 8 秒超时
    // 把字幕拖慢。本地大模型已删掉（用户 10:36：效果不好），不再回落本地。
    private const int ApiMaxConsecutiveFailures = 5;
    private const int ApiCooldownMinutes = 5;
    private int _apiFailureStreak;
    private DateTimeOffset _apiCooldownUntilUtc = DateTimeOffset.MinValue;

    /// <summary>累计因单窗超时被放弃的窗口数（听写 6 秒未出结果）。</summary>
    public long TranscribeTimeouts => _transcriber.TranscribeTimeouts;

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
    /// 听写线程数：留 1 核给播放与界面，其余给听写（翻译走免费接口，不占本地核）；
    /// 配置里 &gt;0 的值手动覆盖（0=自动）。
    /// </summary>
    public static int ResolveTranscribeThreads(AppConfig config)
    {
        if (config.LiveSubtitleTranscribeThreads > 0)
            return Math.Clamp(config.LiveSubtitleTranscribeThreads, 1, 16);
        // 自动：留 1 核给播放与界面，其余给听写（与 LiveSubtitleTranscriber.TranscribeThreads 同口径）。
        return LiveSubtitleTranscriber.TranscribeThreads;
    }

    public LiveSubtitleService(MediaPlayer player, AppConfig config)
    {
        _player = player;
        _config = config;
        _capture = new LiveSubtitleAudioCapture(player);
        _chunker = new LiveSubtitleChunker(Math.Clamp(config.LiveSubtitleWindowSeconds, 1, 10));
        _serialMode = config.LiveSubtitleSerial;
        // 听写线程数：串行与流水线都一样（翻译走接口，不占本地核）。
        TranscribeThreads = _serialMode
            ? LiveSubtitleTranscriber.TranscribeThreads
            : ResolveTranscribeThreads(config);
        _transcriber = new LiveSubtitleTranscriber(threads: TranscribeThreads,
            modelFileName: config.LiveSubtitleWhisperModel);
        // v1.4.33 重构：翻译只走免费接口（Azure/Cloudflare），凭据缺失时
        // 翻译器为 null——TranslateAsync 直接返回 null，调用方显示英文原文。
        _apiTranslator = FreeApiTranslatorFactory.Create(config.FreeApiProvider, config.FreeApiKey,
            config.AzureRegion, config.CloudflareAccountId, config.CloudflareApiToken);
        _transcriber.ModelDownloadProgress += (downloaded, total) =>
        {
            if (total > 0)
                StatusChanged?.Invoke($"正在下载语音模型 {downloaded * 100 / total}%");
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
                translate: (text, ct) => TranslateAsync(text, ct));
            _pipeline.SubtitleReady += line => SubtitleReady?.Invoke(line);
            _pipeline.Start();
        }
        _feederTask = Task.Run(() => PipelineLoopAsync(_cts.Token));
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(_cts.Token));
        _ = PrepareAsync(_cts.Token);
    }

    /// <summary>
    /// 翻译路由（v1.4.33 重构）：只走免费接口（Azure/Cloudflare），
    /// 本地大模型已删掉，不再回落本地。接口失败返回 null（调用方显示英文原文、当行跳过）。
    /// 接口连续失败 5 次后冷却 5 分钟——期间不再每行等 8 秒超时，直接返回 null，
    /// 冷却到点自动再试接口。翻译器为 null（凭据未填）时直接返回 null。
    /// </summary>
    private async Task<string?> TranslateAsync(string english, CancellationToken ct)
    {
        if (_apiTranslator is null) return null;
        if (DateTimeOffset.UtcNow < _apiCooldownUntilUtc) return null;
        var sw = Stopwatch.StartNew();
        var viaApi = await _apiTranslator.TranslateAsync(english, ct).ConfigureAwait(false);
        sw.Stop();
        if (!string.IsNullOrWhiteSpace(viaApi))
        {
            _apiFailureStreak = 0;
            AppLog.Info($"live subtitle: translate via {_apiTranslator.ProviderName} {sw.ElapsedMilliseconds}ms");
            return viaApi;
        }
        if (++_apiFailureStreak >= ApiMaxConsecutiveFailures)
        {
            _apiCooldownUntilUtc = DateTimeOffset.UtcNow.AddMinutes(ApiCooldownMinutes);
            AppLog.Error($"live subtitle: {_apiTranslator.ProviderName} 翻译连续失败 {_apiFailureStreak} 次，冷却 {ApiCooldownMinutes} 分钟（期间跳过该行，显示英文原文）");
        }
        else
        {
            AppLog.Info($"live subtitle: {_apiTranslator.ProviderName} 翻译失败，跳过该行（显示英文原文）");
        }
        return null;
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        // 只准备听写模型：翻译走免费接口，不下载本地模型。
        StatusChanged?.Invoke("正在准备语音模型…");
        _modelReady = await _transcriber.EnsureModelAsync(ct).ConfigureAwait(false);
        if (!_modelReady)
        {
            StatusChanged?.Invoke("语音模型下载失败，实时字幕暂不可用");
            return;
        }
        if (_apiTranslator is null)
        {
            StatusChanged?.Invoke("免费接口未配置（去设置里选服务商、填 Key），只显示英文原文");
            return;
        }
        StatusChanged?.Invoke("");
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
                    if (!TranslationActive)
                    {
                        // 本台不翻译（v1.4.33）：丢窗（不听写、不翻译、不出字幕），只记音频时钟；
                        // 模型保持加载，切回英语台立刻恢复。
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
                AppLog.Info($"live subtitle watchdog: stage={_serialStage} received={WindowsReceived} dropped={WindowsDropped} transcribed={WindowsTranscribed} translated={WindowsTranslated} transcribeTimeouts={TranscribeTimeouts} lastTranscribe={LastTranscribeMs}ms lastTranslate={LastTranslateMs}ms lastEndToEnd={LastEndToEndMs}ms");
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
        var chinese = await TranslateAsync(english, ct).ConfigureAwait(false);
        sw.Stop();
        var translateMs = sw.ElapsedMilliseconds;
        Interlocked.Exchange(ref _serialLastTranslateMs, translateMs);
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
        _apiTranslator?.Dispose();
        try { _cts?.Dispose(); } catch { }
    }
}
