using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LibVLCSharp.Shared;
using TiviMateWin.Models;
using TiviMateWin.Services;

namespace TiviMateWin;

/// <summary>独立悬浮小窗（画中画）：无边框窗口，可拖到屏幕任意位置，浮在其他窗口之上</summary>
public partial class PipWindow : Window
{
    private readonly LibVLC _libvlc;
    private readonly MediaPlayer _player;
    private Media? _media;   // 当前 Media：Play 后需保持存活直到换台/关闭
    private readonly int _networkCache;   // 深查第 22 轮：旧代码写死 800，用户设置的网络缓存不生效
    private bool _isPaused;
    private bool _stopped;   // 停止后：再次按播放应重播当前媒体，而不是先走一次无意义的 Pause
    // 关闭中标志（与 ChannelWindow 的 F6(a) 同款）：Window_Closing 置位后 Play 不再启动新播放，
    // 避免主窗口异步续体在小窗关闭后继续对已释放的播放器 Play。
    private volatile bool _closing;

    public PipWindow(LibVLC libvlc, int volume, int networkCache)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _networkCache = networkCache;
        _player = new MediaPlayer(_libvlc) { Volume = volume };
        VideoView.MediaPlayer = _player;
    }

    /// <summary>播放指定频道</summary>
    public void Play(ChannelItem ch)
    {
        if (_closing) return;   // 关闭中不再启动新播放
        TitleText.Text = ch.Name;
        _isPaused = false;
        _stopped = false;
        BtnPlayPause.Content = "⏸";
        try
        {
            var media = ch.Url.Contains("://")
                ? new Media(_libvlc, new Uri(ch.Url))
                : new Media(_libvlc, ch.Url, FromType.FromPath);
            media.AddOption($":network-caching={_networkCache}");
            _player.Play(media);
            var old = _media;
            _media = media;   // 新 Media 接管
            old?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error($"pip window play failed [{ch.Name}]: {ex.Message}");
        }
        AppLog.Info($"pip window play [{ch.Name}]");
    }

    /// <summary>标记为已由主窗口统一释放引擎（主窗口关闭时调用，避免重复释放）</summary>
    public void DetachEngine() => _engineReleased = true;

    private bool _engineReleased;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    /// <summary>小窗播放/暂停切换</summary>
    public void TogglePlayPause()
    {
        if (_closing) return;
        try
        {
            if (_stopped)
            {
                // 停止后 Media 仍挂在播放器上，Play() 从头重播当前媒体；
                // 旧代码走 Pause 分支，第一次点击只翻图标、播不出来。
                _player.Play();
                _stopped = false;
                _isPaused = false;
                BtnPlayPause.Content = "⏸";
            }
            else if (_isPaused)
            {
                _player.Play();
                _isPaused = false;
                BtnPlayPause.Content = "⏸";
            }
            else
            {
                _player.Pause();
                _isPaused = true;
                BtnPlayPause.Content = "▶";
            }
        }
        catch { }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    /// <summary>小窗停止播放</summary>
    public void Stop()
    {
        if (_closing) return;
        try
        {
            _player.Stop();
            _isPaused = false;
            _stopped = true;
            BtnPlayPause.Content = "⏸";
        }
        catch { }
    }

    // 无边框窗口拖动：Win32 方式（ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION），
    // 由系统接管移动，不依赖 WPF 鼠标捕获——窗口未激活时 CaptureMouse 可能失败，
    // 导致标题栏拖不动。按钮上的按下不拖动（Click 正常走按钮）。
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.OriginalSource is System.Windows.Controls.Button) return; // 点按钮不拖动
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            e.Handled = true;
            AppLog.Info("pip drag start");
        }
        catch (Exception ex)
        {
            AppLog.Error($"pip drag failed: {ex.Message}");
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // 先置位再 Stop：关闭后到达的 Play 直接返回，不再操作已释放的播放器。
        _closing = true;
        try { _player.Stop(); } catch { }
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        if (!_engineReleased)
        {
            try { _media?.Dispose(); } catch { }
            try { _player.Dispose(); } catch { }
        }
        AppLog.Info("pip window closed");
    }
}