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

    [Fact]
    public void AdvanceForIdleTime_ShiftsStartByIdle()
    {
        var start = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(start.AddSeconds(19),
            LiveSubtitleSerialClock.AdvanceForIdleTime(start, TimeSpan.FromSeconds(19)));
    }

    [Fact]
    public void AdvanceForIdleTime_NoAudioYet_Null()
    {
        Assert.Null(LiveSubtitleSerialClock.AdvanceForIdleTime(null, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void StallWithoutAdvance_WindowStaleForever()
    {
        // 19 秒卡顿的回归用例：墙钟过了 22 秒，音频只走了 3 秒（1 窗）→ 落后 19 秒；
        // 即使之后又出了 5 窗（音频走了 18 秒），仍落后 19 秒 → 每窗都判陈旧、永久丢窗。
        var start = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var staleness = LiveSubtitleSerialClock.WindowStalenessSeconds(start, 1, 3, start.AddSeconds(22));
        Assert.Equal(19, staleness, 6);
        Assert.True(LiveSubtitleSerialClock.IsStaleWindow(staleness, 3));
        var laterStaleness = LiveSubtitleSerialClock.WindowStalenessSeconds(start, 6, 3, start.AddSeconds(37));
        Assert.Equal(19, laterStaleness, 6);
        Assert.True(LiveSubtitleSerialClock.IsStaleWindow(laterStaleness, 3));
    }

    [Fact]
    public void StallWithAdvance_WindowRecovers()
    {
        // 同样 19 秒卡顿：每超时 1 秒拨 1 秒、拨 19 次后，落后只剩 0 秒，窗不再陈旧。
        var start = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var advanced = start;
        for (var i = 0; i < 19; i++)
            advanced = LiveSubtitleSerialClock.AdvanceForIdleTime(advanced, TimeSpan.FromSeconds(1))!.Value;
        var staleness = LiveSubtitleSerialClock.WindowStalenessSeconds(advanced, 1, 3, start.AddSeconds(22));
        Assert.Equal(0, staleness, 6);
        Assert.False(LiveSubtitleSerialClock.IsStaleWindow(staleness, 3));
    }
}
