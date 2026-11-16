namespace TiviMateWin.Services;

/// <summary>
/// 托盘图标可见状态机（纯逻辑，无 UI 依赖，可单元测试）。
/// 规则：小窗最小化隐藏 → 图标出现；窗口恢复显示或关闭 → 图标消失；
/// 全屏进出只是临时隐藏窗口，不触动图标；关闭后不再接受最小化。
/// </summary>
public sealed class PipTrayIconState
{
    private bool _hidden;   // 小窗当前是否最小化隐藏中
    private bool _closed;   // 窗口是否已关闭（关闭后不再接受最小化）

    /// <summary>托盘图标是否应显示：小窗最小化隐藏中、且窗口未关闭。</summary>
    public bool IconVisible => _hidden && !_closed;

    public void NotifyMinimized()
    {
        if (_closed) return;
        _hidden = true;
    }

    public void NotifyFullscreenHidden()
    {
        // 全屏进出只是临时隐藏窗口，不触动图标：不改任何状态。
    }

    public void NotifyRestored() => _hidden = false;

    public void NotifyClosed()
    {
        _closed = true;
        _hidden = false;
    }
}
