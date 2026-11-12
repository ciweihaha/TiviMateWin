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
    // 在途播放的后台任务（Play 内 Task.Run 的 Play 调用本身）：Window_Closing 等它而非只等
    // _playGate，同主窗口 M1——释放续体需 UI 线程，关闭时 UI 线程正被阻塞，等门必然超时。
    private Task? _inFlightPlay;
    // F6(a)：窗口关闭中标志。Window_Closing 置位后，Play 不再启动新的播放，
    // 避免关闭后继续操作已释放的播放器/Media。
    private volatile bool _closing;

    public ChannelWindow(LibVLC libvlc, int volume, int networkCache)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _volume = volume;
        _networkCache = networkCache;
        _player = new MediaPlayer(_libvlc) { Volume = _volume };
        VideoView.MediaPlayer = _player;
    }

    /// <summary>标记为已由主窗口统一释放引擎（主窗口关闭时调用，避免重复释放）</summary>
    public void DetachEngine() => _engineReleased = true;

    private bool _engineReleased;

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
                var playTask = Task.Run(() =>
                {
                    var media = ch.Url.Contains("://")
                        ? new Media(_libvlc, new Uri(ch.Url))
                        : new Media(_libvlc, ch.Url, FromType.FromPath);
                    media.AddOption($":network-caching={_networkCache}");
                    if (_closing)
                    {
                        // M1：关闭已开始，不再触碰可能正在释放的播放器，丢弃新建的 Media。
                        media.Dispose();
                        return;
                    }
                    _player.Play(media);
                    var old = _media;
                    _media = media;   // 新 Media 接管
                    old?.Dispose();
                });
                _inFlightPlay = playTask;   // M1：供 Window_Closing 等待
                await playTask;
                if (!_closing) AppLog.Info($"channel window play [{ch.Name}]");
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
        // M1：等待在途播放的后台任务完成，而非等 _playGate（同主窗口：等门必然超时，
        // 释放续体需 UI 线程而关闭正阻塞 UI 线程；该任务在线程池完成，等它不会死锁）。
        try
        {
            var inFlight = _inFlightPlay;
            if (inFlight != null && !inFlight.IsCompleted && !inFlight.Wait(TimeSpan.FromSeconds(2)))
                AppLog.Error("channel window closing: in-flight play wait timed out, continuing close");
        }
        catch (AggregateException)
        {
            // 在途播放任务本身已失败（异常由 Play 的 catch 处理），等待目标已达成。
        }
        catch (Exception ex) { AppLog.Error($"channel window closing: in-flight play wait failed: {ex.Message}"); }
        try { _player.Stop(); } catch { }
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        // 若引擎已被主窗口统一释放，则不再 dispose 播放器/Media（避免二次释放崩溃）
        if (!_engineReleased)
        {
            try { _media?.Dispose(); } catch { }
            try { _player.Dispose(); } catch { }
        }
        AppLog.Info($"channel window closed [{_channel?.Name}]");
    }
}
