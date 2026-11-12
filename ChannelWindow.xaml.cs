using System.ComponentModel;
using System.Windows;
using LibVLCSharp.Shared;
using TiviMateWin.Models;
using TiviMateWin.Services;

namespace TiviMateWin;

/// <summary>独立频道播放窗口：多窗口同时播放，每个窗口一个独立 MediaPlayer（共享 LibVLC 实例）</summary>
public partial class ChannelWindow : Window
{
    private readonly LibVLC _libvlc;
    private readonly MediaPlayer _player;
    private Media? _media;   // 当前 Media：Play 后需保持存活直到换台/关闭
    private ChannelItem? _channel;
    private int _volume;
    private readonly int _networkCache;   // 深查第 22 轮：旧代码写死 800，用户设置的网络缓存不生效
    // 播放串行化：libVLC MediaPlayer 非线程安全，快速换台时多个 Play 需串行执行
    private readonly SemaphoreSlim _playGate = new(1, 1);
    // F6(a)：窗口关闭中标志。Window_Closing 置位后，Play 不再启动新的播放，
    // 避免关闭后继续操作已释放的播放器/Media。
    private volatile bool _closing;
    private bool _teardownStarted; // 后台释放只启动一次：Window_Closing 里置位，避免重复 Dispose

    public ChannelWindow(LibVLC libvlc, int volume, int networkCache)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _volume = volume;
        _networkCache = networkCache;
        _player = new MediaPlayer(_libvlc) { Volume = _volume };
        VideoView.MediaPlayer = _player;
    }

    /// <summary>播放指定频道（串行化，避免并发调用 MediaPlayer 崩溃）</summary>
    public async void Play(ChannelItem ch)
    {
        // F6(a)：关闭中不再启动新播放。
        if (_closing) return;
        _channel = ch;
        Title = ch.Name;
        TitleText.Text = ch.Name;
        try
        {
            await _playGate.WaitAsync();
            try
            {
                if (_closing) return;   // 等待期间窗口已关闭：不再启动播放
                await Task.Run(() =>
                {
                    var media = ch.Url.Contains("://")
                        ? new Media(_libvlc, new Uri(ch.Url))
                        : new Media(_libvlc, ch.Url, FromType.FromPath);
                    media.AddOption($":network-caching={_networkCache}");
                    _player.Play(media);
                    var old = _media;
                    _media = media;   // 新 Media 接管
                    old?.Dispose();
                });
                AppLog.Info($"channel window play [{ch.Name}]");
            }
            finally { _playGate.Release(); }
        }
        catch (Exception ex)
        {
            AppLog.Error($"channel window play failed [{ch.Name}]: {ex.Message}");
        }
    }

    private void BtnVolUp_Click(object sender, RoutedEventArgs e)
    {
        _volume = Math.Min(125, _volume + 5);
        _player.Volume = _volume;
    }

    private void BtnVolDown_Click(object sender, RoutedEventArgs e)
    {
        _volume = Math.Max(0, _volume - 5);
        _player.Volume = _volume;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // F6(a)：先置位再 Stop，使关闭后到达的 Play 直接返回。
        _closing = true;
        // v1.4.15 1-3：删掉 _playGate.Wait(2s)——与主窗口同款，Play 的释放续体需回 UI 线程，
        // Window_Closing 正阻塞 UI 线程等这把锁，刚换台就关窗口必冻满 2 秒；
        // _closing 守卫已能拦住关闭后的后续播放操作（Play 里已有两处 _closing 检查）。
        // Stop() 可能阻塞（等卡住的流拆掉），连同 Dispose 一起放后台线程，
        // 避免关窗口时 UI 卡死；不要在 Window_Closed 里同步 Dispose 播放器——
        // Dispose → libvlc_media_player_release() → 销毁时 input_Stop + input_Close
        //（join 输入线程），和同步 Stop() 是同一处等待，UI 线程一样会堵死
        //（小窗 v1.4.13 关窗卡死的根因同款）。先断开视频面再后台释放。
        try { VideoView.MediaPlayer = null; } catch { }
        StartTeardown();
    }

    /// <summary>后台释放播放器：Stop + Dispose 全部在后台线程做，UI 线程绝不碰播放器释放。</summary>
    private void StartTeardown()
    {
        if (_teardownStarted) return;  // 只在 UI 线程调用，单线程置位即可
        _teardownStarted = true;
        var playerToStop = _player;
        var mediaToDispose = _media;
        _ = Task.Run(() =>
        {
            try { playerToStop.Stop(); } catch { }
            try { mediaToDispose?.Dispose(); } catch { }
            try { playerToStop.Dispose(); } catch { }
            AppLog.Info("channel player released on background thread");
        });
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        // 播放器释放已交给后台 StartTeardown，UI 线程在这里 Dispose 会阻塞。
        AppLog.Info($"channel window closed [{_channel?.Name}]");
    }
}
