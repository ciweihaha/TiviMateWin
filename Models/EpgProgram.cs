namespace TiviMateWin.Models;

/// <summary>EPG 节目条目</summary>
public class EpgProgram
{
    public string ChannelId { get; set; } = "";   // 频道 tvg-id
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime Start { get; set; }
    public DateTime Stop { get; set; }
    public string? Category { get; set; }

    public string TimeRange => $"{Start:HH:mm} - {Stop:HH:mm}";
}