namespace TiviMateWin.Services;

/// <summary>秒断恢复纯逻辑（v1.4.36，供 MainWindow 与测试共用）：
/// - 播放开始 3 秒内就断即秒断（demuxer 没建起来，分片 403 等），不是播着播着断网；
///   3 秒是 CCTV-1 事故里的实测秒断窗口（0–2 秒）；
/// - 触顶后秒断满 3 次自动跳下一台，每轮最多跳 2 次，再不行明确显示“无法播放”。</summary>
public static class StreamRecovery
{
    public const int FastFailSeconds = 3;
    public const int MaxAutoSkips = 2;
    public const string CannotPlayText = "无法播放，请手动重进";
    public static bool IsFastFail(TimeSpan sincePlayStart) => sincePlayStart < TimeSpan.FromSeconds(FastFailSeconds);
    public static bool ShouldAutoSkip(int fastFailStreak, int autoSkipsDone) => fastFailStreak >= 3 && autoSkipsDone < MaxAutoSkips;
}
