using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>
/// 串行模式音频时钟测试（纯函数，不加载模型、不联网）。
/// 真模型只在云电脑实测。
/// </summary>
public class LiveSubtitleServiceTests
{
    [Fact]
    public void WindowStalenessSeconds_FirstWindow_Zero()
    {
        var start = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        // 第一窗 3 秒后攒满：墙钟过了 3 秒，音频也走了 3 秒 → 不陈旧
        Assert.Equal(0, LiveSubtitleSerialClock.WindowStalenessSeconds(start, 1, 3, start.AddSeconds(3)), 6);
    }

    [Fact]
    public void WindowStalenessSeconds_BehindByTwoWindows()
    {
        var start = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        // 墙钟过了 9 秒，音频只走了 3 秒（1 窗）→ 落后 6 秒
        Assert.Equal(6, LiveSubtitleSerialClock.WindowStalenessSeconds(start, 1, 3, start.AddSeconds(9)), 6);
    }

    [Fact]
    public void WindowStalenessSeconds_NoAudioYet_Zero()
    {
        Assert.Equal(0, LiveSubtitleSerialClock.WindowStalenessSeconds(null, 0, 3, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(2.9, 3, false)]
    [InlineData(3.0, 3, false)] // 正好一窗长：不丢（边界留一线）
    [InlineData(3.1, 3, true)]
    [InlineData(6.0, 3, true)]
    public void IsStaleWindow_ThresholdIsOneWindow(double staleness, int window, bool expected)
    {
        Assert.Equal(expected, LiveSubtitleSerialClock.IsStaleWindow(staleness, window));
    }
}
