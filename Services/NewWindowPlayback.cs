namespace TiviMateWin.Services;

/// <summary>
/// 新窗口播放时主窗口的停/续纯逻辑（v1.4.35，供 MainWindow 与测试共用）：
/// - 开新窗口时主窗口必须停，不能两个窗口同时出声（用户 2026-11-19 报：
///   点“新窗口播放”，原窗口和新窗口都在播）；
/// - 最后一个新窗口关掉时，如果主窗口是被新窗口暂停的、且用户中途没手动
///   操作过暂停/播放，就恢复主窗口播放（同款逻辑见小窗关闭恢复主窗口）。
/// </summary>
public static class NewWindowPlayback
{
    /// <summary>开新窗口时主窗口该不该暂停：主窗口正在播、且当前没暂停，才需要暂停。
    /// 主窗口本来就停着/暂停着，不用动。</summary>
    public static bool ShouldPauseMain(bool mainIsPlaying, bool mainIsPaused)
        => mainIsPlaying && !mainIsPaused;

    /// <summary>最后一个新窗口关闭时主窗口该不该恢复：主窗口是被新窗口暂停的、
    /// 现在仍处于暂停态、还有可恢复的当前频道、没有别的新窗口开着，四条全满足才恢复。
    /// 用户中途手动按过暂停/播放（接管了暂停态）或手动停播（当前频道已清），都不恢复。</summary>
    public static bool ShouldResumeMain(bool pausedForNewWindow, bool mainIsPaused, bool hasCurrentChannel, int openWindows)
        => pausedForNewWindow && mainIsPaused && hasCurrentChannel && openWindows == 0;
}
