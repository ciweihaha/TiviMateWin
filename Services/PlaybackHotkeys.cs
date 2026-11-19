namespace TiviMateWin.Services;

/// <summary>
/// 空格键暂停/继续的纯逻辑（v1.4.34，供 MainWindow 与测试共用）：
/// - 空格只在已有播放器、焦点不在输入类控件上时才切换暂停；
///   焦点在搜索框/下拉框/菜单里时空格是打字或菜单操作，不能抢。
/// - 暂停状态切换就是取反；真正的暂停/继续走 LibVLC 的 Pause/Play
///   （见 MainWindow.TogglePlayPause），这里只定"该不该切"和"切到哪边"。
/// </summary>
public static class PlaybackHotkeys
{
    /// <summary>空格键是否应切换暂停/继续。</summary>
    public static bool ShouldTogglePauseOnSpace(bool hasPlayer, bool focusIsTextInput)
    {
        if (!hasPlayer) return false;
        return !focusIsTextInput;
    }

    /// <summary>暂停状态切换：暂停→播放，播放→暂停。</summary>
    public static bool TogglePaused(bool isPaused) => !isPaused;
}
