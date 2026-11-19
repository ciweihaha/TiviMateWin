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

    private bool _isHidden;
    /// <summary>是否在隐藏列表里（v1.4.35：打开“显示隐藏频道”时，已隐藏的台重新出现并带“已隐藏”标记）</summary>
    [JsonIgnore]
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
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

    public event PropertyChangedEventHandler? PropertyChanged;
}
