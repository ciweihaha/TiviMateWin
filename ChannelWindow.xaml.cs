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

    public ChannelWindow(LibVLC libvlc, int volume)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _volume = volume;
        _player = new MediaPlayer(_libvlc) { Volume = _volume };
        VideoView.MediaPlayer = _player;
    }

    /// <summary>标记为已由主窗口统一释放引擎（主窗口关闭时调用，避免重复释放）</summary>
    public void DetachEngine() => _engineReleased = true;

    private bool _engineReleased;

    /// <summary>播放指定频道</summary>
    public void Play(ChannelItem ch)
    {
        _channel = ch;
        Title = ch.Name;
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
            AppLog.Error($"channel window play failed [{ch.Name}]: {ex.Message}");
        }
        AppLog.Info($"channel window play [{ch.Name}]");
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