namespace TiviMateWin.Services;

/// <summary>
/// 串行模式音频时钟（纯逻辑，可单元测试）：窗的音频结束至今过了多久，
/// 超过一窗长即陈旧、该丢（处理追不上说话速度时追新不追旧）。
/// </summary>
public static class LiveSubtitleSerialClock
{
    /// <summary>
    /// 窗的音频结束至今过了多久（秒）。audioStartWall 为 null（还没收到音频）时返回 0。
    /// windowsCompleted 含当前窗（窗的音频在起点后 windowsCompleted × 窗长处结束）。
    /// </summary>
    public static double WindowStalenessSeconds(DateTimeOffset? audioStartWall, long windowsCompleted,
        int windowSeconds, DateTimeOffset now)
    {
        if (audioStartWall is null) return 0;
        return (now - audioStartWall.Value).TotalSeconds - windowsCompleted * windowSeconds;
    }

    /// <summary>窗陈旧到该丢：音频结束至今超过一窗长，处理已追不上说话速度。</summary>
    public static bool IsStaleWindow(double stalenessSeconds, int windowSeconds) =>
        stalenessSeconds > windowSeconds;

    /// <summary>
    /// 等 PCM 超时后把音频时钟往前拨：断流期间墙钟照走、音频不走，
    /// 不拨时钟就会把断流时间算成落后，重连后每个窗都判陈旧、永久丢窗。
    /// audioStartWall 为 null（还没收到音频）时原样返回 null。
    /// </summary>
    public static DateTimeOffset? AdvanceForIdleTime(DateTimeOffset? audioStartWall, TimeSpan idle) =>
        audioStartWall?.Add(idle);
}
