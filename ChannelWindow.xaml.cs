using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using TiviMateWin.Models;
using TiviMateWin.Services;

namespace TiviMateWin;

/// <summary>独立频道播放窗口：多窗口同时播放，每个窗口一个独立 MediaPlayer（共享 LibVLC 实例）</summary>
public partial class ChannelWindow : Window
{
    private readonly LibVLC _libvlc;
    private readonly MediaPlayer _player;
    private ChannelItem? _channel;
    private int _volume;
    private bool _closing;

    public ChannelWindow(LibVLC libvlc, int volume)
    {
        InitializeComponent();
        _libvlc = libvlc;
        _volume = volume;
        _player = new MediaPlayer(_libvlc) { Volume = _volume };
        VideoView.MediaPlayer = _player;
    }

    /// <summary>播放指定频道</summary>
    public void Play(ChannelItem ch)
    {
        _channel = ch;
        Title = ch.Name;
        TitleText.Text = ch.Name;
        try
        {
            using var media = ch.Url.Contains("://")
                ? new Media(_libvlc, new Uri(ch.Url))
                : new Media(_libvlc, ch.Url, FromType.FromPath);
            media.AddOption(":network-caching=800");
            _player.Play(media);
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
        _closing = true;
        try { _player.Stop(); } catch { }
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        try { _player.Dispose(); } catch { }
        AppLog.Info($"channel window closed [{_channel?.Name}]");
    }
}