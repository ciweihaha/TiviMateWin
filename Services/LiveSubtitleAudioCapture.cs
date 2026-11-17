using System.Runtime.InteropServices;
using System.Threading.Channels;
using LibVLCSharp.Shared;
using NAudio.Wave;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TiviMateWin.Services;

/// <summary>
/// 直播音频捕获：把 LibVLC 音频回调接管为 PCM 源。
/// 注意：挂上回调后 LibVLC 不再自己出声，声音由这里经 NAudio 播出；
/// 不设音量回调时 libvlc 在软件里应用音量，回调收到的 PCM 已按音量缩放，
/// 应用的音量滑杆照常有效。回调只做内存复制+入队，绝不调 libvlc 函数
/// （libvlc 回调里调回 libvlc 会死锁），也绝不在回调里抛异常（会直通原生层崩进程）。
/// 无声卡时 NAudio 初始化失败也不影响听写：只记日志，PCM 照常进队列。
/// </summary>
public sealed class LiveSubtitleAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerSample = 2;

    private readonly MediaPlayer _player;
    private readonly Channel<byte[]> _pcmChannel;
    private readonly BufferedWaveProvider _waveProvider;
    private WaveOut? _waveOut;

    // 委托必须以字段强引用持有，防 GC 回收（LibVLCSharp 自己也存一份，双保险）。
    private readonly MediaPlayer.LibVLCAudioPlayCb _playCb;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCb;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCb;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCb;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCb;

    private bool _attached;
    private bool _stopped;
    private bool _disposed;

    public ChannelReader<byte[]> PcmReader => _pcmChannel.Reader;

    public LiveSubtitleAudioCapture(MediaPlayer player)
    {
        _player = player;
        // 有界队列、满时丢最旧：直播追不上旧音频，宁可丢窗也不积压。
        _pcmChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var waveFormat = new WaveFormat(SampleRate, 16, Channels);
        _waveProvider = new BufferedWaveProvider(waveFormat)
        {
            // 默认缓冲 5 秒音频（NAudio 3.x 不再允许改缓冲大小）；满时丢弃新样本，不抛异常。
            DiscardOnBufferOverflow = true,
        };
        _playCb = OnPlayAudio;
        _pauseCb = OnPauseAudio;
        _resumeCb = OnResumeAudio;
        _flushCb = OnFlushAudio;
        _drainCb = OnDrainAudio;
    }

    /// <summary>
    /// 挂音频回调（幂等）。须在 MediaPlayer.Play 之前调用——格式协商在流启动时进行，
    /// 播起来之后再挂要等下次 Play 才生效。由调用方在 PlayAsync 拿锁后执行。
    /// </summary>
    public void Attach()
    {
        if (_attached || _disposed) return;
        _player.SetAudioFormat("S16N", SampleRate, Channels);
        _player.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);
        _attached = true;
        try
        {
            _waveOut = new WaveOut();
            _waveOut.Init(_waveProvider);
            _waveOut.Play();
        }
        catch (Exception ex)
        {
            // 无声卡/声卡被占：不出声也不影响听写，PCM 照常进队列。
            AppLog.Error($"live subtitle audio output unavailable (transcription continues): {ex.Message}");
            _waveOut = null;
        }
    }

    /// <summary>停播出、清缓冲、关闭 PCM 队列（流水线随之退出）。</summary>
    public void Stop()
    {
        _stopped = true;
        try { _waveOut?.Stop(); } catch { }
        try { _waveProvider.ClearBuffer(); } catch { }
        _pcmChannel.Writer.TryComplete();
    }

    private void OnPlayAudio(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_stopped || _disposed || count == 0) return;
        try
        {
            // count 是帧数（每声道样点数），S16N 立体声：字节数 = 帧数 × 声道数 × 2
            var bytes = checked((int)count * Channels * BytesPerSample);
            var buffer = new byte[bytes];
            Marshal.Copy(samples, buffer, 0, bytes);
            if (_waveOut != null)
            {
                try { _waveProvider.AddSamples(buffer, 0, bytes); } catch { }
            }
            _pcmChannel.Writer.TryWrite(buffer);
        }
        catch
        {
            // 音频回调里永不抛异常
        }
    }

    private void OnPauseAudio(IntPtr data, long pts)
    {
        try { _waveOut?.Pause(); } catch { }
    }

    private void OnResumeAudio(IntPtr data, long pts)
    {
        try { _waveOut?.Play(); } catch { }
    }

    private void OnFlushAudio(IntPtr data, long pts)
    {
        // 停止/切源时 libvlc 要求丢弃待播缓冲
        try { _waveProvider.ClearBuffer(); } catch { }
    }

    private void OnDrainAudio(IntPtr data)
    {
        // NAudio 自然播完缓冲，无需阻塞等待（回调里阻塞会卡住 libvlc 音频线程）
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _waveOut?.Dispose(); } catch { }
    }
}
