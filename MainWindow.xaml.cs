using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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
    // 频道列表视图：用 ICollectionView.Filter 过滤/排序，避免每次搜索/切分组全量重建列表
    private ICollectionView _view = CollectionViewSource.GetDefaultView(new List<ChannelItem>());

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
    // 直播停滞看门狗：IsPlaying 但 8 秒播放位置不前进 → 自动重连（上限 3 次）
    private readonly DispatcherTimer _stallWatch = new() { Interval = TimeSpan.FromSeconds(4) };
    private long _lastMediaTime = -1;
    private int _stallTicks;
    private int _stallRetryCount;
    private DateTime _lastStallAt = DateTime.MinValue;
    private bool _closing;

    // 独立悬浮小窗（画中画）：无边框窗口，可拖到屏幕任意位置
    private PipWindow? _pipWindow;
    private ChannelItem? _pipChannel;

    // 多窗口播放：跟踪所有打开的独立频道窗口，主窗口关闭时统一关闭，避免引擎释放后子窗口崩溃
    private readonly List<ChannelWindow> _channelWindows = new();

    // 播放列表加载并发保护：快速切换源/刷新时避免并发覆盖 _all 与 UI 竞态
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _loadSeq;   // 加载序号：只采纳最后一次请求的结果，防止旧结果覆盖新结果

    // 网络请求取消：窗口关闭时取消挂起的播放列表/EPG/Xtream 拉取，避免后台线程继续占用
    private readonly CancellationTokenSource _netCts = new();

    // 播放串行化：libVLC MediaPlayer 非线程安全，快速换台时多个 Play 需串行执行，避免并发崩溃
    private readonly SemaphoreSlim _playGate = new(1, 1);

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

    public MainWindow()
    {
        InitializeComponent();
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _config.Save(); };
        _switchDebounce.Tick += (_, _) =>
        {
            _switchDebounce.Stop();
            if (_closing) return;
            if (_pendingPlay is not { } ch) return;
            _pendingPlay = null;
            _ = PlayAsync(ch);
        };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); if (_fullscreen) StatusBar.Visibility = Visibility.Collapsed; };
        _fsHideTimer.Tick += (_, _) => { _fsHideTimer.Stop(); FullscreenOverlay.Visibility = Visibility.Collapsed; };
        _stallWatch.Tick += OnStallWatchTick;
        _progressTimer.Tick += (_, _) => UpdateProgress();

        // 焦点守护：窗口激活/VLC原生窗口拖走焦点时，把键盘焦点还给频道列表，保证↑↓换台始终可用
        Activated += OnWindowActivated;

        // 线程消息层拦截方向键：无论焦点在 WPF 还是 VLC 原生窗口（HwndHost），↑↓换台都生效
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
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
        if (msg.message != WM_KEYDOWN || handled) return;
        // 注：不检查 IsActive——焦点/激活可能落在 VLC 顶层原生窗口上，主窗口 IsActive 为 false；
        // ThreadFilterMessage 只会收到本线程窗口的消息，天然不会拦截其他应用的按键
        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        if (HandleGlobalKey(key)) handled = true;
    }

    // ---------- 启动 ----------

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info($"start v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        _config = AppConfig.Load();
        VolumeText.Text = $"音量 {_config.Volume}%";
        _aspectRatio = _config.AspectRatio ?? "";
        _playbackRate = (float)(_config.PlaybackRate > 0 ? _config.PlaybackRate : 1.0);
        RefreshPlaylistCombo();
        _ = EnsureEngineAsync();          // 后台初始化，不阻塞界面
        _progressTimer.Start();           // 进度条/时间刷新
        await LoadPlaylistAsync(false, CancellationToken.None);   // 缓存优先秒开，再后台刷新
        ChannelList.Focus();              // 启动焦点给频道列表（否则默认落在搜索框，方向键失效）
    }

    private async Task EnsureEngineAsync()
    {
        if (_engineReady || _engineFailed) return;   // 已就绪或已确认失败，不再重试
        await _engineGate.WaitAsync();
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
            VideoView.MediaPlayer = _player;
            _engineReady = true;
            _stallWatch.Start();
            AppLog.Info($"engine ready libVLC {_libvlc.Version}");
            if (PlayState.Text == "引擎初始化…") PlayState.Text = "就绪";
        }
        catch (Exception ex)
        {
            _engineFailed = true;            // 标记失败，后续不再反复初始化/弹窗
            PlayState.Text = "引擎初始化失败";
            AppLog.Error("engine init failed: " + ex.Message);
            MessageBox.Show(this, "VLC 引擎初始化失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _engineGate.Release(); }
    }

    // ---------- 频道数据 ----------

    private async Task LoadPlaylistAsync(bool forceRefresh, CancellationToken ct)
    {
        var seq = ++_loadSeq;   // 本次请求序号
        await _loadGate.WaitAsync(ct);
        try
        {
            // 若等待期间又有更新的加载请求，本次结果作废
            if (seq != _loadSeq) return;
            await LoadCoreAsync(forceRefresh, ct);
        }
        finally { _loadGate.Release(); }
    }

    private async Task LoadCoreAsync(bool forceRefresh, CancellationToken ct)
    {
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
        if (!forceRefresh && _all.Count == 0)
        {
            var cached = await Task.Run(() => ChannelCache.Load(src.Url));
            if (cached.Count > 0)
            {
                AdoptChannels(cached);
                PlayState.Text = $"已载入 {cached.Count} 个频道（缓存）";
                await ResumeLastAsync();
            }
        }

        // 2) 网络刷新
        try
        {
            var text = await FetchTextAsync(src.Url, ct);
            var list = await Task.Run(() => M3uParser.Parse(text), ct);
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
                _ = Task.Run(() => ChannelCache.Save(src.Url, list));   // 后台写盘，大清单不卡 UI
                PlayState.Text = $"已载入 {list.Count} 个频道";
                await ResumeLastAsync();
                // 尝试从 M3U 头部解析 EPG 源并加载节目单
                var epgUrl = ExtractEpgUrl(text);
                if (epgUrl != null && epgUrl != _epgUrl)
                {
                    _epgUrl = epgUrl;
                    _ = LoadEpgAsync(epgUrl, ct);
                }
            }
            else if (_all.Count == 0)
            {
                PlayState.Text = "播放列表为空";
            }
        }
        catch (Exception ex)
        {
            if (_all.Count == 0) PlayState.Text = "加载失败: " + ex.Message;
            else PlayState.Text = "刷新失败，使用缓存";
        }
    }

    private void AdoptChannels(List<ChannelItem> list)
    {
        var favs = _config.Favorites.ToHashSet();
        foreach (var c in list) c.IsFavorite = favs.Contains(c.Url);

        var keepUrl = _current?.Url;
        _all = list;
        _view = CollectionViewSource.GetDefaultView(_all);   // 重新绑定视图（_all 被整体替换）
        ChannelList.ItemsSource = _view;
        RebuildGroups();
        ApplyFilter();
        EmptyState.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (keepUrl != null)
        {
            var again = _all.FirstOrDefault(c => c.Url == keepUrl);
            if (again != null) { _current = again; SelectInList(again); }
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

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 网络抖动容错：最多重试 2 次（间隔 1s），仅对瞬时失败重试
            for (var attempt = 0; ; attempt++)
            {
                try { return await _http.GetStringAsync(AppConfig.ResolveXtreamUrl(url, _config.XtreamServer, _config.XtreamUsername, _config.XtreamPassword), ct); }
                catch (Exception ex) when (attempt < 2)
                {
                    AppLog.Error($"fetch retry {attempt + 1} [{AppConfig.RedactUrlForLog(url)}]: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }
        return await File.ReadAllTextAsync(url, ct);
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

        CountText.Text = $"{_view.Cast<ChannelItem>().Count()} 个频道";
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
            _lastStallAt = DateTime.MinValue;
            _failCount = 0;                  // 用户手动换台：重置坏源失败链，避免永久停止自动跳台
            _recentFailedUrls.Clear();
        }
        _lastMediaTime = -1;                  // 停滞看门狗重新计位
        _stallTicks = 0;

        _current = ch;
        NowPlaying.Text = ch.Name;
        PlayState.Text = "连接中…";
        EmptyState.Visibility = Visibility.Collapsed;
        ShowOsd();
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
                await Task.Run(() =>
                {
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
                });
            }
            finally { _playGate.Release(); }
        }
        catch (Exception ex)
        {
            PlayState.Text = "播放失败";
            AppLog.Error($"play failed [{ch.Name}] {AppConfig.RedactUrlForLog(url)}: {ex.Message}");
            return;                            // 失败不写断点、不抢焦点
        }
        AppLog.Info($"play [{ch.Name}] {AppConfig.RedactUrlForLog(url)}");

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
        if (_player is null) return;
        try
        {
            _player.Stop();
            _current = null;
            _isPaused = false;
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
            var len = _player.Length;
            var t = _player.Time;
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

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _sideWidth = SideCol.Width;
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
        }
        else
        {
            SideCol.Width = _sideWidth;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            _fullscreen = false;
            _osdTimer.Stop();
            _fsHideTimer.Stop();
            MouseMove -= Fullscreen_MouseMove;
            FullscreenOverlay.Visibility = Visibility.Collapsed; // 退出全屏隐藏浮层
            StatusBar.Visibility = Visibility.Visible;    // 退出全屏恢复状态栏
            SideToggleBtn.Visibility = Visibility.Visible; // 恢复侧栏按钮
            if (_pipWindow != null && _pipChannel != null) _pipWindow.Visibility = Visibility.Visible; // 恢复小窗
        }
    }

    /// <summary>全屏鼠标移动：显示控制浮层并重置隐藏计时</summary>
    private void Fullscreen_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        ShowFullscreenOverlay();
    }

    /// <summary>显示全屏控制浮层并更新当前频道名，3 秒后自动隐藏</summary>
    private void ShowFullscreenOverlay()
    {
        if (!_fullscreen) return;
        FsNowPlaying.Text = _current?.Name ?? "未播放";
        FsPlayPause.Content = _isPaused ? "▶" : "⏸";
        FullscreenOverlay.Visibility = Visibility.Visible;
        _fsHideTimer.Stop();
        _fsHideTimer.Start();
    }

    // ---------- 侧栏收起/展开 ----------

    /// <summary>侧栏边缘按钮：收起/展开侧栏</summary>
    private void SideToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    /// <summary>收起/展开侧栏：收起时缩进最左，视频区占满</summary>
    private void ToggleSidebar()
    {
        if (_sideCollapsed)
        {
            // 展开：恢复之前宽度
            SideCol.Width = _sideWidth;
            _sideCollapsed = false;
            SideToggleBtn.SetValue(TextBlock.TextProperty, "◀");
        }
        else
        {
            // 收起：记录当前宽度，缩进最左
            _sideWidth = SideCol.Width;
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
            SideCol.Width = _sideWidth;
            _sideCollapsed = false;
            SideToggleBtn.SetValue(TextBlock.TextProperty, "◀");
        }
        // 恢复状态栏
        StatusBar.Visibility = Visibility.Visible;
        AppLog.Info("layout reset");
    }

    // ---------- 小窗播放（画中画） ----------

    private void BtnPip_Click(object sender, RoutedEventArgs e)
    {
        // 工具栏小窗按钮：若小窗开着则关闭，否则小窗播放当前选中/正在播放的频道
        if (_pipWindow != null && _pipWindow.IsVisible) { ClosePip(); return; }
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
        if (_libvlc is null) return;
        var win = new ChannelWindow(_libvlc, _config.Volume) { Owner = this };
        _channelWindows.Add(win);
        win.Closed += (_, _) => _channelWindows.Remove(win);
        win.Show();
        win.Play(ch);
    }

    /// <summary>打开小窗播放指定频道；小窗模式下自动收起节目列表（侧栏）</summary>
    private async void OpenPip(ChannelItem ch)
    {
        if (_closing) return;
        await EnsureEngineAsync();
        if (_libvlc is null) return;

        // 小窗播放时自动隐藏节目列表（侧栏收起），腾出视频区
        if (SideCol.Width.Value > 0)
        {
            _sideWidth = SideCol.Width;
            SideCol.Width = new GridLength(0);
        }

        _pipChannel = ch;

        if (_pipWindow == null)
        {
            _pipWindow = new PipWindow(_libvlc, _config.Volume) { Owner = this };
            _pipWindow.Closed += (_, _) =>
            {
                _pipWindow = null;
                _pipChannel = null;
                // 小窗关闭后恢复侧栏
                if (!_fullscreen) SideCol.Width = _sideWidth;
            };
            _pipWindow.Show();
        }
        else if (!_pipWindow.IsVisible)
        {
            _pipWindow.Show();
        }

        _pipWindow.Play(ch);
        AppLog.Info($"pip play [{ch.Name}]");
    }

    private void ClosePip()
    {
        if (_pipWindow != null)
        {
            try { _pipWindow.DetachEngine(); _pipWindow.Close(); } catch { }
            _pipWindow = null;
        }
        _pipChannel = null;
        // 恢复侧栏
        if (!_fullscreen) SideCol.Width = _sideWidth;
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
        _config.Save();
        // 切换源：停掉旧频道（后台 Stop，避免 VLC 阻塞 UI 导致卡死），清空播放状态
        var p = _player;
        if (p is { IsPlaying: true }) _ = Task.Run(() => { try { p.Stop(); } catch { } });
        _current = null;
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
            _config.Save();
            var p = _player;
            if (p is { IsPlaying: true }) _ = Task.Run(() => { try { p.Stop(); } catch { } });
            _current = null;
            NowPlaying.Text = "未播放";
            PlayState.Text = "未播放";
            _ = LoadPlaylistAsync(false, _netCts.Token);
        }
        else _config.Save();
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
        _config.Save();
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
                existing.Name = EnsureUniqueName(name, existing);
            }
            else
            {
                name = EnsureUniqueName(name, null);
                _config.Playlists.Add(new PlaylistSource { Name = name, Url = dlg.SourceUrl });
            }
            _config.Save();
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
        var dlg = new Dialogs.SettingsWindow(_config);
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
    private static string? ExtractEpgUrl(string m3uText)
    {
        try
        {
            var firstLine = m3uText.Split('\n')[0];
            var idx = firstLine.IndexOf("url-tvg=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var rest = firstLine[(idx + 8)..].Trim();
            if (rest.StartsWith('"')) rest = rest.TrimStart('"');
            var end = rest.IndexOf('"');
            if (end >= 0) rest = rest[..end];
            return rest.Trim().Length > 0 ? rest.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>加载 EPG 节目单（XMLTV）</summary>
    private async Task LoadEpgAsync(string url, CancellationToken ct)
    {
        try
        {
            var xml = await FetchTextAsync(url, ct);
            var epg = await Task.Run(() => EpgParser.Parse(xml), ct);
            _epg = epg;
            AppLog.Info($"epg loaded {epg.Count} channels from {url}");
            // 刷新当前频道节目显示
            if (_current != null) ShowEpgForChannel(_current);
        }
        catch (Exception ex)
        {
            AppLog.Error($"epg load failed: {ex.Message}");
        }
    }

    /// <summary>获取频道当前/即将播出的节目</summary>
    private List<EpgProgram> GetPrograms(ChannelItem ch)
    {
        var id = ch.TvgId;
        if (string.IsNullOrEmpty(id)) return new List<EpgProgram>();
        if (_epg.TryGetValue(id, out var list)) return list;
        // 尝试用频道名匹配
        foreach (var kv in _epg)
        {
            if (kv.Key.Equals(ch.Name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
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
                _config.ActivePlaylist = "Xtream";
                _config.Playlists.Add(new PlaylistSource { Name = "Xtream", Url = $"{server}/get.php?type=live&output=m3u8" });
                _config.Save();
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

    /// <summary>应用主题（深色/浅色）：遍历主窗口关键控件调整配色</summary>
    private void ApplyTheme(string theme)
    {
        var isDark = theme != "浅色";
        var merged = Application.Current.Resources.MergedDictionaries;
        // 移除旧的主题字典（App.xaml 默认合并的 Dark.xaml 也在其中），再加载目标主题
        var old = merged.Where(d => d.Source != null && d.Source.OriginalString.Contains("Themes/")).ToList();
        foreach (var d in old) merged.Remove(d);
        merged.Add(new ResourceDictionary
        {
            Source = new Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        });
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
            if (_view.Cast<ChannelItem>().Count() > 0)
            {
                var first = _view.Cast<ChannelItem>().First();
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

    /// <summary>分组管理：重命名 / 新建 / 删除分组</summary>
    private void BtnManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var groups = _all.Select(c => c.Group).Distinct().OrderBy(g => g, StringComparer.CurrentCulture).ToList();
        var dlg = new Dialogs.GroupManagerWindow(groups);
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

        switch (key)
        {
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                ToggleSidebar();
                return true;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                ResetLayout();
                return true;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None && _view.Cast<ChannelItem>().Count() > 0:
                MoveChannel(-1);
                return true;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None && _view.Cast<ChannelItem>().Count() > 0:
                MoveChannel(1);
                return true;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            case Key.F11:
            case Key.F when Keyboard.Modifiers == ModifierKeys.None:
                ToggleFullscreen();
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
        if (_view.Cast<ChannelItem>().Count() == 0) return;
        var items = _view.Cast<ChannelItem>().ToList();
        int idx;
        if (ChannelList.SelectedIndex < 0)
            idx = delta > 0 ? 0 : items.Count - 1;  // 无选中：↓从第1个开始，↑从最后1个开始
        else
            idx = (ChannelList.SelectedIndex + delta + items.Count) % items.Count;
        var ch = items[idx];
        ChannelList.SelectedItem = ch;
        ChannelList.ScrollIntoView(ch);
        NowPlaying.Text = ch.Name;             // 选中/滚动/名称即时反馈
        _pendingPlay = ch;                     // 防抖 150ms：连按时只播最后一个
        _switchDebounce.Stop();
        _switchDebounce.Start();
        ShowOsd();
    }

    // ---------- 坏源容错 ----------

    /// <summary>播放出错处理：优先切同频道备用线路；无备用则 1.2s 后自动跳下一个频道</summary>
    private void HandlePlayError()
    {
        if (_closing || _current is not { } ch) return;
        _recentFailedUrls.Add(ch.Url);
        if (++_failCount > 5)
        {
            PlayState.Text = "连续失败，已停止自动换台";
            AppLog.Error($"auto-skip stopped after limit: [{ch.Name}]");
            return;
        }

        var backup = FindBackupChannel(ch);
        if (backup is not null && !_recentFailedUrls.Contains(backup.Url))
        {
            PlayState.Text = "线路失败，切备用…";
            AppLog.Info($"switch to backup [{backup.Name}]");
            _ = PlayAsync(backup);
            return;
        }

        PlayState.Text = "播放出错，自动跳台…";
        AppLog.Error($"channel error, auto-skip: [{ch.Name}] {ch.Url}");
        _ = AutoSkipAsync(ch);
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
        var baseName = NormalizeName(failed.Name);
        return _all.FirstOrDefault(c => c.Url != failed.Url
                                     && NormalizeName(c.Name) == baseName);
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

    /// <summary>直播停滞看门狗：每 4 秒检查播放位置，IsPlaying 但连续 2 次（8 秒）不前进则自动重连（上限 3 次）</summary>
    private void OnStallWatchTick(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (_player is null || !_player.IsPlaying) { _stallTicks = 0; _lastMediaTime = -1; return; } // 未播/暂停不监控
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
        _closing = true;                    // 挂起回调（自动跳台/防抖/看门狗）不再操作 UI
        _netCts.Cancel();                  // 取消挂起的网络拉取（播放列表/EPG/Xtream）
        _stallWatch.Stop();
        _switchDebounce.Stop();
        _osdTimer.Stop();
        _fsHideTimer.Stop();
        _searchDebounce.Stop();
        _saveDebounce.Stop();
        // 取消订阅静态事件，避免窗口对象被静态引用导致无法回收（内存泄漏）
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        Activated -= OnWindowActivated;

        // 先关闭所有独立频道窗口：它们共享主窗口的 LibVLC 引擎，
        // 若主窗口先释放引擎再关闭子窗口，子窗口 Dispose 会崩溃
        foreach (var win in _channelWindows.ToList())
        {
            try { win.DetachEngine(); win.Close(); } catch { }
        }
        _channelWindows.Clear();

        // 关闭独立悬浮小窗（同样共享引擎，需先 DetachEngine 避免重复释放）
        if (_pipWindow != null)
        {
            try { _pipWindow.DetachEngine(); _pipWindow.Close(); } catch { }
            _pipWindow = null;
        }

        _config.Save();
        AppLog.Info("exit");
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        try { _currentMedia?.Dispose(); } catch { }
        try { _player?.Dispose(); } catch { }
        try { _libvlc?.Dispose(); } catch { }
        _http.Dispose();
    }
}
