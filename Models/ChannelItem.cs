using System.ComponentModel;
using System.Text.Json.Serialization;

namespace TiviMateWin.Models;

/// <summary>频道数据模型</summary>
public class ChannelItem : INotifyPropertyChanged
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Group { get; set; } = "未分组";
    public string? Logo { get; set; }
    public string? TvgId { get; set; }   // EPG 频道标识

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteMark)));
        }
    }

    [JsonIgnore]
    public string FavoriteMark => IsFavorite ? "★" : "☆";

    private bool _isPlaying;
    /// <summary>是否为当前正在播放的频道（列表内高亮指示，V1.4）</summary>
    [JsonIgnore]
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        }
    }

    /// <summary>无 Logo 时的首字符占位（V1.4）</summary>
    [JsonIgnore]
    public string Initial
    {
        get
        {
            var s = Name?.TrimStart();
            if (string.IsNullOrEmpty(s)) return "?";
            // 深查第 16 轮：首字符是代理项对（如 emoji 📺）时只取 1 个 UTF-16 码元
            // 会得到孤立代理项，渲染为 �；成对取。
            return char.IsSurrogatePair(s, 0) ? s[..2] : s[..1];
        }
    }

    private string _nowProgramText = "";
    /// <summary>当前正在播的节目（UI 改版：频道行第二行显示；无节目单时为空）。
    /// 由 MainWindow 随 EPG 加载/每分钟刷新统一更新，不参与序列化。</summary>
    [JsonIgnore]
    public string NowProgramText
    {
        get => _nowProgramText;
        private set
        {
            if (_nowProgramText == value) return;
            _nowProgramText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowProgramText)));
        }
    }

    private double _nowProgress;
    /// <summary>当前节目已播进度 0..1（UI 改版：频道行细进度条 / OSD / 全屏浮层共用）；无节目时为 0。</summary>
    [JsonIgnore]
    public double NowProgress
    {
        get => _nowProgress;
        private set
        {
            if (Math.Abs(_nowProgress - value) < 0.001) return;
            _nowProgress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowProgress)));
        }
    }

    /// <summary>更新当前节目显示（文本 + 进度）：值不变时不触发通知，避免无谓重绘。</summary>
    public void SetNowProgram(string text, double progress)
    {
        NowProgramText = text;
        NowProgress = Math.Clamp(progress, 0, 1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
