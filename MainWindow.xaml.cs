using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using TiviMateWin.Dialogs;
using TiviMateWin.Models;
using TiviMateWin.Services;

namespace TiviMateWin;

/// <summary>
/// 主窗口：频道列表 + VLC 播放。
/// 性能要点：引擎后台初始化、缓存秒开、搜索防抖、列表虚拟化、硬件解码。
/// </summary>
public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private List<ChannelItem> _all = new();
    // P3：归一化频道名 → 同名频道列表（AdoptChannels 建好），FindBackupChannel 查表，
    // 不再每次播放出错时对全列表做 4 次 Replace+Trim。
    private Dictionary<string, List<ChannelItem>> _backupByName = new();
    // 频道列表视图：用 ICollectionView.Filter 过滤/排序，避免每次搜索/切分组全量重建列表
    private ICollectionView _view = CollectionViewSource.GetDefaultView(new List<ChannelItem>());
    // P1：过滤后列表缓存——ApplyFilter 一次枚举建出，翻台/回车直接取用，
    // 不再每按键重枚举 ICollectionView。
    private List<ChannelItem> _filtered = new();

    private LibVLC? _libvlc;
    private MediaPlayer? _player;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private volatile bool _engineReady;
    private volatile bool _engineFailed;   // 引擎初始化失败标志：避免反复弹窗
    // 主播放器当前 Media：Play 后需保持存活直到换台/关闭。
    // 用锁保护：快速换台时多个 Task.Run 并发访问，避免竞态释放正在播放的 Media
    private Media? _currentMedia;
    private readonly object _mediaLock = new();

    // gzip/deflate 自动解压：大清单（如 697KB 全球源）压缩后降 ~70%，下载时间从 35s 级降到 10s 级
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        MaxConnectionsPerServer = 8,   // 连接复用：多源/多窗口并发拉取不互相拖慢
        UseCookies = false
    })
    { Timeout = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _suppressEvents;
    private bool _suppressSrcList; // 源列表防回写标志
    private bool _favOnly;
    private ChannelItem? _current;
    private bool _fullscreen;
    private WindowState _preFullscreenState = WindowState.Normal; // 进全屏前的窗口状态：退出时原样恢复，不再写死 Normal
    private Rect _preFullscreenBounds;                            // 进全屏前的普通窗口位置尺寸（最大化时取 RestoreBounds）
    private bool _fsSideCollapsed; // 进全屏前侧栏是否已收起（小窗收起或用户手动收起）：退出时保持收起，不恢复宽度
    private readonly DispatcherTimer _reattachCheckTimer = new() { Interval = TimeSpan.FromSeconds(1) }; // 重挂视频面后 1 秒检查：仍未恢复播放则重播当前频道兜底

    // 视频区双击切换全屏：VideoView 是 LibVLC 原生 HWND，WPF 收不到它的鼠标事件，
    // 在线程消息层按两次左键按下自己识别双击（不依赖窗口类有没有 CS_DBLCLKS）。
    private DateTime _lastVideoClickUtc = DateTime.MinValue;  // 上一次落在视频区的左键按下
    private Point _lastVideoClickPoint;                        // 上一次按下的屏幕坐标
    // 同一次物理按下去重（v1.4.15 2-1）：钩子与线程消息层都会看到同一按下的消息，
    // 视频面不存在时（未开播/停播）两路各记一次会误算双击；50ms 内同点按下只计一次。
    private DateTime _lastClickDedupeUtc = DateTime.MinValue;
    private Point _lastClickDedupePoint;
    private DateTime _lastFullscreenToggleUtc = DateTime.MinValue; // 上一次切换全屏：300ms 内忽略按下，防止双击“退出全屏”按钮后第二下又切回来

    // 线程消息层双击识别用的 Win32 API（本仓第一处 P/Invoke）
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // ---------- 底层鼠标钩子（WH_MOUSE_LL） ----------
    // 根因：libvlc 视频窗口由 vout 线程创建/拥有，其鼠标消息进的是 vout 线程的消息队列，
    // 根本到不了 UI 线程的 ComponentDispatcher.ThreadFilterMessage——v1.4.8 的双击识别
    // 因此一个按下都收不到（CI 在 Linux 上只能验构建，无法验真机行为）。
    // WH_MOUSE_LL 在原始输入层看鼠标，不依赖视频窗口归属与生命周期；钩子只观察、永不吞消息。
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int VK_LBUTTON = 0x01;
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelMouseProc? _mouseHookProc; // 必须保存在字段里，防 GC 回收导致钩子崩溃
    private IntPtr _mouseHook = IntPtr.Zero;
    private DateTime _lastPipClickUtc = DateTime.MinValue; // 小窗视频区双击配对
    private Point _lastPipClickPoint;
    private Point? _pipVideoDownPoint; // 小窗视频区按下点：拖动候选
    private bool _pipDragging;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private bool _sideCollapsed;   // 侧栏是否收起（缩进最左）
    private GridLength _sideWidth = new(300);

    // 换台防抖：连按↑↓只播最后一个频道（150ms 窗口内）
    private readonly DispatcherTimer _switchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private ChannelItem? _pendingPlay;
    // 全屏 OSD：换台/状态变化时状态栏临时显示 3 秒
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    // 全屏控制浮层：鼠标移动显示，静止 3 秒自动隐藏
    private readonly DispatcherTimer _fsHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    // 坏源容错：连续失败计数 + 本轮已失败线路（防主备来回死循环）
    private int _failCount;
    private readonly HashSet<string> _recentFailedUrls = new();
    // 同一次失败去重（v1.4.15 迭代 1）：libvlc 对同一次失败可能多次触发 EncounteredError，
    // 800ms 内同一 URL 只处理一次，避免 _failCount 连加、备用切换与自动跳台并行触发。
    private DateTime _lastPlayErrorUtc = DateTime.MinValue;
    private string? _lastPlayErrorUrl;
    // 直播停滞看门狗：IsPlaying 但 8 秒播放位置不前进 → 自动重连（上限 3 次）
    private readonly DispatcherTimer _stallWatch = new() { Interval = TimeSpan.FromSeconds(4) };
    private long _lastMediaTime = -1;
    private int _stallTicks;
    private int _stallRetryCount;
    private DateTime _lastStallAt = DateTime.MinValue;
    // 意外停播自动重连（v1.4.16）：直播 HLS 的 playlist 刷新可能失败（SCF 在 200/302 间切换、
    // 偶发相对分片地址），libvlc 播完当前窗口就停（EndReached）或停在 Stopped 态；
    // 看门狗原来只在 IsPlaying 时才动，停播后是死角，必须手动重进。
    // _stopTicks：意外停播持续 tick 数，连续 2 次（8 秒）仍停才重连，避开启动/切台瞬间。
    // _autoReconnectCount：连续自动重连次数，Playing 事件里清零（播起来了就不算失败）；
    // 连续 5 次都没播起来就停手等用户，避免死流无限循环重播。
    private int _stopTicks;
    private int _autoReconnectCount;
    private bool _closing;

    // 独立悬浮小窗（画中画）：无边框窗口，可拖到屏幕任意位置
    private PipWindow? _pipWindow;
    private ChannelItem? _pipChannel;
    // 小窗打开序号（与 _loadSeq/_epgSeq 同款）：只采纳最新一次 OpenPip 的续体；
    // OpenPip 是 async void，await 引擎期间用户可能已关小窗、甚至已关主窗口，
    // 旧续体若继续执行，会在已释放的引擎上建小窗（崩溃）或把已关掉的小窗复活。
    private int _pipSeq;
    // 深查第 12 轮：小窗是否收起了侧栏——仅它收起的才由它恢复；用户手动收起时
    // OpenPip 不动侧栏，关小窗也不得把 _sideWidth 恢复出来（否则侧栏自动弹开）。
    private bool _pipCollapsedSidebar;

    // 小窗播放时主窗口的效果（用户要的效果）：主窗口变黑并最小化，小窗浮在桌面上。
    // 1.4.7 的重挂/重播会把主窗口画面拉回来，与该效果冲突，故小窗开着时跳过重挂/重播。
    private WindowState _prePipState = WindowState.Normal; // 开小窗前的主窗口状态：关小窗时还原
    private bool _pipEffectActive;                          // 小窗效果是否生效中（主窗口黑+最小化）

    // 多窗口播放：跟踪所有打开的独立频道窗口，主窗口关闭时统一关闭，避免引擎释放后子窗口崩溃
    private readonly List<ChannelWindow> _channelWindows = new();

    // 当前列表所属源地址：切源时据此判断缓存是否可用、失败时列表是否已错源。
    // 深查第 21 轮：旧代码只在 _all 为空时读缓存，切源后网络失败仍显示旧源频道。
    private string? _loadedSourceUrl;

    /// <summary>是否需要先上缓存：列表为空，或当前列表属于另一源（切源时）。
    /// 同源刷新不走缓存，避免缓存回滚刚刷新的列表。</summary>
    internal static bool NeedsCacheFirst(int loadedCount, string? loadedSourceUrl, string srcUrl)
        => loadedCount == 0 || loadedSourceUrl != srcUrl;

    /// <summary>是否直播流（v1.4.16）：HLS（.m3u8）地址即直播；本地文件/点播文件播完是正常的，
    /// 不自动重连。查询参数与大小写不影响判定。</summary>
    internal static bool IsLiveStream(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        var q = u.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) u = u[..q];
        return u.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>出错恢复决策（v1.4.16，纯逻辑，自测覆盖）：连续失败超 5 次停手；有备用先切备用；
    /// 无备用时前 3 次重连同一频道（直播闪断常见，跳台是错误体验），再失败才跳下一个频道。</summary>
    internal enum RecoveryAction { RetrySame, UseBackup, SkipNext, Stop }
    internal static RecoveryAction DecideRecovery(int failCount, bool hasBackup)
    {
        if (failCount > 5) return RecoveryAction.Stop;
        if (hasBackup) return RecoveryAction.UseBackup;
        return failCount <= 3 ? RecoveryAction.RetrySame : RecoveryAction.SkipNext;
    }

    // 播放列表加载并发保护：快速切换源/刷新时避免并发覆盖 _all 与 UI 竞态
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _loadSeq;   // 加载序号：只采纳最后一次请求的结果，防止旧结果覆盖新结果

    // 网络请求取消：窗口关闭时取消挂起的播放列表/EPG/Xtream 拉取，避免后台线程继续占用
    private readonly CancellationTokenSource _netCts = new();

    // 播放串行化：libVLC MediaPlayer 非线程安全，快速换台时多个 Play 需串行执行，避免并发崩溃
    private readonly SemaphoreSlim _playGate = new(1, 1);

    // 播放代号（v1.4.15）：每次 PlayAsync 启动新播放时递增；后台 Stop 执行前校验代号，
    // 避免迟到的 Stop 停掉新起的流（2-2 停止竞态、2-3 小窗竞态、2-4 切源竞态同款）。
    private int _playSeq;
    // 主播放器释放锁：后台 Stop（停止/切源/小窗效果）与退出时后台释放串行化，
    // 避免 Stop 与 Dispose 在同一个 native 播放器上竞态；UI 线程永不拿这把锁。
    private readonly object _mainPlayerLock = new();
    private bool _mainTeardownStarted; // 主播放器后台释放只启动一次

    // 播放控制状态：暂停标志 + 进度条拖动标志 + 进度刷新定时器
    private bool _isPaused;
    private bool _seekDragging;
    private bool _seekPending;   // 拖动结束后待跳转
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _aspectRatio = "";   // 画面比例（空=原始，16:9/4:3/拉伸）
    private float _playbackRate = 1.0f; // 倍速
    private string _sortMode = "号码";  // 频道排序：号码/名称

    // EPG 电子节目指南：频道id → 节目列表
    private Dictionary<string, List<EpgProgram>> _epg = new();
    private string? _epgUrl;
    private int _epgSeq;   // F7：EPG 加载序号：只采纳最后一次请求的结果，防止旧结果覆盖新结果
    private string? _epgLoadingUrl;   // M3：正在加载的 EPG 地址（与 _epgUrl 分离：失败时 _epgUrl 不动，下次刷新可重试；加载中不重复发起）
    // EPG 频道名 → 节目列表索引：GetPrograms 的名称回退查索引，不再线性扫整个 EPG 字典；与 _epg 同步重建。
    private Dictionary<string, List<EpgProgram>> _epgByName = new(StringComparer.OrdinalIgnoreCase);

    // 数字键换台（V1.4）：输入缓冲 + 超时确认；匹配规则在 DigitTuner（纯逻辑可自测）
    private string _digitBuf = "";
    private readonly DispatcherTimer _digitTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    // 换台 OSD（V1.4）：右上角频道号+名称+当前节目卡片，3 秒自动隐藏
    private readonly DispatcherTimer _osdHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    // OSD 台标异步加载序号：换台很快时旧图请求回来不再覆盖新频道
    private int _osdLogoSeq;
    // EPG 时效刷新（V1.4）：每分钟刷新本地正在播放显示，避免长时间停留时节目过期
    private readonly DispatcherTimer _epgRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    // 当前处于播放态的列表项引用（列表高亮用；AdoptChannels 整体替换列表后按 Url 恢复）
    private ChannelItem? _playingItem;

    // ---------- 节目表（时间轴）视图（UI 改版） ----------
    // 窗口：现在前 30 分钟起，共 4 小时；每分钟 3px，行高 48px，行数上限 200。
    private const double GuidePxPerMinute = 3.0;
    private const double GuideRowHeight = 48;
    private const double GuideRulerHeight = 28;
    private const int GuideMaxRows = 200;
    private DateTime _guideWindowStart;
    private List<ChannelItem> _guideChannels = new();
    private Border? _guideSelectedBlock;   // 当前选中的节目块（详情区对应显示）
    private ChannelItem? _guideSelectedChannel;
    private EpgProgram? _guideSelectedProgram;

    public MainWindow()
    {
        InitializeComponent();
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            // v1.4.15 迭代 1：配置写盘（DPAPI 加密 + 原子换名）放后台线程，避免慢盘/
            // 杀毒软件扫描时卡 UI；读盘失败待确认时仍走 UI 线程（Save 里要弹确认框）。
            // SaveGate 已串行化并发保存，后台写失败只记日志、下次再存，不影响运行。
            if (AppConfig.ReadFailurePending) _config.Save();
            else _ = Task.Run(() => _config.Save());
        };
        _switchDebounce.Tick += (_, _) =>
        {
            _switchDebounce.Stop();
            if (_closing) return;
            if (_pendingPlay is not { } ch) return;
            _pendingPlay = null;
            // 深查第 21 轮：切源/刷新期间列表已换，按 Url 在新列表中找对应频道；
            // 找不到说明该频道已不在当前源，不再播放旧对象。
            var fresh = _all.FirstOrDefault(c => c.Url == ch.Url);
            if (fresh is null) return;
            _ = PlayAsync(fresh);
        };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); if (_fullscreen) StatusBar.Visibility = Visibility.Collapsed; };
        _fsHideTimer.Tick += (_, _) => { _fsHideTimer.Stop(); FullscreenOverlay.Visibility = Visibility.Collapsed; };
        _stallWatch.Tick += OnStallWatchTick;
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _digitTimer.Tick += (_, _) => CommitDigits();
        _osdHideTimer.Tick += (_, _) => { _osdHideTimer.Stop(); ChannelOsd.Visibility = Visibility.Collapsed; };
        _epgRefreshTimer.Tick += (_, _) => OnEpgRefreshTick();
        _reattachCheckTimer.Tick += (_, _) => { _reattachCheckTimer.Stop(); OnReattachCheckTick(); };

        // 焦点守护：窗口激活/VLC原生窗口拖走焦点时，把键盘焦点还给频道列表，保证↑↓换台始终可用
        Activated += OnWindowActivated;

        // 线程消息层拦截方向键：无论焦点在 WPF 还是 VLC 原生窗口（HwndHost），↑↓换台都生效
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;

        InstallMouseHook(); // 底层鼠标钩子：视频区双击/小窗视频区拖动（见 OnLowLevelMouse）
    }

    private void OnWindowActivated(object? sender, EventArgs e) => ReturnFocusToList();

    private void ReturnFocusToList()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is TextBox) return; // 搜索框输入中不接管
            ChannelList.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>线程消息层拦截全局快捷键：焦点被 VLC 原生窗口夺走时换台/全屏/音量等仍可用</summary>
    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        if (handled) return;
        // 视频区双击切换全屏：VideoView 是原生 HWND，WPF 收不到它的鼠标事件，
        // 在此按两次左键按下识别双击。单击、拖动不受影响（只在第二次按下时切换）。
        if (msg.message == WM_LBUTTONDOWN)
        {
            if (TryHandleVideoDoubleClick(msg.hwnd))
            {
                ToggleFullscreen();
                handled = true;
            }
            return;
        }
        if (msg.message != WM_KEYDOWN) return;
        // 模态对话框打开时不拦截：对话框均为 ShowDialog，打开时它就是活动窗口；
        // 否则对话框内的方向键/空格等会同时触发主窗口换台/暂停/全屏（对话框焦点在
        // 按钮等非 TextBox 控件上时，现有的 FocusedElement 守卫拦不住）。
        foreach (Window w in OwnedWindows)
        {
            if (w.IsActive) return;
        }
        // 注：不检查 IsActive——焦点/激活可能落在 VLC 顶层原生窗口上，主窗口 IsActive 为 false；
        // ThreadFilterMessage 只会收到本线程窗口的消息，天然不会拦截其他应用的按键
        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        if (HandleGlobalKey(key)) handled = true;
    }

    /// <summary>视频区双击切换全屏：全屏时双击退出，窗口化时双击进入（与 F11 一致）。
    /// 只认落到主窗口（含 VLC 原生视频子窗口）且落在视频区内的按下；小窗/独立频道窗口/
    /// 对话框、侧栏/状态栏/全屏浮层上的按下不算。返回 true 表示本次按下构成双击，调用方负责切换。</summary>
    private bool TryHandleVideoDoubleClick(IntPtr hwnd)
    {
        if (_closing || !IsLoaded) return false;
        // 刚切过全屏 300ms 内忽略：双击“退出全屏”按钮时，第一下退出全屏，第二下不能再切回来
        if ((DateTime.UtcNow - _lastFullscreenToggleUtc).TotalMilliseconds < 300) return false;
        // 模态对话框打开时不拦截
        foreach (Window w in OwnedWindows)
        {
            if (w.IsActive) return false;
        }
        // 只处理主窗口（含 VLC 原生视频子窗口）的消息：小窗/独立频道窗口是顶层窗口（Owner 不是父子关系），不在此列
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null) return false;
        var mainHwnd = source.Handle;
        if (hwnd != mainHwnd && !IsChild(mainHwnd, hwnd)) return false;
        // 只认落在视频区内的按下：用光标屏幕坐标比对 VideoView 的屏幕矩形；
        // 全屏浮层、换台 OSD 盖在视频区上时，它们那块不算视频区
        if (!GetCursorPos(out var pt)) return false;
        return TryHandleVideoDoubleClickAt(new Point(pt.X, pt.Y));
    }

    /// <summary>视频区双击配对（供线程消息层与底层鼠标钩子共用）：两次按下都在双击时间与
    /// 矩形内即为双击。注意：只做配对判断，不切换全屏，切换由调用方做（钩子侧用 BeginInvoke
    /// 延迟切，避免与线程消息层的同步切换重复触发）。同一次物理按下只计一次（50ms 去重，
    /// v1.4.15 2-1）：钩子与消息层都会看到同一按下，视频面不存在时两路各记一次会误算双击。</summary>
    private bool TryHandleVideoDoubleClickAt(Point point)
    {
        var now = DateTime.UtcNow;
        // 同一次物理按下只计一次：钩子与线程消息层先后看到同一按下时，第二路直接返回，
        // 否则未开播时单击会被两路各记一次、误算成双击（2-1）。50ms 窗口远小于人手双击间隔。
        if ((now - _lastClickDedupeUtc).TotalMilliseconds <= 50
            && Math.Abs(point.X - _lastClickDedupePoint.X) <= 4
            && Math.Abs(point.Y - _lastClickDedupePoint.Y) <= 4)
            return false;
        _lastClickDedupeUtc = now;
        _lastClickDedupePoint = point;
        if (!IsPointInMainVideo(point)) return false;
        if (IsPointOnMainOverlayOrOsd(point)) return false;
        var doubleClickTime = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        var dx = Math.Abs(point.X - _lastVideoClickPoint.X);
        var dy = Math.Abs(point.Y - _lastVideoClickPoint.Y);
        var doubleClickWidth = GetSystemMetrics(36);  // SM_CXDOUBLECLK
        var doubleClickHeight = GetSystemMetrics(37); // SM_CYDOUBLECLK
        var isDouble = _lastVideoClickUtc != DateTime.MinValue
            && (now - _lastVideoClickUtc) <= doubleClickTime
            && dx <= doubleClickWidth / 2 + 2
            && dy <= doubleClickHeight / 2 + 2;
        _lastVideoClickUtc = now;
        _lastVideoClickPoint = point;
        if (!isDouble) return false;
        _lastVideoClickUtc = DateTime.MinValue; // 本次双击已消费，避免三连击又算一次
        return true;
    }

    private bool IsPointInMainVideo(Point screenPoint)
    {
        if (!IsLoaded || VideoView.ActualWidth <= 0 || VideoView.ActualHeight <= 0) return false;
        var videoTopLeft = VideoView.PointToScreen(new Point(0, 0));
        return new Rect(videoTopLeft, new Size(VideoView.ActualWidth, VideoView.ActualHeight)).Contains(screenPoint);
    }

    private bool IsPointOnMainOverlayOrOsd(Point screenPoint)
    {
        if (FullscreenOverlay.Visibility == Visibility.Visible && IsPointInElement(FullscreenOverlay, screenPoint)) return true;
        if (ChannelOsd.Visibility == Visibility.Visible && IsPointInElement(ChannelOsd, screenPoint)) return true;
        // UI 改版：节目表打开时它是盖在视频区上的浮层，其上的双击不应切全屏
        if (GuideView.Visibility == Visibility.Visible && IsPointInElement(GuideView, screenPoint)) return true;
        return false;
    }

    private static bool IsPointInElement(FrameworkElement element, Point screenPoint)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight)).Contains(screenPoint);
    }

    private void InstallMouseHook()
    {
        try
        {
            _mouseHookProc = OnLowLevelMouse;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero) AppLog.Error("install mouse hook failed");
        }
        catch (Exception ex) { AppLog.Error($"install mouse hook failed: {ex.Message}"); }
    }

    private void UninstallMouseHook()
    {
        try
        {
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            _mouseHookProc = null;
        }
        catch { }
    }

    /// <summary>底层鼠标钩子回调（UI 线程）：只观察、永不吞消息；重活一律 BeginInvoke/PostMessage，钩子内不阻塞。
    /// WM_MOUSEMOVE 只在小窗拖动候选期间才处理，平时直接放行，避免系统范围的鼠标移动都走 Win32 调用。</summary>
    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing && IsLoaded)
        {
            try
            {
                var msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK || msg == WM_LBUTTONUP
                    || (msg == WM_MOUSEMOVE && _pipVideoDownPoint != null && !_pipDragging))
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    HandleLowLevelMouse(msg, new Point(data.pt.X, data.pt.Y));
                }
            }
            catch { /* 钩子永不抛错 */ }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleLowLevelMouse(int msg, Point point)
    {
        if (msg == WM_LBUTTONUP) { _pipVideoDownPoint = null; _pipDragging = false; }
        var fg = GetForegroundWindow();
        var mainHwnd = new WindowInteropHelper(this).Handle;
        // 主窗口在前台：视频区双击切换主窗口全屏（线程消息层收不到视频窗口的消息，这里是主入口）
        if (fg == mainHwnd && mainHwnd != IntPtr.Zero)
        {
            if (msg == WM_LBUTTONDBLCLK)
            {
                // 系统已认定双击（视频窗口类带 CS_DBLCLKS 时第二下是 DBLCLK 而不是 DOWN）：直接处理
                _lastVideoClickUtc = DateTime.MinValue;
                if (IsPointInMainVideo(point) && !IsPointOnMainOverlayOrOsd(point))
                    RequestToggleFullscreen();
                return;
            }
            if (msg == WM_LBUTTONDOWN && TryHandleVideoDoubleClickAt(point))
                RequestToggleFullscreen();
            return;
        }
        // 小窗在前台：视频区双击切小窗全屏；按下后移动则拖动小窗
        if (_pipWindow == null) return;
        var pipHwnd = new WindowInteropHelper(_pipWindow).Handle;
        if (pipHwnd == IntPtr.Zero || fg != pipHwnd) return;
        if (msg == WM_LBUTTONDBLCLK)
        {
            _lastPipClickUtc = DateTime.MinValue;
            _pipVideoDownPoint = null;
            if (IsPointInPipVideo(point))
            {
                var pip = _pipWindow;
                Dispatcher.BeginInvoke(new Action(() => pip.TogglePipFullscreen()));
            }
            return;
        }
        if (msg == WM_LBUTTONDOWN)
        {
            if (!IsPointInPipVideo(point)) return;
            _pipVideoDownPoint = point;
            if (TryHandlePipDoubleClickAt(point))
            {
                var pip = _pipWindow;
                Dispatcher.BeginInvoke(new Action(() => pip.TogglePipFullscreen()));
            }
            return;
        }
        // WM_MOUSEMOVE：按下后移动超 4px 且小窗未全屏 → 发起系统拖动（PostMessage，不阻塞 vout 线程）
        if (msg == WM_MOUSEMOVE && _pipVideoDownPoint is { } down && !_pipDragging && !_pipWindow.IsPipFullscreen)
        {
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                && (Math.Abs(point.X - down.X) > 4 || Math.Abs(point.Y - down.Y) > 4))
            {
                _pipDragging = true;
                PostMessage(pipHwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
    }

    /// <summary>延迟切换主窗口全屏：钩子回调内不同步改窗口状态；300ms 防重入（与线程消息层共用时间戳）。</summary>
    private void RequestToggleFullscreen()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !IsLoaded) return;
            if ((DateTime.UtcNow - _lastFullscreenToggleUtc).TotalMilliseconds < 300) return;
            ToggleFullscreen();
        }));
    }

    private bool TryHandlePipDoubleClickAt(Point point)
    {
        if (!IsPointInPipVideo(point)) return false;
        var now = DateTime.UtcNow;
        var doubleClickTime = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        var dx = Math.Abs(point.X - _lastPipClickPoint.X);
        var dy = Math.Abs(point.Y - _lastPipClickPoint.Y);
        var doubleClickWidth = GetSystemMetrics(36);
        var doubleClickHeight = GetSystemMetrics(37);
        var isDouble = _lastPipClickUtc != DateTime.MinValue
            && (now - _lastPipClickUtc) <= doubleClickTime
            && dx <= doubleClickWidth / 2 + 2
            && dy <= doubleClickHeight / 2 + 2;
        _lastPipClickUtc = now;
        _lastPipClickPoint = point;
        if (!isDouble) return false;
        _lastPipClickUtc = DateTime.MinValue;
        return true;
    }

    /// <summary>点是否落在小窗视频区：小窗矩形去掉顶部 30px 标题栏。</summary>
    private bool IsPointInPipVideo(Point screenPoint)
    {
        var pip = _pipWindow;
        if (pip == null || !pip.IsVisible) return false;
        var rect = new Rect(pip.Left, pip.Top + 30, Math.Max(0, pip.Width), Math.Max(0, pip.Height - 30));
        return rect.Contains(screenPoint);
    }

    // ---------- 启动 ----------

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info($"start v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        _config = AppConfig.Load();
        ApplyTheme(_config.Theme);      // L11：启动时加载保存的主题（带缺失容错，见 ApplyTheme）
        RestoreWindowPlacement();   // V1.4：恢复上次窗口位置/尺寸（边界校验通过才应用）
        // F2：仅在 Window_Loaded 检查一次 LastLoadError（每次 Load() 后不弹）。
        // 三种语义严格区分，文案约束见 AppConfig.LastLoadError 注释。
        if (!string.IsNullOrEmpty(AppConfig.LastLoadError))
        {
            string msg;
            if (AppConfig.ReadFailurePending)
            {
                // 读盘失败：不得含“损坏”字样，也不得含保全路径。
                msg = "配置读取失败，原文件未动，请重试。本次以默认配置启动（仅内存态），下一次保存前将提示确认，避免覆盖原文件。";
            }
            else if (AppConfig.LastLoadError.Contains("config.corrupt-"))
            {
                // 损坏：给出保全文件名（仅文件名，不给完整路径）。
                var err = AppConfig.LastLoadError;
                var idx = err.IndexOf("config.corrupt-", StringComparison.Ordinal);
                var parts = err[idx..].Split(new[] { ' ', '"', '\'', '\r', '\n', '，', '。', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var fileName = parts.Length > 0 ? parts[0] : "config.corrupt-*.json";
                msg = $"配置文件已损坏，已保全为 {fileName}，本次以默认配置启动。";
            }
            else if (AppConfig.LastLoadError.Contains("无法解密") || AppConfig.LastLoadError.Contains("重新输入"))
            {
                // 解密失败：不得提“配置文件损坏”或保全路径；其余设置已正常加载。
                msg = "Xtream 登录凭据无法解密，已清空密码，请重新输入密码后保存；其余设置已正常加载。";
            }
            else
            {
                msg = AppConfig.LastLoadError;
            }
            MessageBox.Show(this, msg, "配置加载提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        VolumeText.Text = $"音量 {_config.Volume}%";
        _aspectRatio = _config.AspectRatio ?? "";
        _playbackRate = (float)(_config.PlaybackRate > 0 ? _config.PlaybackRate : 1.0);
        RefreshPlaylistCombo();
        _ = EnsureEngineAsync();          // 后台初始化，不阻塞界面
        _progressTimer.Start();           // 进度条/时间刷新
        _epgRefreshTimer.Start();         // EPG 每分钟刷新本地正在播放显示（V1.4）
        await LoadPlaylistAsync(false, _netCts.Token);   // F6(c)：初始加载改传 _netCts.Token，不再传 CancellationToken.None；缓存优先秒开，再后台刷新
        ChannelList.Focus();              // 启动焦点给频道列表（否则默认落在搜索框，方向键失效）
    }

    private async Task EnsureEngineAsync()
    {
        if (_engineReady || _engineFailed) return;   // 已就绪或已确认失败，不再重试
        try
        {
            await _engineGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;   // L10：关闭时信号量已释放，属正常关闭
        }
        try
        {
            if (_engineReady || _engineFailed) return;
            PlayState.Text = "引擎初始化…";
            _libvlc = await Task.Run(() =>
            {
                Core.Initialize();
                // no-osd 不显示标题水印；avcodec-hw 按用户设置启用/禁用硬件解码
                var args = new List<string> { "--no-osd" };
                args.Add(_config.HardwareDecode ? "--avcodec-hw=any" : "--avcodec-hw=disabled");
                return new LibVLC(args.ToArray());
            });
            _player = new MediaPlayer(_libvlc) { Volume = _config.Volume };
            _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _failCount = 0;              // 播放成功：重置失败链
                _autoReconnectCount = 0;     // v1.4.16：播起来了，自动重连计数清零
                _recentFailedUrls.Clear();   // 网络已恢复，允许重试已失败线路
                PlayState.Text = "播放中";
                ShowOsd();
            });
            _player.Buffering += (_, args) => Dispatcher.BeginInvoke(() =>
            {
                var txt = args.Cache < 100 ? $"缓冲中 {args.Cache:F0}%" : "播放中";
                if (PlayState.Text == txt) return;   // 高频事件去重，避免无效 UI 刷新
                PlayState.Text = txt;
                ShowOsd();
            });
            _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(HandlePlayError);
            // v1.4.16：直播流播完（EndReached）不是正常结束——HLS 窗口耗尽/playlist 刷新失败时
            // libvlc 会停在这里；点播文件播完才是正常的，由 OnPlayerEndReached 里的 IsLiveStream 区分。
            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(OnPlayerEndReached);
            VideoView.MediaPlayer = _player;
            _engineReady = true;
            _stallWatch.Start();
            AppLog.Info($"engine ready libVLC {_libvlc.Version}");
            if (PlayState.Text == "引擎初始化…") PlayState.Text = "就绪";
        }
        catch (Exception ex)
        {
            // L10：关闭中不弹框、不写 UI 状态（关闭时信号量释放会使等待中的调用以 ObjectDisposedException 到此）。
            if (_closing) return;
            _engineFailed = true;            // 标记失败，后续不再反复初始化/弹窗
            PlayState.Text = "引擎初始化失败";
            AppLog.Error("engine init failed: " + ex.Message);
            // v1.4.15 迭代 1：关闭期间不弹窗，否则弹窗会挡住关闭流程。
            if (!_closing)
                MessageBox.Show(this, "VLC 引擎初始化失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // L10：关闭时信号量可能已释放，Release 抛 ObjectDisposedException 属正常关闭。
        finally { try { _engineGate.Release(); } catch (ObjectDisposedException) { } }
    }

    // ---------- 频道数据 ----------

    private async Task LoadPlaylistAsync(bool forceRefresh, CancellationToken ct)
    {
        var seq = ++_loadSeq;   // 本次请求序号
        try
        {
            await _loadGate.WaitAsync(ct);
            try
            {
                // 若等待期间又有更新的加载请求，本次结果作废
                if (seq != _loadSeq) return;
                await LoadCoreAsync(forceRefresh, ct);
            }
            // L10：关闭时信号量可能已释放，Release 抛 ObjectDisposedException 属正常关闭。
            finally { try { _loadGate.Release(); } catch (ObjectDisposedException) { } }
        }
        catch (OperationCanceledException)
        {
            // F6(c)：取消属正常关闭/切换，不上抛到 async void 的 Window_Loaded。
        }
        catch (ObjectDisposedException)
        {
            // L10：关闭时信号量已释放，等待中的调用到此属正常关闭，不上抛。
        }
    }

    private async Task LoadCoreAsync(bool forceRefresh, CancellationToken ct)
    {
        // F6(c)：触 UI 前检查取消/关闭状态。
        if (_closing || ct.IsCancellationRequested) return;
        var src = _config.Playlists.FirstOrDefault(p => p.Name == _config.ActivePlaylist)
                  ?? _config.Playlists.FirstOrDefault();
        if (src is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            PlayState.Text = "未添加播放列表";
            return;
        }
        _config.ActivePlaylist = src.Name;

        // 1) 磁盘缓存：秒开
        // 深查第 21 轮：旧条件只在 _all 为空时读缓存，切源（forceRefresh）永远跳过，
        // 新源网络一失败就只能显示旧源的频道。改为列表为空或当前列表属于另一源时
        // 都先上缓存；同源刷新仍只走网络，避免缓存回滚刚刷新的列表。
        if (NeedsCacheFirst(_all.Count, _loadedSourceUrl, src.Url))
        {
            var cached = await Task.Run(() => ChannelCache.Load(src.Url));
            // F6(c)：缓存采纳（AdoptChannels 触 UI）前再检查取消/关闭。
            if (_closing || ct.IsCancellationRequested) return;
            if (cached.Count > 0)
            {
                AdoptChannels(cached);
                _loadedSourceUrl = src.Url;
                PlayState.Text = $"已载入 {cached.Count} 个频道（缓存）";
                await ResumeLastAsync();
            }
        }

        // 2) 网络刷新
        try
        {
            var text = await FetchTextAsync(src.Url, ct);
            var list = await Task.Run(() => M3uParser.Parse(text), ct);
            // F6(c)：网络结果采纳（AdoptChannels 触 UI）前再检查取消/关闭。
            if (_closing || ct.IsCancellationRequested) return;
            if (list.Count == 0 && text.Contains("#EXT-X-"))
            {
                // 单流地址（HLS 直播流）：自动包装为单频道，直接贴流地址也能看
                list.Add(new ChannelItem
                {
                    Name = GuessChannelName(src.Url),
                    Url = src.Url,
                    Group = "直播流"
                });
            }
            if (list.Count > 0)
            {
                AdoptChannels(list);
                _loadedSourceUrl = src.Url;
                _ = Task.Run(() => ChannelCache.Save(src.Url, list));   // 后台写盘，大清单不卡 UI
                PlayState.Text = $"已载入 {list.Count} 个频道";
                await ResumeLastAsync();
                // 尝试从 M3U 头部解析 EPG 源并加载节目单
                // M3：_epgUrl 只在 LoadEpgAsync 成功后才更新（失败时保持旧值，下次刷新可重试）；
                // _epgLoadingUrl 防止同一地址在加载中被重复发起。
                var epgUrl = ExtractEpgUrl(text);
                if (epgUrl != null && epgUrl != _epgUrl && epgUrl != _epgLoadingUrl)
                {
                    _epgLoadingUrl = epgUrl;
                    _ = LoadEpgAsync(epgUrl, ct);
                }
            }
            else if (_all.Count == 0)
            {
                PlayState.Text = "播放列表为空";
            }
        }
        catch (OperationCanceledException)
        {
            // F6(c)：取消透传给 LoadPlaylistAsync 统一静默处理，不写失败状态。
            throw;
        }
        catch (Exception ex)
        {
            if (_all.Count == 0) PlayState.Text = "加载失败: " + ex.Message;
            else if (_loadedSourceUrl != src.Url)
            {
                // 深查第 21 轮：切源后网络失败且新源无缓存——当前列表属于旧源，
                // 继续显示会误导用户以为是新源内容，清空并明确报错。
                AdoptChannels(new List<ChannelItem>());
                _loadedSourceUrl = null;
                PlayState.Text = "加载失败: " + ex.Message;
            }
            else PlayState.Text = "刷新失败，使用缓存";
        }
    }

    private void AdoptChannels(List<ChannelItem> list)
    {
        var favs = _config.Favorites.ToHashSet();
        foreach (var c in list) c.IsFavorite = favs.Contains(c.Url);

        var keepUrl = _current?.Url;
        var wasPlayingUrl = _playingItem?.Url;   // V1.4：旧列表中处于播放态的频道，新列表中恢复高亮
        _all = list;
        // P3：建归一化名 → 同名频道表（首个为准的顺序与原 FirstOrDefault 一致）
        _backupByName = new Dictionary<string, List<ChannelItem>>();
        foreach (var c in list)
        {
            var key = NormalizeName(c.Name);
            if (!_backupByName.TryGetValue(key, out var bucket))
            {
                bucket = new List<ChannelItem>();
                _backupByName[key] = bucket;
            }
            bucket.Add(c);
        }
        _view = CollectionViewSource.GetDefaultView(_all);   // 重新绑定视图（_all 被整体替换）
        ChannelList.ItemsSource = _view;
        RebuildGroups();
        ApplyFilter();
        EmptyState.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (keepUrl != null)
        {
            var again = _all.FirstOrDefault(c => c.Url == keepUrl);
            if (again != null)
            {
                _current = again;
                if (wasPlayingUrl == keepUrl)
                {
                    _playingItem = again;
                    again.IsPlaying = true;
                }
                SelectInList(again);
            }
        }
    }

    private async Task ResumeLastAsync()
    {
        if (_current != null) return;
        if (_config.ActiveChannelUrl is not string last) return;
        var ch = _all.FirstOrDefault(c => c.Url == last);
        if (ch == null) return;
        SelectInList(ch);
        await PlayAsync(ch);
    }

    // 响应体上限（深查第 13 轮）：播放列表/Xtream 走 20MB（实测全球源约 697KB，
    // 20MB 已是 28 倍余量）；EPG XMLTV 全量可达数十 MB，单独放宽到 200MB。
    // 上限防的是失控/恶意源把旧 GetStringAsync 的无界缓冲撑爆内存（Logo 下载同类
    // 缺口已在第 5 轮以流式限长堵上，本处是同一缺口在清单拉取侧的版本）。
    private const long MaxPlaylistBytes = 20L * 1024 * 1024;
    private const long MaxEpgBytes = 200L * 1024 * 1024;

    /// <summary>响应体超限：确定性失败，不重试（见 FetchTextAsync）。</summary>
    internal sealed class ResponseTooLargeException : Exception
    {
        public ResponseTooLargeException(long limit) : base($"响应体超过上限 {limit} 字节，已拒绝") { }
    }

    private Task<string> FetchTextAsync(string url, CancellationToken ct)
        => FetchTextAsync(url, ct, MaxPlaylistBytes);

    /// <summary>按调用方上限拉取文本：流式限长读体，超限抛 ResponseTooLargeException（不重试）。</summary>
    private async Task<string> FetchTextAsync(string url, CancellationToken ct, long maxBytes)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 网络抖动容错：最多重试 2 次（间隔 1s），仅对瞬时失败重试
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var resolved = AppConfig.ResolveXtreamUrl(url, _config.XtreamServer, _config.XtreamUsername, _config.XtreamPassword);
                    // ResponseHeadersRead + 自建 60 秒 CTS：HttpClient.Timeout 在收到响应头后
                    // 不再约束读体（LogoService 同款处理），慢速滴流的源否则可无限挂起。
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(60));
                    using var resp = await _http.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    return await ReadResponseTextCappedAsync(resp, maxBytes, cts.Token);
                }
                // 超限是确定性失败：重试只会再下载 20MB，不重试、不记失败日志，直接透传。
                catch (ResponseTooLargeException) { throw; }
                // 仅对瞬时失败重试；调用方取消（窗口关闭/切换源）不重试、不记失败日志，
                // 直接透传 OperationCanceledException。注意不能按异常类型排除：
                // HttpClient 超时抛的也是 TaskCanceledException（OperationCanceledException
                // 子类），但此时 ct 未取消，仍属可重试的瞬时失败，故按 ct 判定。
                catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested)
                {
                    AppLog.Error($"fetch retry {attempt + 1} [{AppConfig.RedactUrlForLog(url)}]: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }
        // 本地文件：先按长度拒收超限文件，再读盘（不存在的文件仍由 ReadAllTextAsync 抛错，行为不变）。
        if (File.Exists(url) && new FileInfo(url).Length > maxBytes) throw new ResponseTooLargeException(maxBytes);
        return await File.ReadAllTextAsync(url, ct);
    }

    /// <summary>流式限长读响应体并解码：超限在读满上限后即停读并抛 ResponseTooLargeException；
    /// 编码优先用响应头 CharSet（与旧 GetStringAsync 口径一致），缺失/非法时回落 UTF-8。</summary>
    internal static async Task<string> ReadResponseTextCappedAsync(HttpResponseMessage resp, long maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var bytes = await LogoService.ReadCappedAsync(stream, maxBytes, ct);
        if (bytes == null) throw new ResponseTooLargeException(maxBytes);
        var charset = resp.Content.Headers.ContentType?.CharSet;
        Encoding enc;
        try { enc = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch { enc = Encoding.UTF8; }
        return enc.GetString(bytes);
    }

    /// <summary>从地址推断频道名：取 URL/路径的最后一段文件名（如 cctv1.m3u8 → cctv1）</summary>
    private static string GuessChannelName(string url)
    {
        try
        {
            var path = url.Contains("://") ? new Uri(url).AbsolutePath : url;
            var name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? url : name;
        }
        catch { return url; }
    }

    private void RebuildGroups()
    {
        _suppressEvents = true;
        var prev = GroupCombo.SelectedItem as string;
        var groups = _all.Select(c => c.Group).Distinct()
                         .OrderBy(g => g, StringComparer.CurrentCulture).ToList();
        GroupCombo.Items.Clear();
        GroupCombo.Items.Add("全部");
        foreach (var g in groups) GroupCombo.Items.Add(g);
        GroupCombo.SelectedItem = prev != null && (prev == "全部" || groups.Contains(prev)) ? prev : "全部";
        _suppressEvents = false;
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var group = GroupCombo.SelectedItem as string;
        var hidden = _config.HiddenChannels.ToHashSet();
        var favOnly = _favOnly;

        // 过滤：隐藏频道 / 收藏 / 分组 / 搜索关键词
        _view.Filter = c =>
        {
            var ch = (ChannelItem)c;
            if (hidden.Contains(ch.Url)) return false;
            if (favOnly && !ch.IsFavorite) return false;
            if (group is string g && g != "全部" && ch.Group != g) return false;
            if (q.Length > 0 && !ch.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                            && !ch.Group.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        };

        // 排序：号码（默认）或名称
        using (var defer = _view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(
                _sortMode == "名称" ? "Name" : "Number", ListSortDirection.Ascending));
        }

        // P1：一次枚举同时建出过滤后列表与数量，供翻台/回车直接取用，
        // 不再每按键重枚举过滤视图（L7 的计数循环并入此处）。
        _filtered.Clear();
        foreach (ChannelItem item in _view) _filtered.Add(item);
        var filteredCount = _filtered.Count;
        CountText.Text = $"{filteredCount} 个频道";
        // V1.4：有频道但被过滤得一个不剩时，给出明确反馈而不是空白列表
        NoMatchText.Visibility = _all.Count > 0 && filteredCount == 0
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshChannelNowPrograms();   // UI 改版：过滤后列表的频道行当前节目 + 进度
        BuildGuide();                  // UI 改版：节目表打开时跟随分组/搜索同步重建（关闭时内部直接返回）
    }

    private void SelectInList(ChannelItem ch)
    {
        if (!_view.Contains(ch))
        {
            ch = _view.Cast<ChannelItem>().FirstOrDefault(c => c.Url == ch.Url) ?? ch;
        }
        ChannelList.SelectedItem = ch;
        ChannelList.ScrollIntoView(ch);
    }

    // ---------- 播放 ----------

    private Task PlayAsync(ChannelItem ch) => PlayAsync(ch, false);

    private async Task PlayAsync(ChannelItem ch, bool watchdogReconnect)
    {
        if (_closing) return;                 // 窗口关闭中，不再启动播放
        await EnsureEngineAsync();
        if (_player is null || _libvlc is null) return;

        _switchDebounce.Stop();               // 新播放请求使挂起的防抖播放失效
        _pendingPlay = null;
        if (!watchdogReconnect)              // 用户主动播放：看门狗计数与失败计数重新开始
        {
            _stallRetryCount = 0;
            _autoReconnectCount = 0;         // v1.4.16：手动换台/重进，自动重连计数重新开始
            _lastStallAt = DateTime.MinValue;
            _failCount = 0;                  // 用户手动换台：重置坏源失败链，避免永久停止自动跳台
            _recentFailedUrls.Clear();
        }
        _lastMediaTime = -1;                  // 停滞看门狗重新计位
        _stallTicks = 0;

        _current = ch;
        _playSeq++;   // v1.4.15：新播放提交，代号递增，作废此前挂起的后台 Stop（2-2/2-3/2-4）
        NowPlaying.Text = ch.Name;
        PlayState.Text = "连接中…";
        EmptyState.Visibility = Visibility.Collapsed;
        ShowOsd();
        ShowChannelOsd(ch);   // V1.4：换台 OSD（频道号+名称+当前节目）
        _isPaused = false;
        BtnPlayPause.Content = "⏸";
        FsPlayPause.Content = "⏸";
        SeekBar.Value = 0;
        TimeText.Text = "00:00";
        DurationText.Text = "--:--";
        ShowEpgForChannel(ch);

        var url = ch.Url;
        try
        {
            // 播放串行化：快速换台时避免并发调用 _player.Play（libVLC 非线程安全）
            await _playGate.WaitAsync();
            try
            {
                // v1.4.15 迭代 3：等锁期间可能已被停止（_current 已清）或被更新的换台取代
                // （_current 已指向另一 Url），此时不再启动本次播放——否则停止后流又自己起播。
                // 按 Url 比对：列表刷新后 _current 会换成同 Url 的新对象，引用比对会误判。
                if (_closing || _current?.Url != ch.Url) return;
                await Task.Run(() =>
                {
                    // 与后台 Stop 用同一把 _mainPlayerLock 串行化：锁内再校验一次，
                    // 堵住“检查通过后、Play 之前”被停止的微秒窗口。
                    lock (_mainPlayerLock)
                    {
                        if (_closing || _current?.Url != ch.Url) return;
                        // 创建新 Media 并播放，然后释放旧的（顺序：先播新的，再释放旧的，避免黑屏）
                        var media = url.Contains("://")
                            ? new Media(_libvlc!, new Uri(url))
                            : new Media(_libvlc!, url, FromType.FromPath);
                        media.AddOption($":network-caching={_config.NetworkCache}");
                        _player!.Play(media);
                        lock (_mediaLock)
                        {
                            var old = _currentMedia;
                            _currentMedia = media;   // 新 Media 接管播放
                            old?.Dispose();          // 旧 Media 已停止，安全释放
                        }
                    }
                });
            }
            // L10：关闭时信号量可能已释放，Release 抛 ObjectDisposedException 属正常关闭。
            finally { try { _playGate.Release(); } catch (ObjectDisposedException) { } }
        }
        catch (Exception ex)
        {
            // L10：关闭中不写 UI、不记日志（关闭时信号量释放会使等待中的调用以 ObjectDisposedException 到此）。
            if (_closing) return;
            PlayState.Text = "播放失败";
            AppLog.Error($"play failed [{ch.Name}] {AppConfig.RedactUrlForLog(url)}: {ex.Message}");
            return;                            // 失败不写断点、不抢焦点
        }
        // F6(a)：_playGate 释放后、后续触 UI（焦点/断点保存）前再检查关闭状态。
        if (_closing) return;
        AppLog.Info($"play [{ch.Name}] {AppConfig.RedactUrlForLog(url)}");

        // V1.4：标记正在播放的频道（列表高亮）+ 标题栏同步频道名
        SetPlayingChannel(ch);
        Title = "星辰直播 - " + ch.Name;

        // 应用用户设置的倍速与画面比例（MediaPlayer 级别，播放后生效）
        try
        {
            if (Math.Abs(_playbackRate - 1.0f) > 0.01f) _player.SetRate(_playbackRate);
            if (!string.IsNullOrEmpty(_aspectRatio)) ApplyAspectRatio(_aspectRatio);
        }
        catch (Exception ex) { AppLog.Error($"apply playback settings failed: {ex.Message}"); }

        // 播放启动后焦点归还频道列表：VideoView 的原生 HWND 会抢走键盘焦点，
        // 不归还会导致后续 ↑↓ 换台失效（真实用户高频场景）
        ChannelList.Focus();

        _config.ActiveChannelUrl = url;
        QueueSave();
    }

    private void AdjustVolume(int delta)
    {
        _config.Volume = Math.Clamp(_config.Volume + delta, 0, 125);
        if (_player != null) _player.Volume = _config.Volume;
        VolumeText.Text = $"音量 {_config.Volume}%";
        ShowOsd();
        QueueSave();
    }

    // ---------- 播放控制（暂停/停止/进度/倍速/画面比例） ----------

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    /// <summary>播放/暂停切换</summary>
    private void TogglePlayPause()
    {
        if (_player is null || _current is null) return;
        if (_isPaused)
        {
            _player.Play();
            _isPaused = false;
            BtnPlayPause.Content = "⏸";
            FsPlayPause.Content = "⏸";
            PlayState.Text = "播放中";
        }
        else
        {
            _player.Pause();
            _isPaused = true;
            BtnPlayPause.Content = "▶";
            FsPlayPause.Content = "▶";
            PlayState.Text = "已暂停";
        }
        ShowOsd();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    /// <summary>停止播放：停止当前流并清空进度</summary>
    private void StopPlayback()
    {
        // 深查第 21 轮：150ms 防抖窗口内按下的 ↑/↓ 仍挂着 _pendingPlay，停止后
        // 防抖到期会重新播起来；先清掉挂起的换台（_player 为空时同样要清）。
        _pendingPlay = null;
        _switchDebounce.Stop();
        var playerToStop = _player;
        if (playerToStop is null) return;
        // v1.4.15 2-2：代号与 _current 都在 UI 线程同步定好再排后台 Stop——
        // 旧代码把“_current 是否为空”的判断放在后台线程里，后台若抢在 UI 线程
        // 清 _current 之前跑，就会误判为“已重播”而跳过 Stop，声音继续播。
        // 代号校验：之后若有新播放提交（_playSeq 递增），迟到的 Stop 直接跳过。
        var seqAtStop = _playSeq;
        _current = null;
        _ = Task.Run(() =>
        {
            try { lock (_mainPlayerLock) { if (seqAtStop == _playSeq) playerToStop.Stop(); } } catch { }
        });
        try
        {
            _isPaused = false;
            ClearPlayingChannel();          // V1.4：取消列表播放高亮
            Title = "星辰直播";             // V1.4：标题栏恢复
            BtnPlayPause.Content = "⏸";
            FsPlayPause.Content = "⏸";
            SeekBar.Value = 0;
            TimeText.Text = "00:00";
            DurationText.Text = "--:--";
            PlayState.Text = "已停止";
            NowPlaying.Text = "未播放";
        }
        catch (Exception ex) { AppLog.Error($"stop playback failed: {ex.Message}"); }
    }

    /// <summary>进度条/时间刷新（500ms 定时器驱动）</summary>
    private void UpdateProgress()
    {
        if (_player is null || _seekDragging) return;
        try
        {
            // UI 改版：换台 OSD / 全屏浮层的节目进度随播放推进（仅浮层可见时算，避免空转）；
            // 直播也有节目进度，放在下面的直播分支之前。
            if (_current != null && (ChannelOsd.Visibility == Visibility.Visible || FullscreenOverlay.Visibility == Visibility.Visible))
            {
                var (_, programProgress) = CurrentProgram(_current);
                if (ChannelOsd.Visibility == Visibility.Visible && OsdProgress.Visibility == Visibility.Visible)
                    OsdProgress.Value = programProgress;
                if (FullscreenOverlay.Visibility == Visibility.Visible && FsProgress.Visibility == Visibility.Visible)
                    FsProgress.Value = programProgress;
            }
            var len = _player.Length;
            var t = _player.Time;
            // v1.4.16：直播流没有总时长——libvlc 报的 Length 只是 HLS 滑动窗口（十几秒），
            // 拿它当总时长会显示 00:18/00:16 这种“已播超总时长”；直播只显示“直播”，不驱动进度条。
            if (_current != null && IsLiveStream(_current.Url))
            {
                DurationText.Text = "直播";
                TimeText.Text = FormatTime(t);
                return;
            }
            if (len > 0)
            {
                SeekBar.Maximum = len;
                if (!_seekDragging) SeekBar.Value = Math.Clamp(t, 0, len);
                DurationText.Text = FormatTime(len);
            }
            TimeText.Text = FormatTime(t);
        }
        catch (Exception ex) { AppLog.Error($"update progress failed: {ex.Message}"); }
    }

    private static string FormatTime(long ms)
    {
        if (ms < 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void SeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = true;
        _seekPending = true;
    }

    private void SeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = false;
        if (_seekPending && _player != null)
        {
            _seekPending = false;
            try { _player.Time = (long)SeekBar.Value; } catch (Exception ex) { AppLog.Error($"seek failed: {ex.Message}"); }
        }
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seekDragging) TimeText.Text = FormatTime((long)e.NewValue);
    }

    /// <summary>设置倍速（0.5x-2x）</summary>
    private void SetPlaybackRate(float rate)
    {
        _playbackRate = Math.Clamp(rate, 0.5f, 2.0f);
        if (_player != null) { try { _player.SetRate(_playbackRate); } catch (Exception ex) { AppLog.Error($"set rate failed: {ex.Message}"); } }
        ShowOsd();
    }

    /// <summary>设置画面比例：空=原始，16:9/4:3/拉伸</summary>
    private void SetAspectRatio(string ratio)
    {
        _aspectRatio = ratio;
        ApplyAspectRatio(ratio);
        ShowOsd();
    }

    private void ApplyAspectRatio(string ratio)
    {
        if (_player is null) return;
        try
        {
            if (ratio == "拉伸") { _player.Scale = 0; _player.AspectRatio = ""; }
            else if (string.IsNullOrEmpty(ratio)) { _player.Scale = 1; _player.AspectRatio = ""; }
            else { _player.Scale = 0; _player.AspectRatio = ratio; }
        }
        catch (Exception ex) { AppLog.Error($"apply aspect ratio failed: {ex.Message}"); }
    }

    private void ToggleFavorite(ChannelItem ch)
    {
        ch.IsFavorite = !ch.IsFavorite;
        if (ch.IsFavorite)
        {
            if (!_config.Favorites.Contains(ch.Url)) _config.Favorites.Add(ch.Url);
        }
        else _config.Favorites.Remove(ch.Url);
        if (_favOnly) ApplyFilter();
        QueueSave();
    }

    private void QueueSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    /// <summary>配置保存放后台线程（v1.4.15 迭代 3）：Save 含 DPAPI 加密 + 原子换名，
    /// 同步写盘在慢盘/杀毒扫描时会卡 UI；迭代 1 只改了防抖路径，本处补上其余 6 处直接调用。
    /// 读盘失败待确认时仍走 UI 线程（Save 里要弹确认框）；退出时的最后一次保存见 Window_Closing。</summary>
    private void SaveConfigAsync()
    {
        if (AppConfig.ReadFailurePending) _config.Save();
        else _ = Task.Run(() => _config.Save());
    }

    private void ToggleFullscreen()
    {
        _lastFullscreenToggleUtc = DateTime.UtcNow; // 双击识别的防重入时间戳：切完 300ms 内忽略左键按下
        if (!_fullscreen)
        {
            // 小窗播时主窗口是黑+最小化：用户主动切全屏时先解除该效果，后面正常进全屏
            // （小窗不设 Owner，最小化主窗口不会连带隐藏小窗，无需恢复 Owner 关系）
            if (_pipEffectActive)
            {
                _pipEffectActive = false;
            }
            // 进全屏前记住当前状态与位置：退出时原样恢复（最大化回 Maximized，普通窗口回原尺寸位置）
            _preFullscreenState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _preFullscreenBounds = WindowState == WindowState.Maximized
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);
            // 侧栏宽度只在展开时保存：小窗开着或用户已手动收起时 SideCol.Width 是 0，
            // 直接保存会把 _sideWidth 覆写为 0，退出全屏/关小窗后侧栏再也拉不出来（v1.4.8 bug）。
            if (SideCol.Width.Value > 0) _sideWidth = SideCol.Width;
            _fsSideCollapsed = _sideCollapsed || SideCol.Width.Value <= 0;
            SideCol.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
            StatusBar.Visibility = Visibility.Collapsed;  // 全屏沉浸：隐藏状态栏
            SideToggleBtn.Visibility = Visibility.Collapsed; // 全屏隐藏侧栏按钮
            if (_pipWindow != null) _pipWindow.Visibility = Visibility.Hidden; // 全屏隐藏小窗
            // 全屏控制浮层：鼠标移动显示，静止自动隐藏
            MouseMove += Fullscreen_MouseMove;
            ShowFullscreenOverlay();
            ReattachVideoSurface(); // WindowStyle 改变会重建顶层 HWND，VideoView 原生视频子 HWND 随之销毁，需重挂
        }
        else
        {
            // 退出全屏恢复侧栏：小窗还开着时侧栏仍由小窗管着（保持收起，关小窗时再恢复，
            // 小窗已关则清掉 _pipCollapsedSidebar，走下面的正常恢复）；
            // 进全屏前用户已手动收起时保持收起；否则恢复展开宽度。
            if (_pipCollapsedSidebar && _pipWindow != null)
            {
                SideCol.Width = new GridLength(0);
            }
            else
            {
                _pipCollapsedSidebar = false;
                SideCol.Width = _fsSideCollapsed ? new GridLength(0) : ExpandedSideWidth();
            }
            _sideCollapsed = SideCol.Width.Value <= 0;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            // 按进全屏前的状态恢复：最大化回 Maximized，普通窗口回原尺寸位置，不再写死 Normal。
            // 写死 Normal 会让最大化窗口退出时缩成小窗，双击第二下落到任务栏上，点任务栏图标即最小化。
            if (_preFullscreenState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                if (WindowPlacement.CanRestore(_preFullscreenBounds.Left, _preFullscreenBounds.Top,
                        _preFullscreenBounds.Width, _preFullscreenBounds.Height))
                {
                    var (cl, ct, cw, ch) = WindowPlacement.ClampToVisible(
                        _preFullscreenBounds.Left, _preFullscreenBounds.Top,
                        _preFullscreenBounds.Width, _preFullscreenBounds.Height);
                    Left = cl;
                    Top = ct;
                    Width = cw;
                    Height = ch;
                }
            }
            _fullscreen = false;
            _osdTimer.Stop();
            _fsHideTimer.Stop();
            MouseMove -= Fullscreen_MouseMove;
            FullscreenOverlay.Visibility = Visibility.Collapsed; // 退出全屏隐藏浮层
            StatusBar.Visibility = Visibility.Visible;    // 退出全屏恢复状态栏
            SideToggleBtn.Visibility = Visibility.Visible; // 恢复侧栏按钮
            SideToggleBtn.SetValue(TextBlock.TextProperty, _sideCollapsed ? "▶" : "◀"); // 箭头跟随侧栏实际状态
            if (_pipWindow != null && _pipChannel != null) _pipWindow.Visibility = Visibility.Visible; // 恢复小窗
            ReattachVideoSurface(); // 同上：样式改完重挂视频面，否则 libvlc 往已销毁的 HWND 送帧导致黑屏
            ApplyPipMainEffect(); // 小窗开着：退出全屏后主窗口仍应黑+最小化（重挂的延迟回调会被效果标志拦住）
        }
    }

    /// <summary>重挂视频面：WindowStyle 改变会重建顶层 HWND，VideoView 的原生视频子 HWND
    /// 随之销毁，libvlc 仍往已销毁的 HWND 送帧导致黑屏；把 MediaPlayer 先置 null 再挂回，
    /// 强制 VideoView 重建视频面。Dispatcher 上执行，等样式切换完成后再挂。</summary>
    private void ReattachVideoSurface()
    {
        if (_player is null || _closing) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_player is null || _closing) return;
            if (_pipEffectActive) return; // 小窗播时主窗口应保持黑屏，不重挂
            try
            {
                VideoView.MediaPlayer = null;
                VideoView.MediaPlayer = _player;
                AppLog.Info("video surface reattached after fullscreen toggle");
                // 1 秒后检查：重挂后仍未恢复播放（不含用户主动暂停），重播当前频道兜底
                _reattachCheckTimer.Stop();
                _reattachCheckTimer.Start();
                // 小窗开着时按 F11 进主全屏：主播放器是停着的，别等 1 秒检查，直接重播当前频道。
                // （退出全屏且小窗开着时，回调跑到这里之前 ApplyPipMainEffect 已把标志置回真，
                // 上面的 _pipEffectActive 守卫会提前返回，走不到这里，不会跟小窗效果打架）
                if (_current != null && !_isPaused && !_player.IsPlaying)
                    _ = PlayAsync(_current);
            }
            catch (Exception ex) { AppLog.Error($"reattach video surface failed: {ex.Message}"); }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>重挂后 1 秒检查：播放器已停且非用户暂停时，重播当前频道兜底。</summary>
    private void OnReattachCheckTick()
    {
        _reattachCheckTimer.Stop();
        if (_pipEffectActive) return; // 小窗播时主窗口应保持黑屏，不重播
        if (_closing || _player is null || _current is null || _isPaused) return;
        try
        {
            // v1.4.15 迭代 1：缓冲中（Buffering）时 IsPlaying 为 false，慢启动流会被误判
            // 未恢复而多播一次；缓冲态也视为“正在播”，不重播。
            if (!_player.IsPlaying && _player.State != VLCState.Buffering)
            {
                AppLog.Info("video surface still not playing after reattach, replaying current channel");
                _ = PlayAsync(_current);
            }
        }
        catch (Exception ex) { AppLog.Error($"reattach check failed: {ex.Message}"); }
    }

    /// <summary>全屏鼠标移动：显示控制浮层并重置隐藏计时</summary>
    private void Fullscreen_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        ShowFullscreenOverlay();
    }

    /// <summary>显示全屏控制浮层并更新当前频道名与当前节目，3 秒后自动隐藏</summary>
    private void ShowFullscreenOverlay()
    {
        if (!_fullscreen) return;
        FsNowPlaying.Text = _current?.Name ?? "未播放";
        FsPlayPause.Content = _isPaused ? "▶" : "⏸";
        // UI 改版：浮层底部控制条带当前节目名与节目进度
        if (_current != null)
        {
            var (cur, progress) = CurrentProgram(_current);
            FsEpg.Text = cur != null ? $"正在播放 {cur.TimeRange}  {cur.Title}" : "";
            FsProgress.Value = progress;
            FsProgress.Visibility = cur != null ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            FsEpg.Text = "";
            FsProgress.Visibility = Visibility.Collapsed;
        }
        FullscreenOverlay.Visibility = Visibility.Visible;
        _fsHideTimer.Stop();
        _fsHideTimer.Start();
    }

    // ---------- 侧栏收起/展开 ----------

    /// <summary>侧栏展开宽度：_sideWidth 只在展开时保存，正常是上次的展开宽度；
    /// 一旦被覆写为 0（v1.4.8 全屏入口无条件保存的 bug），回落 300，避免侧栏再也拉不出来。</summary>
    private GridLength ExpandedSideWidth() =>
        _sideWidth.Value > 0 ? _sideWidth : new GridLength(300);

    /// <summary>侧栏边缘按钮：收起/展开侧栏</summary>
    private void SideToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    /// <summary>收起/展开侧栏：收起时缩进最左，视频区占满</summary>
    private void ToggleSidebar()
    {
        if (_sideCollapsed)
        {
            // 展开：恢复之前宽度（_sideWidth 被覆写为 0 时回落 300，否则再也拉不出来）
            SideCol.Width = ExpandedSideWidth();
            _sideCollapsed = false;
            SideToggleBtn.SetValue(TextBlock.TextProperty, "◀");
        }
        else
        {
            // 收起：只在展开时记录当前宽度；小窗/全屏已把宽度压到 0 时不再保存，否则 _sideWidth 被覆写为 0
            if (SideCol.Width.Value > 0) _sideWidth = SideCol.Width;
            SideCol.Width = new GridLength(0);
            _sideCollapsed = true;
            SideToggleBtn.SetValue(TextBlock.TextProperty, "▶");
        }
    }

    /// <summary>一键恢复初始布局：侧栏展开、小窗关闭、退出全屏</summary>
    private void ResetLayout()
    {
        // 退出全屏
        if (_fullscreen) ToggleFullscreen();
        // 关闭小窗
        if (_pipWindow != null && _pipWindow.IsVisible) ClosePip();
        // 展开侧栏
        if (_sideCollapsed)
        {
            SideCol.Width = ExpandedSideWidth();
            _sideCollapsed = false;
            SideToggleBtn.SetValue(TextBlock.TextProperty, "◀");
        }
        // 恢复状态栏
        StatusBar.Visibility = Visibility.Visible;
        AppLog.Info("layout reset");
    }

    // ---------- 数字键换台 / 换台 OSD / 播放高亮（V1.4） ----------

    /// <summary>数字键输入：缓冲 → OSD 预览 → 唯一匹配立即提交，否则 1.2s 超时提交</summary>
    private void HandleDigit(char d)
    {
        _digitBuf = DigitTuner.AppendDigit(_digitBuf, d);
        OsdNumber.Text = _digitBuf;
        OsdName.Text = "输入频道号…";
        OsdEpg.Text = "";
        OsdProgress.Visibility = Visibility.Collapsed;   // 数字输入预览时不显示旧节目进度
        ChannelOsd.Visibility = Visibility.Visible;
        _osdHideTimer.Stop();
        _osdHideTimer.Start();
        if (DigitTuner.ShouldCommit(_all, _digitBuf))
        {
            CommitDigits();
            return;
        }
        _digitTimer.Stop();
        _digitTimer.Start();
    }

    /// <summary>提交当前数字缓冲：匹配频道则换台，无匹配则提示后消失</summary>
    private void CommitDigits()
    {
        _digitTimer.Stop();
        var buf = _digitBuf;
        _digitBuf = "";
        if (buf.Length == 0) return;
        if (_closing) return;
        var ch = DigitTuner.Match(_all, buf);
        if (ch is null)
        {
            OsdNumber.Text = buf;
            OsdName.Text = $"无 {buf} 号频道";
            OsdEpg.Text = "";
            OsdProgress.Visibility = Visibility.Collapsed;   // 无匹配提示时不显示旧节目进度
            ChannelOsd.Visibility = Visibility.Visible;
            _osdHideTimer.Stop();
            _osdHideTimer.Start();
            AppLog.Info($"digit tune miss: {buf}");
            return;
        }
        SelectInList(ch);
        NowPlaying.Text = ch.Name;
        ShowChannelOsd(ch);
        _ = PlayAsync(ch);
    }

    /// <summary>换台 OSD：底部控制条（频道号 + 名称 + 当前节目 + 节目进度），3 秒自动隐藏（数字输入中不覆盖）。
    /// UI 改版：由右上角卡片改为底部横条；边距仍按视频区尺寸自适应——电视 HDMI 常有 overscan 裁边，
    /// 底边距取视频区高度的 2%（不小于 16px），左右各取宽度的 2%（不小于 16px），保证横条完整落在屏幕内。</summary>
    private void ShowChannelOsd(ChannelItem ch)
    {
        if (_digitBuf.Length > 0) return;
        var vh = VideoView.ActualHeight;
        var vw = VideoView.ActualWidth;
        var osdBottom = vh > 0 ? Math.Max(16.0, vh * 0.02) : 16.0;
        var osdSide = vw > 0 ? Math.Max(16.0, vw * 0.02) : 16.0;
        ChannelOsd.Margin = new Thickness(osdSide, 0, osdSide, osdBottom);
        OsdNumber.Text = ch.Number > 0 ? ch.Number.ToString() : "";
        OsdName.Text = ch.Name;
        var (cur, progress) = CurrentProgram(ch);
        OsdEpg.Text = cur != null ? $"正在播放 {cur.TimeRange}  {cur.Title}" : "";
        OsdProgress.Value = progress;
        OsdProgress.Visibility = cur != null ? Visibility.Visible : Visibility.Collapsed;
        // OSD 台标：先显示首字符占位，有 Logo 地址时异步取图（复用 LogoService 三级缓存）；
        // 序号守卫：换台很快时旧图请求回来不再覆盖新频道，取图失败时占位保持显示。
        OsdLogoInitial.Text = ch.Initial;
        OsdLogoImg.Source = null;
        OsdLogoFallback.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(ch.Logo))
        {
            var seq = ++_osdLogoSeq;
            _ = LoadOsdLogoAsync(ch.Logo, seq);
        }
        ChannelOsd.Visibility = Visibility.Visible;
        _osdHideTimer.Stop();
        _osdHideTimer.Start();
    }

    /// <summary>OSD 台标异步加载：序号已变（又换了台）或 Logo 取失败时不碰界面，占位保持显示。
    /// 2026-11-14 1.4.19：取到图后不再隐藏首字符占位——有的台标图是透明/空白底，
    /// 藏掉占位后横幅里只剩空黑框；占位留作垫底，图不透明时会盖住它，透明图则透出首字符，台标区永不为空。</summary>
    private async Task LoadOsdLogoAsync(string url, int seq)
    {
        var bmp = await LogoService.GetAsync(url);
        if (seq != _osdLogoSeq || bmp is null) return;
        OsdLogoImg.Source = bmp;
    }

    /// <summary>标记正在播放的列表项（左侧蓝色指示条）；旧项取消高亮</summary>
    private void SetPlayingChannel(ChannelItem ch)
    {
        if (ReferenceEquals(_playingItem, ch)) return;
        var old = _playingItem;
        _playingItem = ch;
        if (old != null) old.IsPlaying = false;
        ch.IsPlaying = true;
    }

    /// <summary>清除播放态高亮（停止播放/切换源时）</summary>
    private void ClearPlayingChannel()
    {
        if (_playingItem is null) return;
        _playingItem.IsPlaying = false;
        _playingItem = null;
    }

    /// <summary>EPG 每分钟刷新本地正在播放显示：节目跨档后“正在播放”保持准确，OSD 可见时同步刷新</summary>
    private void OnEpgRefreshTick()
    {
        if (_closing || _current is null) return;
        ShowEpgForChannel(_current);
        RefreshChannelNowPrograms();   // UI 改版：频道行当前节目 + 进度随分钟刷新
        BuildGuide();                  // UI 改版：节目表打开时同步重建（关闭时内部直接返回）
        if (ChannelOsd.Visibility == Visibility.Visible && _digitBuf.Length == 0)
            ShowChannelOsd(_current);   // 顺带重置隐藏计时，刷新后重新计 3 秒
    }

    /// <summary>频道 Logo 异步加载：内存/磁盘缓存命中即显示，失败留在首字符占位</summary>
    private void LogoImg_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image img) return;
        img.Source = null;
        if (img.DataContext is not ChannelItem ch || string.IsNullOrWhiteSpace(ch.Logo)) return;
        _ = LoadLogoInto(img, ch.Logo);
    }

    private async Task LoadLogoInto(Image img, string url)
    {
        var bmp = await LogoService.GetAsync(url);
        // 虚拟化回收后 DataContext 可能已换绑到其他频道：仅当仍是原图请求时才赋值
        if (img.DataContext is not ChannelItem cur || cur.Logo != url) return;
        img.Source = bmp;
        if (img.Parent is Grid g && g.Children.Count > 0 && g.Children[0] is Border fallback)
            fallback.Visibility = bmp != null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- 窗口位置记忆（V1.4） ----------

    /// <summary>启动时恢复上次窗口位置/尺寸：校验通过才应用（并钳制到可见屏幕内），
    /// 非法/越界/非有限值时不应用记忆值、回落 XAML 默认（CenterScreen，1120x640），最大化状态单独恢复</summary>
    private void RestoreWindowPlacement()
    {
        if (_config.WindowMaximized) WindowState = WindowState.Maximized;
        if (_config.WindowWidth is double w && _config.WindowHeight is double h &&
            w >= MinWidth && h >= MinHeight &&
            _config.WindowLeft is double l && _config.WindowTop is double t &&
            WindowPlacement.CanRestore(l, t, w, h))
        {
            var (cl, ct, cw, ch) = WindowPlacement.ClampToVisible(l, t, w, h);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = cl;
            Top = ct;
            Width = cw;
            Height = ch;
        }
        else if (!_config.WindowMaximized)
        {
            // 显式回落：不以记忆值覆盖默认位置（此前是隐式不应用，此处写明意图）。
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>关闭时保存窗口位置/尺寸；最大化只记标志，避免存下异常坐标</summary>
    private void SaveWindowPlacement()
    {
        if (WindowState == WindowState.Normal && !_fullscreen)
        {
            _config.WindowLeft = Left;
            _config.WindowTop = Top;
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
        }
        _config.WindowMaximized = WindowState == WindowState.Maximized;
    }

    // ---------- 小窗播放（画中画） ----------

    private void BtnPip_Click(object sender, RoutedEventArgs e)
    {
        // 工具栏小窗按钮：若小窗开着则关闭，否则小窗播放当前选中/正在播放的频道。
        // 全屏时小窗只是隐藏（IsVisible 为假），仍算开着，点按钮应关闭而不是重开。
        if (_pipWindow != null) { ClosePip(); return; }
        var ch = ChannelList.SelectedItem as ChannelItem ?? _current;
        if (ch != null) OpenPip(ch);
    }

    private void MiPip_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelItem ch) OpenPip(ch);
    }

    /// <summary>新窗口播放：打开独立频道窗口（多窗口同时播放）</summary>
    private async void MiNewWin_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (ChannelList.SelectedItem is not ChannelItem ch) return;
        await EnsureEngineAsync();
        if (_closing) return;   // M4：等待期间窗口可能已关闭并释放引擎，不得再用 _libvlc 建播放器
        if (_libvlc is null) return;
        var win = new ChannelWindow(_libvlc, _config.Volume, _config.NetworkCache) { Owner = this };
        _channelWindows.Add(win);
        win.Closed += (_, _) => _channelWindows.Remove(win);
        win.Show();
        win.Play(ch);
    }

    /// <summary>打开小窗播放指定频道；小窗模式下自动收起节目列表（侧栏）</summary>
    private async void OpenPip(ChannelItem ch)
    {
        if (_closing) return;
        var seq = ++_pipSeq;   // 本次打开序号
        await EnsureEngineAsync();
        // await 期间可能已关闭主窗口、或用户已关小窗又开了新的：过期续体直接返回。
        // 不复查会在已释放的引擎上建小窗（崩溃）或把已关掉的小窗复活。
        if (_closing || seq != _pipSeq) return;
        if (_libvlc is null) return;

        // 小窗播放时自动隐藏节目列表（侧栏收起），腾出视频区
        if (SideCol.Width.Value > 0)
        {
            _sideWidth = SideCol.Width;
            SideCol.Width = new GridLength(0);
            _pipCollapsedSidebar = true;
        }

        _pipChannel = ch;

        if (_pipWindow == null)
        {
            // 小窗不设 Owner：有 Owner 时最小化主窗口会连带隐藏小窗，v1.4.8 为此在可见状态下
            // 反复改 Owner（开小窗时清掉、进全屏时恢复），小窗因此丢激活、点不动；
            // 无 Owner 的独立顶层窗口不受主窗口最小化影响。主窗口关闭时已显式关小窗（见 Window_Closing）。
            _pipWindow = new PipWindow(_libvlc, _config.Volume, _config.NetworkCache);
            RestorePipPlacement(_pipWindow);
            _pipWindow.Closed += (_, _) =>
            {
                ++_pipSeq;   // 小窗已关：作废挂起的旧 OpenPip 续体
                SavePipPlacement(_pipWindow);
                RestoreMainAfterPip(); // 小窗关闭：主窗口恢复可见并恢复播放
                _pipWindow = null;
                _pipChannel = null;
                // 小窗关闭后恢复侧栏：仅恢复小窗自己收起的（见 _pipCollapsedSidebar）
                if (!_fullscreen && _pipCollapsedSidebar)
                {
                    SideCol.Width = ExpandedSideWidth();
                    _sideCollapsed = false;
                    SideToggleBtn.SetValue(TextBlock.TextProperty, "◀");
                    _pipCollapsedSidebar = false;
                }
            };
            _pipWindow.Show();
            // 全屏时新建的小窗先隐藏，退出全屏再由 ToggleFullscreen 恢复显示；
            // 同一 UI 帧内 Show 后 Hidden，不会闪现。
            if (_fullscreen) _pipWindow.Visibility = Visibility.Hidden;
            else _pipWindow.Activate(); // 主动激活：小窗要成为前台窗口才能接收鼠标，否则只见画面点不动
        }
        else if (!_pipWindow.IsVisible)
        {
            // 全屏隐藏的小窗只换台不显示，避免全屏时小窗弹出来盖住画面
            _pipWindow.Show();
            if (_fullscreen) _pipWindow.Visibility = Visibility.Hidden;
            else _pipWindow.Activate();
        }

        _pipWindow.Play(ch);
        ApplyPipMainEffect(); // 小窗开播：主窗口变黑并最小化（用户要的效果）
        AppLog.Info($"pip play [{ch.Name}]");
    }

    /// <summary>记忆小窗位置：关闭时保存到配置，下次打开恢复</summary>
    private void SavePipPlacement(PipWindow? win)
    {
        if (win == null) return;
        // v1.4.15 2-5：全屏时 Left/Top/Width/Height 就是满屏尺寸，存下它下次开小窗
        // 会变成满屏巨窗；全屏关闭时跳过保存，保留上次正常尺寸。
        if (win.IsPipFullscreen) return;
        _config.PipLeft = win.Left;
        _config.PipTop = win.Top;
        _config.PipWidth = win.Width;
        _config.PipHeight = win.Height;
        QueueSave();
    }

    /// <summary>恢复小窗位置：校验在屏内（副屏断开不恢复到屏幕外），部分出屏则钳制进屏内</summary>
    private void RestorePipPlacement(PipWindow win)
    {
        var (l, t, w, h) = (_config.PipLeft, _config.PipTop, _config.PipWidth, _config.PipHeight);
        if (l is null || t is null || w is null || h is null) return;
        if (!WindowPlacement.CanRestore(l.Value, t.Value, w.Value, h.Value)) return;
        var c = WindowPlacement.ClampToVisible(l.Value, t.Value, w.Value, h.Value);
        win.Left = c.Left;
        win.Top = c.Top;
        win.Width = c.Width;
        win.Height = c.Height;
    }

    private void ClosePip()
    {
        if (_pipWindow != null)
        {
            // 直接 Close()，让 PipWindow 自己后台释放 _media/_player；
            // 主窗口关闭路径同款（见 Window_Closing），不再先 DetachEngine。
            try { _pipWindow.Close(); } catch { }
            _pipWindow = null;
        }
        RestoreMainAfterPip(); // 兜底：Closed 回调里已恢复时为空操作
        _pipChannel = null;
        // 恢复侧栏：仅恢复小窗自己收起的（Closed 回调已处理时此处的标志已清零，不会重复恢复）
        if (!_fullscreen && _pipCollapsedSidebar)
        {
            SideCol.Width = ExpandedSideWidth();
            _sideCollapsed = false;
            SideToggleBtn.SetValue(TextBlock.TextProperty, "◀");
            _pipCollapsedSidebar = false;
        }
    }

    /// <summary>
    /// 小窗开播时主窗口的效果（用户要的效果）：主窗口变黑并最小化，小窗浮在桌面上。
    /// 注意：不能用 StopPlayback()——它会清 _current，关小窗后就不知道播哪个频道了；
    /// 这里直接 _player.Stop()（只停流、不清频道），再断开视频面保证黑屏。
    /// 另：先断开小窗的 Owner，否则最小化主窗口会连带隐藏小窗（Win32 owned window 规则）。
    /// 全屏时小窗先隐藏，不做该效果；退出全屏时由 ToggleFullscreen 退出分支补做。
    /// </summary>
    private void ApplyPipMainEffect()
    {
        if (_fullscreen || _pipWindow == null || _closing || _pipEffectActive) return;
        _prePipState = WindowState;
        // 小窗无 Owner（见 OpenPip），最小化主窗口不会连带隐藏小窗，直接最小化即可。
        // 主画面变黑：停主播放器（不停小窗的独立播放器）并断开视频面。
        // 注意：MediaPlayer.Stop() 可能阻塞（等输入/输出线程拆掉卡住的直播流），
        // 必须放后台线程——UI 线程卡在这里时小窗画不出画面（黑屏）、底层鼠标钩子
        // 也跑不了（鼠标全系统卡住），就是 v1.4.12 报的“小窗黑屏 + 卡死”。
        // 与 ActivatePlaylist/RemovePlaylist 的后台 Stop 同款处理。
        // v1.4.15 2-3：标志先同步置位再排后台 Stop（旧代码先排队后置位，后台若抢跑
        // 会误判效果未生效而跳过 Stop）；执行前在锁内再校验代号与标志——
        // 关小窗后若已重播（代号变了），迟到的 Stop 直接跳过，避免停掉新起的流。
        _pipEffectActive = true;
        var mainPlayer = _player;
        if (mainPlayer != null)
        {
            var seqAtEffect = _playSeq;
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_mainPlayerLock)
                    {
                        if (seqAtEffect == _playSeq && _pipEffectActive) mainPlayer.Stop();
                    }
                }
                catch { }
            });
        }
        try { VideoView.MediaPlayer = null; } catch { }
        PlayState.Text = "小窗播放中";
        WindowState = WindowState.Minimized;
        // 主窗口最小化后把小窗推到前台并激活：它是唯一可见的窗口，必须拿到输入焦点，否则只见画面点不动。
        try { _pipWindow.Activate(); } catch { }
        AppLog.Info("pip effect applied: main black + minimized");
    }

    /// <summary>小窗关闭：主窗口恢复可见并恢复播放。效果未生效或应用正在关闭时为空操作。</summary>
    private void RestoreMainAfterPip()
    {
        if (!_pipEffectActive) return;
        _pipEffectActive = false;
        if (_closing) return;
        WindowState = _prePipState == WindowState.Minimized ? WindowState.Normal : _prePipState;
        try { Activate(); } catch { } // 主窗口回到前台并拿回焦点
        ReattachVideoSurface(); // 重挂视频面（自带 1 秒检查：未播则重播当前频道）
        AppLog.Info("pip effect cleared: main restored");
    }

    // ---------- 网盘播放 ----------

    /// <summary>网盘按钮：未配 OpenList 时提示去设置，配好后打开网盘浏览窗</summary>
    private void BtnCloud_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.OpenListServer) ||
            string.IsNullOrWhiteSpace(_config.OpenListUsername) ||
            string.IsNullOrWhiteSpace(_config.OpenListPassword))
        {
            if (MessageBox.Show("请先在设置里填写 OpenList 服务器地址和账号密码。\n现在打开设置吗？", "网盘未配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                BtnSettings_Click(this, e);
            return;
        }
        var dlg = new Dialogs.CloudDriveWindow(this, _config) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>供网盘对话框调用：播放一个网盘项（主窗口或小窗）</summary>
    internal void PlayCloudItem(ChannelItem ch, bool pip)
    {
        if (_closing) return;
        if (pip) OpenPip(ch);
        else _ = PlayAsync(ch);
    }

    // ---------- 播放列表管理（供管理对话框调用） ----------

    internal List<PlaylistSource> Playlists => _config.Playlists;
    internal string? ActivePlaylistName => _config.ActivePlaylist;

    /// <summary>刷新主界面源列表面板</summary>
    private void RefreshPlaylistCombo()
    {
        _suppressSrcList = true;
        var lists = _config.Playlists;
        SrcList.ItemsSource = null;
        SrcList.ItemsSource = lists;
        // 当前激活源高亮
        var active = lists.FirstOrDefault(p => p.Name == _config.ActivePlaylist);
        if (active != null) SrcList.SelectedItem = active;
        _suppressSrcList = false;
    }

    /// <summary>展开/收起源列表面板</summary>
    private void SrcToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = SrcPanel.Visibility != Visibility.Visible;
        SrcPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SrcToggleBtn.Content = show ? "▴" : "▾";
        RefreshPlaylistCombo();
    }

    /// <summary>点击源列表切换源</summary>
    private void SrcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSrcList) return;
        if (SrcList.SelectedItem is PlaylistSource sel && sel.Name != _config.ActivePlaylist)
        {
            ActivatePlaylist(sel.Name);
        }
        else
        {
            // 无效选择：回弹到当前激活
            _suppressSrcList = true;
            SrcList.SelectedItem = _config.Playlists.FirstOrDefault(p => p.Name == _config.ActivePlaylist);
            _suppressSrcList = false;
        }
    }

    /// <summary>切换当前播放列表并重载</summary>
    internal void ActivatePlaylist(string name)
    {
        if (name == _config.ActivePlaylist) { RefreshPlaylistCombo(); return; }
        _config.ActivePlaylist = name;
        SaveConfigAsync();
        // 切换源：停掉旧频道（后台 Stop，避免 VLC 阻塞 UI 导致卡死），清空播放状态。
        // v1.4.15 2-4：代号校验——切源后若已播新频道（代号变了），迟到的 Stop 直接跳过，
        // 否则会停掉新起的流；此处无 1 秒重播兜底，停掉就黑屏无声。
        var p = _player;
        if (p is { IsPlaying: true })
        {
            var seq = _playSeq;
            _ = Task.Run(() => { try { lock (_mainPlayerLock) { if (seq == _playSeq) p.Stop(); } } catch { } });
        }
        _current = null;
        ClearPlayingChannel();
        Title = "星辰直播";
        NowPlaying.Text = "未播放";
        PlayState.Text = "未播放";
        _ = LoadPlaylistAsync(true, _netCts.Token);
        RefreshPlaylistCombo();
    }

    /// <summary>删除播放列表；删的是当前列表时自动切到剩余第一个</summary>
    internal void RemovePlaylist(PlaylistSource s)
    {
        var wasActive = _config.ActivePlaylist == s.Name;
        _config.Playlists.Remove(s);
        if (wasActive)
        {
            _config.ActivePlaylist = _config.Playlists.FirstOrDefault()?.Name;
            SaveConfigAsync();
            // v1.4.15 2-4：同 ActivatePlaylist，代号校验防迟到的 Stop 停掉新流。
            var p = _player;
            if (p is { IsPlaying: true })
            {
                var seq = _playSeq;
                _ = Task.Run(() => { try { lock (_mainPlayerLock) { if (seq == _playSeq) p.Stop(); } } catch { } });
            }
            _current = null;
            ClearPlayingChannel();
            Title = "星辰直播";
            NowPlaying.Text = "未播放";
            PlayState.Text = "未播放";
            _ = LoadPlaylistAsync(false, _netCts.Token);
        }
        else SaveConfigAsync();
        RefreshPlaylistCombo();
    }

    /// <summary>重命名；重名/空名拒绝；当前列表改名后激活指针同步</summary>
    internal void RenamePlaylist(PlaylistSource s, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return;
        if (_config.Playlists.Any(p => p != s && p.Name == newName)) return;
        var wasActive = _config.ActivePlaylist == s.Name;
        s.Name = newName;
        if (wasActive) _config.ActivePlaylist = newName;
        SaveConfigAsync();
        RefreshPlaylistCombo();
    }

    /// <summary>自动名防重号：找一个未被占用的“播放列表 N”</summary>
    private string NextAutoName()
    {
        for (var i = _config.Playlists.Count + 1; ; i++)
        {
            var candidate = $"播放列表 {i}";
            if (_config.Playlists.All(p => p.Name != candidate)) return candidate;
        }
    }

    /// <summary>名称唯一化：重名时追加 (2)(3)…后缀</summary>
    private string EnsureUniqueName(string name, PlaylistSource? self)
    {
        var others = _config.Playlists.Where(p => p != self).ToList();
        if (others.All(p => p.Name != name)) return name;
        for (var i = 2; ; i++)
        {
            var n = $"{name} ({i})";
            if (others.All(p => p.Name != n)) return n;
        }
    }

    // ---------- 界面事件 ----------

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddPlaylistWindow(this) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var name = string.IsNullOrWhiteSpace(dlg.SourceName)
                ? NextAutoName()
                : dlg.SourceName;
            var existing = _config.Playlists.FirstOrDefault(p => p.Url == dlg.SourceUrl);
            if (existing != null)
            {
                // 深查第 11 轮：旧代码只改 existing.Name、不回写 name，
                // 重名时 EnsureUniqueName 返回 "源名 (2)"、name 仍是原名，
                // 随后 ActivatePlaylist(name) 会激活同名的另一个播放列表，
                // 而不是用户刚改名的这条。回写 name，与 else 分支口径一致。
                name = EnsureUniqueName(name, existing);
                existing.Name = name;
            }
            else
            {
                name = EnsureUniqueName(name, null);
                _config.Playlists.Add(new PlaylistSource { Name = name, Url = dlg.SourceUrl });
            }
            SaveConfigAsync();
            ActivatePlaylist(name);   // 切到新源：停旧播、重载、刷新下拉
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadPlaylistAsync(true, _netCts.Token);

    private void BtnFav_Click(object sender, RoutedEventArgs e)
    {
        _favOnly = !_favOnly;
        (sender as Button)!.Content = _favOnly ? "★ 收藏中" : "收藏";
        ApplyFilter();
    }

    private void BtnFull_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        // 深查第 9 轮：必须设 Owner——OnThreadFilterMessage 的模态守卫只查 OwnedWindows，
        // 不设 Owner 时设置对话框打开期间 ↑/↓/空格/F 等全局键会漏拦截、在对话框背后换台/暂停/切全屏。
        var dlg = new Dialogs.SettingsWindow(_config) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            // 应用设置：画面比例、倍速、主题
            _aspectRatio = _config.AspectRatio ?? "";
            _playbackRate = (float)(_config.PlaybackRate > 0 ? _config.PlaybackRate : 1.0);
            ApplyAspectRatio(_aspectRatio);
            if (_player != null) { try { _player.SetRate(_playbackRate); } catch (Exception ex) { AppLog.Error($"apply rate from settings failed: {ex.Message}"); } }
            ApplyTheme(_config.Theme);
            // 若配置了 Xtream，加载其频道列表
            if (!string.IsNullOrEmpty(_config.XtreamServer) && _config.Playlists.Count == 0)
                _ = LoadXtreamAsync(_netCts.Token);
        }
    }

    /// <summary>从 M3U 头部解析 EPG 源地址（#EXTM3U url-tvg="..."）</summary>
    internal static string? ExtractEpgUrl(string m3uText)
    {
        try
        {
            // 深查第 10 轮：旧实现只看 Split('\n')[0]，清单首行是空行时
            // （部分采集器输出带前导空行）url-tvg 永远取不到，EPG 静默缺失。
            // 改为取首个非空行；M3uParser 同样跳过空行，口径一致。
            // L6：用 IndexOf 逐行找首个非空行，不再对整个清单 Split（20MB 清单只为
            // 取首行就切成行数组，M3uParser 还要再 Split 一次，纯属重复分配）。
            string? firstLine = null;
            var start = 0;
            while (start <= m3uText.Length)
            {
                var nl = m3uText.IndexOf('\n', start);
                var line = nl < 0 ? m3uText[start..] : m3uText[start..nl];
                if (!string.IsNullOrWhiteSpace(line)) { firstLine = line; break; }
                if (nl < 0) break;
                start = nl + 1;
            }
            if (firstLine is null) return null;
            var idx = firstLine.IndexOf("url-tvg=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var rest = firstLine[(idx + 8)..].Trim();
            if (rest.StartsWith('"'))
            {
                rest = rest[1..];
                var end = rest.IndexOf('"');
                if (end >= 0) rest = rest[..end];
            }
            else
            {
                // 深查第 22 轮：无引号写法（如 url-tvg=http://epg.xml x-tvg-url="..."）
                // 旧代码把后随属性一并当成地址，拉取必失败；以空白截断。
                var sp = rest.IndexOfAny(new[] { ' ', '\t' });
                if (sp >= 0) rest = rest[..sp];
            }
            return rest.Trim().Length > 0 ? rest.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>加载 EPG 节目单（XMLTV）</summary>
    private async Task LoadEpgAsync(string url, CancellationToken ct)
    {
        // F7：与 _loadSeq 同模式的序号，并捕获调用时的 url；完成时比对不一致则丢弃。
        var seq = ++_epgSeq;
        try
        {
            var xml = await FetchTextAsync(url, ct, MaxEpgBytes);
            var epg = await Task.Run(() => EpgParser.Parse(xml), ct);
            if (seq != _epgSeq) return;   // 已有更新的 EPG 请求，丢弃旧结果，不覆盖 _epg、不刷新当前频道节目
            _epgUrl = url;   // M3：仅成功后才更新 _epgUrl，失败时保持旧值，下次刷新仍会重试
            _epg = epg;
            // L7：同步重建名称索引（TryAdd：仅大小写不同的 id 碰撞时先到先得，与旧线性扫描的首个匹配一致）。
            _epgByName = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in epg) _epgByName.TryAdd(kv.Key, kv.Value);
            AppLog.Info($"epg loaded {epg.Count} channels from {AppConfig.RedactUrlForLog(url)}");
            // 刷新当前频道节目显示
            if (_current != null) ShowEpgForChannel(_current);
            RefreshChannelNowPrograms();   // UI 改版：频道行当前节目 + 进度
            BuildGuide();                  // UI 改版：节目表打开时同步重建（关闭时内部直接返回）
        }
        catch (OperationCanceledException)
        {
            // 取消属正常关闭/切换，静默返回。
        }
        catch (Exception ex)
        {
            // F7：失败不清空已有 _epg，仅记日志，当前节目显示保持旧数据。
            AppLog.Error($"epg load failed: {ex.Message}");
        }
        finally
        {
            // M3：加载结束（成功/失败/取消/被新请求取代）后清除在途标记；若已有更新的
            // 加载启动（标记已被覆写为新地址），不清除他人的标记。
            if (_epgLoadingUrl == url) _epgLoadingUrl = null;
        }
    }

    /// <summary>取频道当前正在播的节目及其已播进度（0..1）；无节目时返回 (null, 0)。
    /// 频道行第二行、换台 OSD、全屏浮层、节目表高亮共用同一口径。</summary>
    private (EpgProgram? Program, double Progress) CurrentProgram(ChannelItem ch)
    {
        var now = DateTime.Now;
        var cur = GetPrograms(ch).FirstOrDefault(p => p.Start <= now && now < p.Stop);
        if (cur is null) return (null, 0);
        var total = (cur.Stop - cur.Start).TotalMinutes;
        var progress = total > 0 ? Math.Clamp((now - cur.Start).TotalMinutes / total, 0, 1) : 0;
        return (cur, progress);
    }

    /// <summary>刷新过滤后频道行的“当前节目 + 进度”（UI 改版）：
    /// EPG 加载成功、每分钟时效刷新、过滤条件变化后调用；无 EPG 数据时直接返回。</summary>
    private void RefreshChannelNowPrograms()
    {
        if (_epg.Count == 0) return;
        var now = DateTime.Now;
        foreach (var ch in _filtered)
        {
            var cur = GetPrograms(ch).FirstOrDefault(p => p.Start <= now && now < p.Stop);
            if (cur is null) ch.SetNowProgram("", 0);
            else
            {
                var total = (cur.Stop - cur.Start).TotalMinutes;
                var progress = total > 0 ? Math.Clamp((now - cur.Start).TotalMinutes / total, 0, 1) : 0;
                ch.SetNowProgram($"{cur.Title}  {cur.TimeRange}", progress);
            }
        }
    }

    /// <summary>获取频道当前/即将播出的节目</summary>
    private List<EpgProgram> GetPrograms(ChannelItem ch)
    {
        var id = ch.TvgId;
        if (string.IsNullOrEmpty(id)) return new List<EpgProgram>();
        if (_epg.TryGetValue(id, out var list)) return list;
        // 名称回退：查预建的索引，不再线性扫整个 EPG 字典（每次换台都触发，见 L7）。
        if (_epgByName.TryGetValue(ch.Name, out var byName)) return byName;
        return new List<EpgProgram>();
    }

    private void ShowEpgForChannel(ChannelItem ch)
    {
        var progs = GetPrograms(ch);
        if (progs.Count == 0) { EpgPanel.Visibility = Visibility.Collapsed; return; }
        EpgPanel.Visibility = Visibility.Visible;
        var now = DateTime.Now;
        var current = progs.FirstOrDefault(p => p.Start <= now && now < p.Stop);
        var upcoming = progs.Where(p => p.Start > now).Take(4).ToList();
        EpgList.ItemsSource = upcoming;
        if (current != null)
        {
            EpgNow.Text = $"正在播放：{current.Title}  {current.TimeRange}";
            EpgNow.Visibility = Visibility.Visible;
        }
        else EpgNow.Visibility = Visibility.Collapsed;
    }

    // ---------- 节目表（时间轴）视图（UI 改版） ----------
    // 数据复用现有 EPG（_epg / GetPrograms），不改解析；行取当前过滤后的频道
    //（分组/搜索/收藏同样生效），仅保留窗口内有节目的，至多 200 行。

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    /// <summary>节目表入口（侧栏“节目表”按钮）：开/关切换</summary>
    private void BtnGuide_Click(object sender, RoutedEventArgs e) => ToggleGuide();

    private void GuideClose_Click(object sender, RoutedEventArgs e) => CloseGuide();

    private void GuideNow_Click(object sender, RoutedEventArgs e)
    {
        BuildGuide();
        ScrollGuideToNow();
    }

    /// <summary>节目表开/关：打开时重建时间轴并滚到“现在”，关闭时清空选中详情</summary>
    private void ToggleGuide()
    {
        if (GuideView.Visibility == Visibility.Visible) CloseGuide();
        else
        {
            GuideView.Visibility = Visibility.Visible;
            BuildGuide();
            // 刚设为可见时布局未跑完，ViewportWidth 还是 0：等布局完成后再滚动定位
            Dispatcher.BeginInvoke(new Action(ScrollGuideToNow), DispatcherPriority.Loaded);
        }
    }

    private void CloseGuide()
    {
        GuideView.Visibility = Visibility.Collapsed;
        _guideSelectedBlock = null;
        _guideSelectedChannel = null;
        _guideSelectedProgram = null;
        GuideDetail.Visibility = Visibility.Collapsed;
        GuideDetailEmpty.Visibility = Visibility.Visible;
    }

    /// <summary>把时间轴横向滚到“现在”竖线附近（留 1/4 视口看过去的节目），纵向回顶</summary>
    private void ScrollGuideToNow()
    {
        var nowX = (DateTime.Now - _guideWindowStart).TotalMinutes * GuidePxPerMinute;
        GuideTimelineScroll.ScrollToHorizontalOffset(Math.Max(0, nowX - GuideTimelineScroll.ViewportWidth * 0.25));
        GuideTimelineScroll.ScrollToVerticalOffset(0);
        GuideChannelScroll.ScrollToVerticalOffset(0);
    }

    /// <summary>时间轴纵向滚动时同步左侧频道列（频道列自身不处理滚动事件，不会回环）</summary>
    private void GuideTimelineScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0) GuideChannelScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    /// <summary>重建节目表时间轴：窗口为现在前 30 分钟起共 4 小时。
    /// 节目表关闭时不建（ApplyFilter / 每分钟刷新也会调到这里，由此守卫挡住）。</summary>
    private void BuildGuide()
    {
        if (GuideView.Visibility != Visibility.Visible) return;
        var now = DateTime.Now;
        _guideWindowStart = now.AddMinutes(-30);
        var windowEnd = _guideWindowStart.AddHours(4);
        var timelineWidth = 4 * 60 * GuidePxPerMinute;

        GuideGroupText.Text = GroupCombo.SelectedItem as string ?? "全部";
        GuideWindowText.Text = $"{_guideWindowStart:HH:mm} - {windowEnd:HH:mm}";

        // 重建前记住选中（频道 Url + 节目开始时间）：重建后同一节目仍在窗口内则恢复选中与详情
        var selUrl = _guideSelectedChannel?.Url;
        var selStart = _guideSelectedProgram?.Start;
        _guideSelectedBlock = null;

        // 频道行：窗口内有节目的过滤后频道，上限 200 行
        _guideChannels = _filtered
            .Where(ch => GetPrograms(ch).Any(p => p.Stop > _guideWindowStart && p.Start < windowEnd))
            .Take(GuideMaxRows)
            .ToList();

        var accent = ThemeBrush("AccentBrush", Brushes.DodgerBlue);
        var border = ThemeBrush("BorderBrush", Brushes.Gray);
        var panelBg = ThemeBrush("PanelBgBrush", Brushes.DimGray);
        var itemFg = ThemeBrush("ItemFgBrush", Brushes.White);
        var subFg = ThemeBrush("SubFgBrush", Brushes.LightGray);
        var mutedFg = ThemeBrush("MutedFgBrush", Brushes.Gray);
        var nowBg = ThemeBrush("GuideNowBgBrush", Brushes.DarkSlateBlue);

        // ---- 频道列 ----
        GuideChannelList.Children.Clear();
        foreach (var ch in _guideChannels)
        {
            var row = new Border
            {
                Height = GuideRowHeight,
                Background = Brushes.Transparent,
                BorderBrush = border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor = Cursors.Hand,
                Tag = ch,
                ToolTip = "点击播放该频道"
            };
            row.MouseLeftButtonUp += GuideChannel_Click;
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // 台标：与主列表同结构（Grid[Border 占位, Image]），复用 LogoImg_Loaded 异步加载
            var logoGrid = new Grid { Width = 28, Height = 28, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var fallback = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x8F, 0xA0)),
                Child = new TextBlock
                {
                    Text = ch.Initial, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = itemFg,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            var img = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform, DataContext = ch };
            img.Loaded += LogoImg_Loaded;
            logoGrid.Children.Add(fallback);
            logoGrid.Children.Add(img);
            Grid.SetColumn(logoGrid, 0);
            rowGrid.Children.Add(logoGrid);
            var name = new TextBlock
            {
                Text = ch.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _current?.Url == ch.Url ? accent : itemFg,
                FontWeight = _current?.Url == ch.Url ? FontWeights.SemiBold : FontWeights.Normal
            };
            Grid.SetColumn(name, 1);
            rowGrid.Children.Add(name);
            row.Child = rowGrid;
            GuideChannelList.Children.Add(row);
        }

        // ---- 时间刻度：每 30 分钟一格 ----
        GuideRuler.Children.Clear();
        GuideRuler.Width = timelineWidth;
        for (var t = _guideWindowStart; t <= windowEnd; t = t.AddMinutes(30))
        {
            var x = (t - _guideWindowStart).TotalMinutes * GuidePxPerMinute;
            var tick = new Border { Width = 1, Height = 8, Background = border, VerticalAlignment = VerticalAlignment.Bottom };
            Canvas.SetLeft(tick, x);
            GuideRuler.Children.Add(tick);
            var label = new TextBlock { Text = t.ToString("HH:mm"), FontSize = 11, Foreground = mutedFg };
            Canvas.SetLeft(label, x + 4);
            Canvas.SetTop(label, 6);
            GuideRuler.Children.Add(label);
        }

        // ---- 节目行 ----
        GuideRows.Children.Clear();
        GuideRows.RowDefinitions.Clear();
        GuideRows.Width = timelineWidth;
        GuideTimelineContent.Width = timelineWidth;
        for (var i = 0; i < _guideChannels.Count; i++)
        {
            var ch = _guideChannels[i];
            GuideRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GuideRowHeight) });
            var rowCanvas = new Canvas();
            Grid.SetRow(rowCanvas, i);
            foreach (var prog in GetPrograms(ch))
            {
                if (prog.Stop <= _guideWindowStart || prog.Start >= windowEnd) continue;
                var start = prog.Start < _guideWindowStart ? _guideWindowStart : prog.Start;
                var stop = prog.Stop > windowEnd ? windowEnd : prog.Stop;
                var x = (start - _guideWindowStart).TotalMinutes * GuidePxPerMinute;
                var w = Math.Max(24, (stop - start).TotalMinutes * GuidePxPerMinute - 2);
                var isNow = prog.Start <= now && now < prog.Stop;
                var block = new Border
                {
                    Width = w,
                    Height = GuideRowHeight - 8,
                    CornerRadius = new CornerRadius(8),
                    Background = isNow ? nowBg : panelBg,
                    BorderBrush = isNow ? accent : border,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = (ch, prog),
                    ToolTip = $"{prog.Title}  {prog.TimeRange}"
                };
                block.MouseLeftButtonUp += GuideProgram_Click;
                var inner = new Grid();
                inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var title = new TextBlock
                {
                    Text = prog.Title, FontSize = 12, Foreground = itemFg,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 5, 8, 0), VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(title, 0);
                inner.Children.Add(title);
                if (isNow)
                {
                    var (_, progress) = CurrentProgram(ch);
                    var bar = new Border
                    {
                        Height = 3, Background = accent,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(8, 0, 8, 5), Width = Math.Max(0, w - 16) * progress
                    };
                    Grid.SetRow(bar, 1);
                    inner.Children.Add(bar);
                }
                else
                {
                    var time = new TextBlock
                    {
                        Text = prog.TimeRange, FontSize = 10, Foreground = subFg,
                        Margin = new Thickness(8, 0, 8, 5)
                    };
                    Grid.SetRow(time, 1);
                    inner.Children.Add(time);
                }
                block.Child = inner;
                Canvas.SetLeft(block, x);
                Canvas.SetTop(block, 4);
                rowCanvas.Children.Add(block);
                // 重建后恢复之前的选中
                if (selUrl != null && ch.Url == selUrl && selStart == prog.Start)
                    ShowGuideDetail(ch, prog, block);
            }
            GuideRows.Children.Add(rowCanvas);
        }

        // 之前选中的节目已不在窗口内（播完了或被过滤掉）：清空详情区，不留过期内容
        if (selUrl != null && _guideSelectedBlock == null)
        {
            _guideSelectedChannel = null;
            _guideSelectedProgram = null;
            GuideDetail.Visibility = Visibility.Collapsed;
            GuideDetailEmpty.Visibility = Visibility.Visible;
            GuideDetailEmpty.Text = "点击上方节目块查看详情";
        }

        // ---- 现在竖线 ----
        var nowX = (now - _guideWindowStart).TotalMinutes * GuidePxPerMinute;
        Canvas.SetLeft(GuideNowLine, nowX);
        GuideNowLine.Height = GuideRulerHeight + _guideChannels.Count * GuideRowHeight;
        GuideNowLine.Visibility = Visibility.Visible;

        // 空态提示（写在详情区）：没有 EPG，或当前分组/搜索下没有带节目单的频道
        if (_guideChannels.Count == 0)
        {
            GuideDetail.Visibility = Visibility.Collapsed;
            GuideDetailEmpty.Visibility = Visibility.Visible;
            GuideDetailEmpty.Text = _epg.Count == 0
                ? "节目单尚未加载：请先在播放列表里配置 EPG 地址（或等 EPG 加载完成）"
                : "当前分组 / 搜索下没有带节目单的频道";
        }
        else if (_guideSelectedProgram is null)
        {
            GuideDetailEmpty.Text = "点击上方节目块查看详情";
        }
    }

    /// <summary>点节目块：高亮该块，下方详情区显示频道/节目/简介（提醒/录制/回放本应用没有对应功能，只显示能做的“看直播”）</summary>
    private void GuideProgram_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border block || block.Tag is not (ChannelItem ch, EpgProgram prog)) return;
        ShowGuideDetail(ch, prog, block);
        e.Handled = true;
    }

    private void ShowGuideDetail(ChannelItem ch, EpgProgram prog, Border block)
    {
        if (_guideSelectedBlock != null && _guideSelectedBlock != block) RestoreGuideBlockBorder(_guideSelectedBlock);
        _guideSelectedBlock = block;
        block.BorderBrush = ThemeBrush("AccentBrush", Brushes.DodgerBlue);
        block.BorderThickness = new Thickness(2);
        _guideSelectedChannel = ch;
        _guideSelectedProgram = prog;
        var now = DateTime.Now;
        var status = prog.Start <= now && now < prog.Stop ? "正在播出"
            : prog.Start > now ? "即将播出" : "已结束";
        GuideDetailTitle.Text = prog.Title;
        GuideDetailMeta.Text = $"{ch.Number}  {ch.Name}  ·  {prog.TimeRange}  ·  {status}";
        GuideDetailDesc.Text = string.IsNullOrWhiteSpace(prog.Description) ? "暂无节目简介" : prog.Description.Trim();
        GuideDetailEmpty.Visibility = Visibility.Collapsed;
        GuideDetail.Visibility = Visibility.Visible;
        GuideDetailPlay.Tag = ch;
    }

    /// <summary>恢复节目块的默认描边（正在播：蓝色细边；其他：主题边框色细边）</summary>
    private void RestoreGuideBlockBorder(Border b)
    {
        if (b.Tag is not (ChannelItem, EpgProgram prog)) return;
        var now = DateTime.Now;
        var isNow = prog.Start <= now && now < prog.Stop;
        b.BorderBrush = ThemeBrush(isNow ? "AccentBrush" : "BorderBrush", Brushes.Gray);
        b.BorderThickness = new Thickness(1);
    }

    /// <summary>详情区“看直播”：换到该频道并关闭节目表</summary>
    private void GuideDetailPlay_Click(object sender, RoutedEventArgs e)
    {
        if (GuideDetailPlay.Tag is not ChannelItem ch) return;
        SelectInList(ch);
        CloseGuide();
        _ = PlayAsync(ch);
    }

    /// <summary>点频道列：直接播放该频道并关闭节目表</summary>
    private void GuideChannel_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border row || row.Tag is not ChannelItem ch) return;
        SelectInList(ch);
        CloseGuide();
        _ = PlayAsync(ch);
    }

    /// <summary>加载 Xtream Codes 频道列表：通过 M3U 输出接口获取</summary>
    private async Task LoadXtreamAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.XtreamServer) ||
            string.IsNullOrEmpty(_config.XtreamUsername) ||
            string.IsNullOrEmpty(_config.XtreamPassword)) return;

        var server = _config.XtreamServer.TrimEnd('/');
        var url = $"{server}/get.php?username={Uri.EscapeDataString(_config.XtreamUsername)}" +
                  $"&password={Uri.EscapeDataString(_config.XtreamPassword)}&type=live&output=m3u8";
        try
        {
            PlayState.Text = "加载 Xtream…";
            var text = await FetchTextAsync(url, ct);
            var list = await Task.Run(() => M3uParser.Parse(text), ct);
            if (list.Count > 0)
            {
                AdoptChannels(list);
                _loadedSourceUrl = $"{server}/get.php?type=live&output=m3u8";
                _config.ActivePlaylist = "Xtream";
                // F7：追加前先按 Name 去重，保证 Xtream 项恰为 1。
                _config.Playlists.RemoveAll(p => p.Name == "Xtream");
                _config.Playlists.Add(new PlaylistSource { Name = "Xtream", Url = _loadedSourceUrl });
                SaveConfigAsync();
                // 深查第 14 轮：改动播放列表的各处（增/删/改名/切换）都会 RefreshPlaylistCombo，
                // 唯独本处漏了——Xtream 项已入库，源列表面板仍显示旧内容（空），要点开收起才刷新。
                RefreshPlaylistCombo();
                PlayState.Text = $"已载入 {list.Count} 个频道（Xtream）";
                AppLog.Info($"xtream loaded {list.Count} channels");
            }
            else PlayState.Text = "Xtream 无频道";
        }
        catch (Exception ex)
        {
            PlayState.Text = "Xtream 加载失败";
            AppLog.Error($"xtream load failed: {ex.Message}");
        }
    }

    /// <summary>应用主题（深色/浅色）：加载 Themes/*.xaml 资源字典。
    /// L11：主题是松散 Content 文件，缺失/损坏时加载抛错——先加载成功再替换旧字典，
    /// 失败时保持当前主题（启动时即 WPF 默认配色）并记日志，不崩溃。</summary>
    private void ApplyTheme(string theme)
    {
        var isDark = theme != "浅色";
        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.Where(d => d.Source != null && d.Source.OriginalString.Contains("Themes/")).ToList();
        try
        {
            // 先加载：ResourceDictionary 的 Source 在设置时立即解析文件，缺失即抛错。
            var dict = new ResourceDictionary
            {
                Source = new Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
            };
            // 加载成功后再替换旧字典，失败时旧主题保持生效。
            foreach (var d in old) merged.Remove(d);
            merged.Add(dict);
        }
        catch (Exception ex)
        {
            AppLog.Error($"apply theme failed ({theme}): {ex.Message}，保持当前主题");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_filtered.Count > 0)
            {
                var first = _filtered[0];
                SelectInList(first);
                _ = PlayAsync(first);
            }
            ChannelList.Focus();
            e.Handled = true;
        }
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressEvents) ApplyFilter();
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelItem ch) _ = PlayAsync(ch);
    }

    private void MiFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelItem ch) ToggleFavorite(ch);
    }

    /// <summary>频道列表项星标点击：直接收藏/取消收藏（更直观的交互）</summary>
    private void FavStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelItem ch }) ToggleFavorite(ch);
        e.Handled = true;   // 阻止冒泡触发列表项选中/双击
    }

    private void MiCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelItem ch)
        {
            try { Clipboard.SetText(ch.Url); } catch (Exception ex) { AppLog.Error($"copy url failed: {ex.Message}"); }
        }
    }

    /// <summary>隐藏当前频道（从列表移除，可恢复）</summary>
    private void MiHide_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelItem ch) return;
        if (!_config.HiddenChannels.Contains(ch.Url)) _config.HiddenChannels.Add(ch.Url);
        QueueSave();
        ApplyFilter();
        AppLog.Info($"hide channel [{ch.Name}]");
    }

    private void SortByNumber_Click(object sender, RoutedEventArgs e)
    {
        _sortMode = "号码";
        SortByNumber.IsChecked = true;
        SortByName.IsChecked = false;
        ApplyFilter();
    }

    private void SortByName_Click(object sender, RoutedEventArgs e)
    {
        _sortMode = "名称";
        SortByNumber.IsChecked = false;
        SortByName.IsChecked = true;
        ApplyFilter();
    }

    /// <summary>分组管理：重命名 / 删除分组</summary>
    private void BtnManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var groups = _all.Select(c => c.Group).Distinct().OrderBy(g => g, StringComparer.CurrentCulture).ToList();
        // 深查第 9 轮：必须设 Owner，原因同 BtnSettings_Click（模态守卫只查 OwnedWindows）。
        var dlg = new Dialogs.GroupManagerWindow(groups) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        switch (dlg.Action)
        {
            case "rename" when dlg.SelectedGroup != null && !string.IsNullOrWhiteSpace(dlg.NewName):
                var oldName = dlg.SelectedGroup;
                var newName = dlg.NewName.Trim();
                if (oldName == newName) return;
                foreach (var c in _all.Where(c => c.Group == oldName)) c.Group = newName;
                RebuildGroups();
                ApplyFilter();
                QueueSave();
                break;
            case "delete" when dlg.SelectedGroup != null:
                var delName = dlg.SelectedGroup;
                foreach (var c in _all.Where(c => c.Group == delName)) c.Group = "未分组";
                RebuildGroups();
                ApplyFilter();
                QueueSave();
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = HandleGlobalKey(e.Key);
    }

    /// <summary>全局快捷键统一处理：消息层拦截与 WPF 路由共用，焦点在 VLC 原生窗口时也生效</summary>
    private bool HandleGlobalKey(Key key)
    {
        if (Keyboard.FocusedElement is TextBox or MenuItem or ComboBox) return false; // 输入/菜单/下拉中不接管

        // 数字键换台（V1.4）：主键盘 + 小键盘，无修饰键时拦截
        var k = (int)key;
        if (Keyboard.Modifiers == ModifierKeys.None && k is >= (int)Key.D0 and <= (int)Key.D9)
        {
            HandleDigit((char)('0' + (k - (int)Key.D0)));
            return true;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && k is >= (int)Key.NumPad0 and <= (int)Key.NumPad9)
        {
            HandleDigit((char)('0' + (k - (int)Key.NumPad0)));
            return true;
        }

        switch (key)
        {
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                ToggleSidebar();
                return true;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                ResetLayout();
                return true;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None && _filtered.Count > 0:
                MoveChannel(-1);
                return true;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None && _filtered.Count > 0:
                MoveChannel(1);
                return true;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            case Key.F11:
            case Key.F when Keyboard.Modifiers == ModifierKeys.None:
                // 小窗在前台时全屏的是小窗本身：主窗口播停了、画面已断开，全屏主窗口只会看到黑屏
                if (_pipWindow != null && _pipWindow.IsActive && !_fullscreen)
                {
                    _pipWindow.TogglePipFullscreen();
                    return true;
                }
                ToggleFullscreen();
                return true;
            case Key.Escape when GuideView.Visibility == Visibility.Visible:
                CloseGuide();
                return true;
            case Key.Escape when _pipWindow is { } pipEsc && pipEsc.IsPipFullscreen:
                pipEsc.TogglePipFullscreen();
                return true;
            case Key.Escape when _fullscreen:
                ToggleFullscreen();
                return true;
            case Key.OemPlus:
            case Key.Add:
                AdjustVolume(5);
                return true;
            case Key.OemMinus:
            case Key.Subtract:
                AdjustVolume(-5);
                return true;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.Control:
                AdjustVolume(5);
                return true;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.Control:
                AdjustVolume(-5);
                return true;
            case Key.Enter when ChannelList.SelectedItem is ChannelItem ch:
                _ = PlayAsync(ch);
                return true;
            case Key.Space when _player != null:
                TogglePlayPause();
                return true;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None && _player != null:
                StopPlayback();
                return true;
            case Key.OemOpenBrackets when Keyboard.Modifiers == ModifierKeys.Control:
                SetPlaybackRate(_playbackRate - 0.25f);
                return true;
            case Key.OemCloseBrackets when Keyboard.Modifiers == ModifierKeys.Control:
                SetPlaybackRate(_playbackRate + 0.25f);
                return true;
            case Key.D0 when Keyboard.Modifiers == ModifierKeys.Control:
                SetPlaybackRate(1.0f);
                return true;
        }
        return false;
    }

    private void MoveChannel(int delta)
    {
        // P1：用 ApplyFilter 缓存的 _filtered，不再每按键 ToList 全量枚举。
        if (_filtered.Count == 0) return;
        int idx;
        if (ChannelList.SelectedIndex < 0)
            idx = delta > 0 ? 0 : _filtered.Count - 1;  // 无选中：↓从第1个开始，↑从最后1个开始
        else
            idx = (ChannelList.SelectedIndex + delta + _filtered.Count) % _filtered.Count;
        var ch = _filtered[idx];
        ChannelList.SelectedItem = ch;
        ChannelList.ScrollIntoView(ch);
        NowPlaying.Text = ch.Name;             // 选中/滚动/名称即时反馈
        _pendingPlay = ch;                     // 防抖 150ms：连按时只播最后一个
        _switchDebounce.Stop();
        _switchDebounce.Start();
        ShowOsd();
        ShowChannelOsd(ch);                    // V1.4：换台 OSD 即时预览
    }

    // ---------- 坏源容错 ----------

    /// <summary>直播流意外播完（v1.4.16）：HLS 窗口耗尽或 playlist 刷新失败时 libvlc 停在这里；
    /// 点播文件播完是正常的，不重连；直播则自动重连同一频道，不用用户手动重进。</summary>
    private void OnPlayerEndReached()
    {
        if (_closing || _isPaused || _pipEffectActive) return;
        if (_current is not { } ch) return;
        if (!IsLiveStream(ch.Url)) return;
        AppLog.Error($"live stream ended unexpectedly, reconnect: [{ch.Name}]");
        PlayState.Text = "直播中断，自动重连…";
        TryAutoReconnect(ch);
    }

    /// <summary>自动重连同一频道（v1.4.16）：EndReached / 意外停播共用；连续 5 次都没播起来
    /// 就停手（流可能真死了），显示文案等用户手动处理，避免无限循环重播。</summary>
    private bool TryAutoReconnect(ChannelItem ch)
    {
        if (_closing || _isPaused || _pipEffectActive) return false;
        if (++_autoReconnectCount > 5)
        {
            PlayState.Text = "连续失败，请手动重进";
            AppLog.Error($"auto-reconnect stopped after limit: [{ch.Name}]");
            return false;
        }
        _ = PlayAsync(ch, true);
        return true;
    }

    /// <summary>播放出错处理：优先切同频道备用线路；无备用先重连同一频道（3 次），再失败才跳下一个频道</summary>
    private void HandlePlayError()
    {
        if (_closing || _current is not { } ch) return;
        // 同一次失败去重：800ms 内同一 URL 的重复 EncounteredError 直接丢弃。
        var now = DateTime.UtcNow;
        if (_lastPlayErrorUrl == ch.Url && (now - _lastPlayErrorUtc).TotalMilliseconds < 800) return;
        _lastPlayErrorUtc = now;
        _lastPlayErrorUrl = ch.Url;
        _recentFailedUrls.Add(ch.Url);
        ++_failCount;

        var backup = FindBackupChannel(ch);
        var hasBackup = backup is not null && !_recentFailedUrls.Contains(backup.Url);
        switch (DecideRecovery(_failCount, hasBackup))
        {
            case RecoveryAction.Stop:
                PlayState.Text = "连续失败，已停止自动换台";
                AppLog.Error($"auto-skip stopped after limit: [{ch.Name}]");
                return;
            case RecoveryAction.UseBackup:
                PlayState.Text = "线路失败，切备用…";
                AppLog.Info($"switch to backup [{backup!.Name}]");
                _ = PlayAsync(backup);
                return;
            case RecoveryAction.RetrySame:
                // v1.4.16：无备用时先重连同一频道——直播闪断常见，直接跳台是错误体验；
                // 3 次还不行再跳台（SkipNext）。Playing 事件会清零 _failCount，长期播得住的台不会累积到跳台。
                PlayState.Text = "播放出错，自动重连…";
                AppLog.Error($"channel error, reconnect same: [{ch.Name}] {AppConfig.RedactUrlForLog(ch.Url)}");
                _ = PlayAsync(ch, true);
                return;
            default: // SkipNext
                PlayState.Text = "播放出错，自动跳台…";
                AppLog.Error($"channel error, auto-skip: [{ch.Name}] {AppConfig.RedactUrlForLog(ch.Url)}");
                _ = AutoSkipAsync(ch);
                return;
        }
    }

    private async Task AutoSkipAsync(ChannelItem failed)
    {
        await Task.Delay(1200);                     // 留缓冲，避免瞬间跳走
        if (_closing) return;                      // 窗口已关闭：不再操作 UI
        if (_current?.Url != failed.Url) return;    // 用户已手动换台
        if (_player is { IsPlaying: true }) return; // 事件竞态：实际已恢复播放
        MoveChannel(1);
    }

    /// <summary>按频道名配对备用线路（去掉常见备用标记后名称相同的另一条 URL）</summary>
    private ChannelItem? FindBackupChannel(ChannelItem failed)
    {
        // P3：查 AdoptChannels 预建的表；同名多条时取第一条非自身的，与原 FirstOrDefault 语义一致。
        if (!_backupByName.TryGetValue(NormalizeName(failed.Name), out var bucket)) return null;
        return bucket.FirstOrDefault(c => c.Url != failed.Url);
    }

    private static string NormalizeName(string name) => name
        .Replace("(SCF备用)", "", StringComparison.OrdinalIgnoreCase)
        .Replace("(备用)", "", StringComparison.OrdinalIgnoreCase)
        .Replace("-备用", "", StringComparison.OrdinalIgnoreCase)
        .Trim();

    /// <summary>全屏时临时显示状态栏/控制浮层（3 秒后自动隐藏）</summary>
    private void ShowOsd()
    {
        if (!_fullscreen) return;
        StatusBar.Visibility = Visibility.Visible;
        ShowFullscreenOverlay();
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    /// <summary>直播停滞看门狗：每 4 秒检查播放位置，IsPlaying 但连续 2 次（8 秒）不前进则自动重连（上限 3 次）；
    /// v1.4.16 增加意外停播分支：播完/停在 Stopped/Error 态也不再是死角，直播流自动重连。</summary>
    private void OnStallWatchTick(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (_player is null) return;
        // v1.4.16 意外停播：播放器停在 Stopped/Ended/Error 态、当前频道是直播流、不是暂停/
        // 小窗效果 → 自动重连。原来 !IsPlaying 就直接 return，播完/停住只能手动重进。
        // 连续 2 次（8 秒）仍停才动手，避开启动/切台/停止的瞬间状态；小窗效果时主播放器
        // 是故意停着的，必须排除（_pipEffectActive）。
        if (!_player.IsPlaying && _current != null && !_isPaused && !_pipEffectActive
            && (_player.State == VLCState.Stopped || _player.State == VLCState.Ended || _player.State == VLCState.Error)
            && IsLiveStream(_current.Url))
        {
            _stallTicks = 0; _lastMediaTime = -1;
            if (++_stopTicks >= 2)
            {
                _stopTicks = 0;
                PlayState.Text = "直播中断，自动重连…";
                AppLog.Info($"unexpected stop detected, reconnect: [{_current.Name}]");
                TryAutoReconnect(_current);
            }
            return;
        }
        _stopTicks = 0;
        if (!_player.IsPlaying) { _stallTicks = 0; _lastMediaTime = -1; return; } // 未播/暂停不监控
        var t = _player.Time;
        if (t <= 0) { _stallTicks = 0; _lastMediaTime = -1; return; }   // 部分流 Time 恒 0，跳过监控防误判

        if (t == _lastMediaTime)
        {
            if (++_stallTicks >= 2)
            {
                _stallTicks = 0;
                if (_current is null) return;
                _lastStallAt = DateTime.Now;
                if (_stallRetryCount < 3)
                {
                    _stallRetryCount++;
                    PlayState.Text = "直播停滞，自动重连…";
                    AppLog.Info($"stall detected, reconnect #{_stallRetryCount}: [{_current.Name}]");
                    _ = PlayAsync(_current, true);
                }
                else
                {
                    if (PlayState.Text != "直播停滞")   // 放弃状态只提示一次，避免重复刷日志
                    {
                        PlayState.Text = "直播停滞";
                        AppLog.Error($"stall persists after 3 reconnects: [{_current.Name}]");
                    }
                }
            }
        }
        else
        {
            _stallTicks = 0;          // 播放正常前进
            // 停滞 1 分钟后仍在正常播放：流已真正恢复，看门狗计数清零
            if (_stallRetryCount > 0 && (DateTime.Now - _lastStallAt).TotalMinutes > 1)
                _stallRetryCount = 0;
        }
        _lastMediaTime = t;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        App.IsShuttingDown = true;          // v1.4.15 迭代 2：退出期间全局异常不再弹窗挡关闭
        _closing = true;                    // 挂起回调（自动跳台/防抖/看门狗）不再操作 UI
        UninstallMouseHook();               // 先卸底层鼠标钩子，关闭过程不再收鼠标消息
        SaveWindowPlacement();              // V1.4：记忆窗口位置/尺寸
        // v1.4.15 1-2：删掉 _playGate.Wait(2s)——PlayAsync 的释放续体需回 UI 线程执行，
        // Window_Closing 正阻塞 UI 线程等这把锁，换台进行中关 App 必冻满 2 秒才超时；
        // _closing 守卫已能拦住关闭后的后续 UI 操作（PlayAsync 释放锁后有 if (_closing) return），
        // 等待本身没有任何保护作用。
        _netCts.Cancel();                  // 取消挂起的网络拉取（播放列表/EPG/Xtream）
        _stallWatch.Stop();
        _progressTimer.Stop();              // L1：此前漏停，关闭后仍每 500ms 读已释放播放器的 Length/Time
        _switchDebounce.Stop();
        _osdTimer.Stop();
        _fsHideTimer.Stop();
        _searchDebounce.Stop();
        _saveDebounce.Stop();
        _digitTimer.Stop();
        _osdHideTimer.Stop();
        _epgRefreshTimer.Stop();
        // 取消订阅静态事件，避免窗口对象被静态引用导致无法回收（内存泄漏）
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        Activated -= OnWindowActivated;

        // 先关闭所有独立频道窗口：它们在自己的 Window_Closing 里后台释放播放器；
        // 主窗口不再释放 LibVLC 引擎（随进程退出回收），不存在“先释引擎后关子窗口崩溃”的问题。
        foreach (var win in _channelWindows.ToList())
        {
            try { win.Close(); } catch { }
        }
        _channelWindows.Clear();

        // 关闭独立悬浮小窗（同款：自己后台释放播放器）
        if (_pipWindow != null)
        {
            try { _pipWindow.Close(); } catch { }
            _pipWindow = null;
        }

        // v1.4.15 1-1：主播放器释放放后台——先在 UI 线程断开视频面（快操作），
        // 再由 StartMainTeardown 在后台做 Stop → Dispose Media → Dispose Player；
        // Window_Closed 里同步 Dispose 会等卡住的直播流拆掉（和 v1.4.12/1.4.13 关小窗
        // 卡死同一根因），进程退出时后台释放不用等，系统回收。
        try { VideoView.MediaPlayer = null; } catch { }
        StartMainTeardown();

        // v1.4.15 迭代 3：退出时的最后一次保存仍同步（关掉进程前把本次改动落盘），
        // 但读盘失败待确认时跳过——Save 会弹确认框挡住退出，且用默认配置覆盖好文件
        // 正是待确认要防的事；跳过本次保存，配置以本次会话前的落盘为准。
        if (!AppConfig.ReadFailurePending) _config.Save();
        AppLog.Info("exit");
    }

    /// <summary>
    /// 后台释放主播放器：Stop → Dispose Media → Dispose Player 全部在后台线程做，
    /// UI 线程绝不碰播放器释放（v1.4.15 1-1）。与后台 Stop（停止/切源/小窗效果）
    /// 用同一把 _mainPlayerLock 串行化，避免 Stop 与 Dispose 在同一个 native 播放器上竞态。
    /// LibVLC 不在这里释放：子窗口播放器共享同一引擎，进程退出时由系统回收；
    /// 在后台释放引擎可能与子窗口的释放竞态导致退出崩溃。
    /// </summary>
    private void StartMainTeardown()
    {
        if (_mainTeardownStarted) return;  // 只启动一次
        _mainTeardownStarted = true;
        var playerToStop = _player;
        if (playerToStop is null) return;
        Media? mediaToDispose;
        lock (_mediaLock) { mediaToDispose = _currentMedia; }
        _ = Task.Run(() =>
        {
            try
            {
                lock (_mainPlayerLock)
                {
                    try { playerToStop.Stop(); } catch { }
                    try { mediaToDispose?.Dispose(); } catch { }
                    try { playerToStop.Dispose(); } catch { }
                }
                AppLog.Info("main player released on background thread");
            }
            catch (Exception ex) { AppLog.Error($"main player release failed: {ex.Message}"); }
        });
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        // 主播放器释放已交给后台 StartMainTeardown：UI 线程在这里 Dispose 会阻塞
        // （等卡住的流拆掉），就是 v1.4.12/1.4.13 关小窗卡死的同款根因；
        // LibVLC 随进程退出由系统回收，不再同步释放。
        _http.Dispose();
        // L10：释放其余可释放资源（_netCts 已在 Window_Closing 中 Cancel，此处释放安全；
        // 信号量释放会使仍在等待的调用以 ObjectDisposedException 醒来，各调用方已按关闭处理）。
        try { _netCts.Dispose(); } catch { }
        try { _engineGate.Dispose(); } catch { }
        try { _loadGate.Dispose(); } catch { }
        try { _playGate.Dispose(); } catch { }
    }
}
