using System.Windows;
using System.Windows.Input;
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
    private bool _dragging;
    private Point _dragStart;
    private Point _winOrigin;

    public PipWindow(LibVLC libvlc, int volume)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _player = new MediaPlayer(_libvlc) { Volume = volume };
        VideoView.MediaPlayer = _player;
    }

    /// <summary>播放指定频道</summary>
    public void Play(ChannelItem ch)
    {
        TitleText.Text = ch.Name;
        try
        {
            var media = ch.Url.Contains("://")
                ? new Media(_libvlc, new Uri(ch.Url))
                : new Media(_libvlc, ch.Url, FromType.FromPath);
            media.AddOption(":network-caching=800");
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

    // 拖动窗口
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _winOrigin = new Point(Left, Top);
        TitleBar.CaptureMouse();
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(this);
        Left = _winOrigin.X + (cur.X - _dragStart.X);
        Top = _winOrigin.Y + (cur.Y - _dragStart.Y);
    }

    private void TitleBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        TitleBar.ReleaseMouseCapture();
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