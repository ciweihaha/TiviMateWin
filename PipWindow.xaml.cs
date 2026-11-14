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
    private readonly DispatcherTimer _pipOpenCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private bool _pipOpenCheckArmed; // 开播检查是否待触发：只兜底一次，避免循环重播
    // 停滞看门狗（v1.4.15 2-6）：主窗口同款思路的简化版——播出中断流/黑屏后不再静默，
    // 4 秒一查，位置连续 2 次不前进则重播一次（最多 2 次），仍失败则显示文案等用户点重播。
    private readonly DispatcherTimer _pipStallTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private long _pipLastTime = -1;
    private int _pipStallTicks;
    private int _pipStallRetries;
    // 关闭中标志（与 ChannelWindow 的 F6(a) 同款）：Window_Closing 置位后 Play 不再启动新播放，
    // 避免主窗口异步续体在小窗关闭后继续对已释放的播放器 Play。
    private volatile bool _closing;
    private bool _teardownStarted; // 后台释放只启动一次：Window_Closing 里置位，避免重复 Dispose

    public PipWindow(LibVLC libvlc, int volume, int networkCache)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _networkCache = networkCache;
        _player = new MediaPlayer(_libvlc) { Volume = volume };
        VideoView.MediaPlayer = _player;
        _pipFsCheckTimer.Tick += (_, _) => OnPipFsCheckTick();
        _pipOpenCheckTimer.Tick += (_, _) => OnPipOpenCheckTick();
        _pipStallTimer.Tick += (_, _) => OnPipStallTick();
        // v1.4.15 2-6：播出失败有提示、可重播，不再静默黑屏；出画面后提示自动消失。
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(OnPipError);
        // v1.4.16：直播流播完（EndReached）自动重播——HLS 窗口耗尽/刷新失败时 libvlc 停在这里；
        // 点播文件播完是正常的，不重播。
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(OnPipEndReached);
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(HidePipStatus);
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
        UpdateFullscreenButton();
        _pipFsCheckTimer.Stop();
        _pipFsCheckTimer.Start();
    }

    /// <summary>全屏按钮图标跟随状态：正常时 □（点按全屏），全屏时 ▣（点按还原）。
    /// 双击视频区、F11、Esc 切全屏也走 TogglePipFullscreen，图标在这里统一同步。</summary>
    private void UpdateFullscreenButton()
    {
        try
        {
            BtnFullscreen.Content = _pipFullscreen ? "▣" : "□";
            BtnFullscreen.ToolTip = _pipFullscreen ? "退出全屏" : "全屏";
        }
        catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        // 最小化小窗：只改 WindowState，不碰播放器/视频面（播着的流继续播，
        // 只是窗口收起来）。恢复入口在主窗口：工具栏小窗按钮、右键“小窗播放”
        // 都会把已最小化的小窗恢复出来（见 MainWindow），小窗本身不进任务栏。
        WindowState = WindowState.Minimized;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => TogglePipFullscreen();

    private void OnPipFsCheckTick()
    {
        _pipFsCheckTimer.Stop();
        if (_closing) return;
        try
        {
            // v1.4.15 迭代 1：缓冲中 IsPlaying 为 false，慢启动流会被误判未出画面而多播一次。
            if (!_isPaused && !_stopped && !_player.IsPlaying && _player.State != VLCState.Buffering && _currentChannel != null)
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
            // 标题栏（高 30px）且不在右侧四个按钮区（4×28px）→ 按标题栏处理，系统接管拖动
            if (pt.Y >= 0 && pt.Y < 30 && pt.X >= 0 && pt.X < ActualWidth - 112)
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
        HidePipStatus(); // 新播放开始：清掉上次的失败文案
        _pipLastTime = -1;
        _pipStallTicks = 0;
        _pipStallRetries = 0;
        _pipStallTimer.Stop();
        _pipStallTimer.Start(); // 停滞看门狗开始计时
        StartPlayback(ch);
        // 开播 1 秒检查：仍未出画面时重挂视频面并重播一次兜底（见 OnPipOpenCheckTick）。
        _pipOpenCheckArmed = true;
        _pipOpenCheckTimer.Stop();
        _pipOpenCheckTimer.Start();
        AppLog.Info($"pip window play [{ch.Name}]");
    }

    /// <summary>真正启动一次播放（建 Media 并 Play）；开播检查的兜底重播也走这里，但不重设检查定时器。</summary>
    private void StartPlayback(ChannelItem ch)
    {
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
            // L2：成功日志放 try 内，播放抛错时不再先记 Error 紧接着又记 Info 成功。
            AppLog.Info($"pip window play [{ch.Name}]");
        }
        catch (Exception ex)
        {
            AppLog.Error($"pip window play failed [{ch.Name}]: {ex.Message}");
        }
    }

    /// <summary>开播 1 秒检查：小窗仍未出画面时重挂视频面并重播一次兜底（只兜底一次）。
    /// 覆盖“小窗一打开就黑屏”的偶发面挂失败；主窗口的 1 秒重挂检查同款思路。</summary>
    private void OnPipOpenCheckTick()
    {
        _pipOpenCheckTimer.Stop();
        if (_closing || !_pipOpenCheckArmed) return;
        _pipOpenCheckArmed = false; // 只兜底一次
        try
        {
            // v1.4.15 迭代 1：缓冲中 IsPlaying 为 false，慢启动流会被误判未出画面而多播一次。
            if (!_isPaused && !_stopped && !_player.IsPlaying && _player.State != VLCState.Buffering && _currentChannel != null)
            {
                VideoView.MediaPlayer = null;
                VideoView.MediaPlayer = _player;
                StartPlayback(_currentChannel);
                AppLog.Info("pip open check: video surface reattached + replayed");
            }
        }
        catch { }
    }

    /// <summary>播放失败：显示文案、停看门狗，等用户点重播（v1.4.15 2-6）。</summary>
    private void OnPipError()
    {
        if (_closing) return;
        _pipStallTimer.Stop();
        ShowPipStatus("播放失败，点击重播");
        AppLog.Error($"pip playback error [{_currentChannel?.Name}]");
    }

    /// <summary>直播流意外播完（v1.4.16）：自动重播同一频道（最多 2 次，与停滞看门狗共用计数），
    /// 仍失败则显示文案等用户点重播；点播文件播完不重播。</summary>
    private void OnPipEndReached()
    {
        if (_closing || _isPaused || _stopped || _currentChannel == null) return;
        if (!MainWindow.IsLiveStream(_currentChannel.Url)) return;
        if (_pipStallRetries < 2)
        {
            _pipStallRetries++;
            AppLog.Info($"pip stream ended, replay #{_pipStallRetries}: [{_currentChannel.Name}]");
            StartPlayback(_currentChannel);
        }
        else
        {
            ShowPipStatus("直播中断，点击重播");
            AppLog.Error($"pip stream ended after retries: [{_currentChannel.Name}]");
        }
    }

    private void HidePipStatus()
    {
        try { PipStatusText.Visibility = Visibility.Collapsed; } catch { }
    }

    private void ShowPipStatus(string text)
    {
        try
        {
            PipStatusText.Text = text;
            PipStatusText.Visibility = Visibility.Visible;
        }
        catch { }
    }

    /// <summary>失败文案点击重播（Handled=true，不触发根网格的拖动）。</summary>
    private void PipStatusText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_closing || _currentChannel == null) return;
        HidePipStatus();
        Play(_currentChannel);
    }

    /// <summary>停滞看门狗：IsPlaying 但位置连续 2 次（8 秒）不前进则重播一次（最多 2 次），
    /// 仍失败则显示文案等用户点重播；暂停/停止/未播/部分 Time 恒 0 的流不监控。</summary>
    private void OnPipStallTick()
    {
        if (_closing || _isPaused || _stopped || _currentChannel == null)
        {
            _pipStallTicks = 0;
            _pipLastTime = -1;
            return;
        }
        try
        {
            // v1.4.16 意外停播：停在 Stopped/Ended/Error 态（非缓冲中）且是直播流 → 重播
            // （计入 2 次上限），否则播完/停住是死角，只能手动点重播。
            // Opening/Buffering 态不算停，不动手，避开启动瞬间。
            if (!_player.IsPlaying && (_player.State == VLCState.Stopped || _player.State == VLCState.Ended || _player.State == VLCState.Error)
                && MainWindow.IsLiveStream(_currentChannel.Url))
            {
                if (_pipStallRetries < 2)
                {
                    _pipStallRetries++;
                    AppLog.Info($"pip unexpected stop, replay #{_pipStallRetries}: [{_currentChannel.Name}]");
                    StartPlayback(_currentChannel);
                }
                else
                {
                    _pipStallTimer.Stop();
                    ShowPipStatus("直播中断，点击重播");
                }
                _pipStallTicks = 0; _pipLastTime = -1;
                return;
            }
            if (!_player.IsPlaying) { _pipStallTicks = 0; _pipLastTime = -1; return; }
            var t = _player.Time;
            if (t <= 0) { _pipStallTicks = 0; _pipLastTime = -1; return; }
            if (t == _pipLastTime)
            {
                if (++_pipStallTicks >= 2)
                {
                    _pipStallTicks = 0;
                    if (_pipStallRetries < 2)
                    {
                        _pipStallRetries++;
                        AppLog.Info($"pip stall detected, replay #{_pipStallRetries}: [{_currentChannel.Name}]");
                        StartPlayback(_currentChannel);
                    }
                    else
                    {
                        _pipStallTimer.Stop();
                        ShowPipStatus("直播停滞，点击重播");
                        AppLog.Error($"pip stall persists after retries: [{_currentChannel.Name}]");
                    }
                }
            }
            else
            {
                _pipStallTicks = 0;
                if (_pipStallRetries > 0) _pipStallRetries = 0; // 恢复后计数清零
            }
            _pipLastTime = t;
        }
        catch { }
    }

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
        // 两个检查定时器都停掉：否则 DispatcherTimer 会一直持有窗口引用（泄漏），
        // 且关闭后触发的 Tick 还会去碰已释放的播放器/视频面。
        try { _pipOpenCheckTimer.Stop(); } catch { }
        try { _pipFsCheckTimer.Stop(); } catch { }
        try { _pipStallTimer.Stop(); } catch { } // v1.4.15 2-6：停滞看门狗也停掉
        // 先在 UI 线程断开视频面（快操作，只改 HWND 挂接），再在后台释放播放器。
        try { VideoView.MediaPlayer = null; } catch { }
        StartTeardown();
    }

    /// <summary>
    /// 后台释放播放器：Stop + Dispose 全部在后台线程做，UI 线程绝不碰播放器释放。
    /// 根因（v1.4.13 关小窗卡死）：MediaPlayer.Dispose() → libvlc_media_player_release()
    /// → 销毁时 input_Stop + input_Close（join 输入线程），要等卡住的直播流拆掉，
    /// 和同步 Stop() 是同一处等待；v1.4.13 只把 Stop 移到了后台，Window_Closed 里
    /// 的 Dispose 仍在 UI 线程同步执行，Close() 在 Window_Closed 里就堵死了——
    /// 底层鼠标钩子也跑在 UI 线程上，所以整机鼠标都像死机。另：后台 Stop 和 UI 线程
    /// Dispose 还会在同一个 native 播放器对象上竞态。上游 VLC 3.0.12 的说明也明确讲：
    /// release 会等析构线程结束，UI 销毁时不应直接 release（应放后台或程序退出时才释放）。
    /// </summary>
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
            AppLog.Info("pip player released on background thread");
        });
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        // 播放器释放已交给后台 StartTeardown：UI 线程在这里 Dispose 会阻塞
        // （等卡住的流拆掉），就是 v1.4.13 关小窗卡死的根因。
        AppLog.Info("pip window closed");
    }
}