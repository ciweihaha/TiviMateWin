using System.Diagnostics;
using System.Threading.Channels;

namespace TiviMateWin.Services;

/// <summary>一条实时字幕：英文原文 + 中文译文（翻译失败时中文即英文原文）。</summary>
public sealed record LiveSubtitleLine(string English, string Chinese, DateTimeOffset At);

/// <summary>
/// 直播实时字幕两级流水线：听写与翻译并行——听写下一窗的同时翻译上一窗。
/// 串行管线每窗耗时 = 听写 + 翻译（3 秒窗约 3.9 秒 &gt; 窗长，越跑越掉队）；
/// 两级流水线吞吐由最慢的一级决定（窗长 / max(听写, 翻译)），端到端延迟 =
/// 窗（等窗满）+ 听写 + 翻译（不含排队等待）。两级之间用有界队列交接，
/// 满时丢最旧（直播追新不追旧，宁可丢窗也不积压出越来越慢的字幕）；
/// 每级串行、FIFO，字幕顺序天然保持。
/// 任何一步失败只记日志、当窗跳过，永不挡播放；翻译熔断由调用方通过
/// isTranslatorBroken 传入，触发后整条流水线停掉（TranslatorBroken 事件）。
/// </summary>
public sealed class LiveSubtitlePipeline : IDisposable
{
    // 两级队列各最多攒 2 窗：再多就是陈旧音频，丢了比晚显示强。
    private const int QueueCapacity = 2;

    private readonly Func<short[], CancellationToken, Task<string?>> _transcribe;
    private readonly Func<string, CancellationToken, Task<string?>> _translate;
    private readonly Func<short[], bool> _isSilent;
    private readonly Func<bool>? _isTranslatorBroken;

    private readonly Channel<TranscribeItem> _transcribeChannel;
    private readonly Channel<TranslateItem> _translateChannel;

    private CancellationTokenSource? _cts;
    private Task? _transcribeTask;
    private Task? _translateTask;
    private long _seq;
    private long _received;
    private long _dropped;
    private long _transcribed;
    private long _translated;
    private bool _disposed;

    /// <summary>新字幕就绪（后台线程触发，调用方自行切回 UI 线程）。</summary>
    public event Action<LiveSubtitleLine>? SubtitleReady;

    /// <summary>翻译熔断：流水线已停，调用方应停掉字幕、播放继续。</summary>
    public event Action? TranslatorBroken;

    /// <summary>已入队的窗口数（含被丢弃的）。</summary>
    public long WindowsReceived => Interlocked.Read(ref _received);

    /// <summary>队列满时丢掉的窗口数（听写级 + 翻译级合计）。</summary>
    public long WindowsDropped => Interlocked.Read(ref _dropped);

    /// <summary>听写出英文的窗口数。</summary>
    public long WindowsTranscribed => Interlocked.Read(ref _transcribed);

    /// <summary>发出字幕的窗口数。</summary>
    public long WindowsTranslated => Interlocked.Read(ref _translated);

    /// <summary>最近一窗听写耗时（毫秒）。</summary>
    public long LastTranscribeMs { get; private set; }

    /// <summary>最近一窗翻译耗时（毫秒）。</summary>
    public long LastTranslateMs { get; private set; }

    /// <summary>最近一窗端到端延迟（毫秒：窗口入队 → 字幕发出，含两级处理与排队）。</summary>
    public long LastEndToEndMs { get; private set; }

    public LiveSubtitlePipeline(
        Func<short[], CancellationToken, Task<string?>> transcribe,
        Func<string, CancellationToken, Task<string?>> translate,
        Func<short[], bool>? isSilent = null,
        Func<bool>? isTranslatorBroken = null)
    {
        _transcribe = transcribe;
        _translate = translate;
        _isSilent = isSilent ?? (w => LiveSubtitleChunker.IsSilent(w));
        _isTranslatorBroken = isTranslatorBroken;
        _transcribeChannel = Channel.CreateBounded<TranscribeItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        _translateChannel = Channel.CreateBounded<TranslateItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>启动两级处理任务（幂等）；停止用 <see cref="Stop"/>。</summary>
    public void Start()
    {
        if (_disposed || _cts is not null) return;
        _cts = new CancellationTokenSource();
        _transcribeTask = Task.Run(() => TranscribeLoopAsync(_cts.Token));
        _translateTask = Task.Run(() => TranslateLoopAsync(_cts.Token));
    }

    /// <summary>停掉两级处理任务（幂等）；已在队列里的窗直接丢弃，不再出字幕。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    /// <summary>送入一窗 16kHz 单声道音频；队列满时丢最旧的窗（记入 WindowsDropped）。</summary>
    public void EnqueueWindow(short[] window)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _received);
        if (_transcribeChannel.Reader.Count >= QueueCapacity)
            Interlocked.Increment(ref _dropped);
        _transcribeChannel.Writer.TryWrite(new TranscribeItem(
            Interlocked.Increment(ref _seq), window, DateTimeOffset.UtcNow));
    }

    private async Task TranscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _transcribeChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) return;
                if (_isSilent(item.Window)) continue;
                var sw = Stopwatch.StartNew();
                var english = await _transcribe(item.Window, ct).ConfigureAwait(false);
                sw.Stop();
                LastTranscribeMs = sw.ElapsedMilliseconds;
                if (string.IsNullOrWhiteSpace(english)) continue;
                // 整窗只是非语音标记（如 "(upbeat music)"）：不显示、不翻译。
                if (LiveSubtitleChunker.IsNonSpeechMarker(english)) continue;
                Interlocked.Increment(ref _transcribed);
                if (_translateChannel.Reader.Count >= QueueCapacity)
                    Interlocked.Increment(ref _dropped);
                _translateChannel.Writer.TryWrite(new TranslateItem(
                    item.Seq, english, item.EnqueuedAt, sw.ElapsedMilliseconds));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            AppLog.Error($"live subtitle transcribe stage failed: {ex.Message}");
        }
    }

    private async Task TranslateLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _translateChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) return;
                var sw = Stopwatch.StartNew();
                var chinese = await _translate(item.English, ct).ConfigureAwait(false);
                sw.Stop();
                LastTranslateMs = sw.ElapsedMilliseconds;
                if (_isTranslatorBroken?.Invoke() == true)
                {
                    // 熔断：推理连续失败，字幕停掉、播放继续。
                    TranslatorBroken?.Invoke();
                    Stop();
                    return;
                }
                Interlocked.Increment(ref _translated);
                LastEndToEndMs = (long)(DateTimeOffset.UtcNow - item.EnqueuedAt).TotalMilliseconds;
                SubtitleReady?.Invoke(new LiveSubtitleLine(
                    item.English, chinese ?? item.English, DateTimeOffset.UtcNow));
                AppLog.Info($"live subtitle: [{item.English}] => [{chinese ?? item.English}]");
                AppLog.Info($"live subtitle: transcribe {item.TranscribeMs}ms " +
                            $"translate {LastTranslateMs}ms end-to-end {LastEndToEndMs}ms");
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            AppLog.Error($"live subtitle translate stage failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _transcribeChannel.Writer.TryComplete();
        _translateChannel.Writer.TryComplete();
        try { _cts?.Dispose(); } catch { }
    }

    private sealed record TranscribeItem(long Seq, short[] Window, DateTimeOffset EnqueuedAt);

    private sealed record TranslateItem(long Seq, string English, DateTimeOffset EnqueuedAt, long TranscribeMs);
}
