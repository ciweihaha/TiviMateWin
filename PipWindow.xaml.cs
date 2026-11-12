using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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
    private bool _pipFullscreen;      // 小窗是否全屏中（最大化）
    private Rect _pipPreBounds;       // 小窗全屏前的位置尺寸：退出时原样恢复
    private ChannelItem? _currentChannel; // 小窗当前频道：全屏切回来画面丢了时重播用
    private readonly DispatcherTimer _pipFsCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
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
        _pipFsCheckTimer.Tick += (_, _) => OnPipFsCheckTick();
        // 标题栏拖动走 Win32 层：Loaded 后挂 WndProc，WM_NCHITTEST 对标题栏区返回 HTCAPTION，
        // 系统接管拖动，不走 WPF 鼠标事件（标题栏点按偶尔到不了 WPF 处理器时仍能拖动）。
        Loaded += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(PipWndProc);
        };
    }

    /// <summary>小窗全屏切换：只改 WindowState（不改 WindowStyle），视频 HWND 不重建，
    /// 画面应直接跟随；300ms 后检查，若播放器未在播则重挂视频面并重播当前频道兜底。</summary>
    public bool IsPipFullscreen => _pipFullscreen;

    public void TogglePipFullscreen()
    {
        if (_closing) return;
        if (!_pipFullscreen)
        {
            _pipPreBounds = new Rect(Left, Top, Width, Height);
            WindowState = WindowState.Maximized;
            _pipFullscreen = true;
        }
        else
        {
            WindowState = WindowState.Normal;
            Left = _pipPreBounds.Left;
            Top = _pipPreBounds.Top;
            Width = _pipPreBounds.Width;
            Height = _pipPreBounds.Height;
            _pipFullscreen = false;
        }
        _pipFsCheckTimer.Stop();
        _pipFsCheckTimer.Start();
    }

    private void OnPipFsCheckTick()
    {
        _pipFsCheckTimer.Stop();
        if (_closing) return;
        try
        {
            if (!_isPaused && !_stopped && !_player.IsPlaying && _currentChannel != null)
            {
                VideoView.MediaPlayer = null;
                VideoView.MediaPlayer = _player;
                Play(_currentChannel);
                AppLog.Info("pip fullscreen: video surface reattached + replayed");
            }
        }
        catch { }
    }

    private IntPtr PipWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg == WM_NCHITTEST)
        {
            var x = (short)(lParam.ToInt32() & 0xFFFF);
            var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
            var pt = PointFromScreen(new Point(x, y));
            // 标题栏（高 30px）且不在右侧两个按钮区（2×28px）→ 按标题栏处理，系统接管拖动
            if (pt.Y >= 0 && pt.Y < 30 && pt.X >= 0 && pt.X < ActualWidth - 56)
            {
                handled = true;
                return (IntPtr)HTCAPTION;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>播放指定频道</summary>
    public void Play(ChannelItem ch)
    {
        if (_closing) return;   // 关闭中不再启动新播放
        _currentChannel = ch;
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