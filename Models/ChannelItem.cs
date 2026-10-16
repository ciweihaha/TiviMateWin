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

    public event PropertyChangedEventHandler? PropertyChanged;
}
