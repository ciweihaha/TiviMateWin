using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>
/// 延迟优化的默认配置回归测试：tiny.en + 1.5 秒窗（base.en + 3 秒窗在两台
/// 机器上端到端约 11 秒，目标 6 秒内）。切块器与串行时钟接受小数窗口。
/// </summary>
public class LiveSubtitleLatencyDefaultsTests
{
    [Fact]
    public void Chunker_FractionalWindow_OnePointFiveSeconds()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 1.5);
        var window = chunker.Feed(new short[24000]);
        Assert.NotNull(window);
        Assert.Equal(24000, window!.Length);
    }

    [Fact]
    public void Chunker_FractionalWindow_NotFull_ReturnsNull()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 1.5);
        Assert.Null(chunker.Feed(new short[23999]));
        Assert.NotNull(chunker.Feed(new short[1]));
    }

    [Fact]
    public void SerialClock_FractionalWindow_StalenessUsesDouble()
    {
        var start = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        // 一窗 1.5 秒：第 1 窗在起点后 1.5 秒处结束，3 秒时陈旧 1.5 秒。
        Assert.Equal(1.5, LiveSubtitleSerialClock.WindowStalenessSeconds(start, 1, 1.5, start.AddSeconds(3)), 6);
        Assert.True(LiveSubtitleSerialClock.IsStaleWindow(1.6, 1.5));
        Assert.False(LiveSubtitleSerialClock.IsStaleWindow(1.5, 1.5));
    }
}
