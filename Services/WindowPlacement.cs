using System.Windows;

namespace TiviMateWin.Services;

/// <summary>
/// 窗口位置记忆的边界校验（V1.4 新增，纯逻辑便于 SelfTest 验证）：
/// 记忆坐标恢复前必须确认窗口主体落在当前虚拟桌面内，
/// 防止「在副屏全屏过、后来副屏断开」导致主窗口恢复到屏幕外不可见。
/// </summary>
public static class WindowPlacement
{
    /// <summary>窗口至少可见的最小宽高（低于此视为不可操作）。</summary>
    public const double MinVisibleWidth = 120;
    public const double MinVisibleHeight = 80;

    /// <summary>
    /// 记忆的位置尺寸是否可安全恢复：与虚拟桌面相交，且在桌面内可见部分
    /// 宽高不低于最小可用值（右侧/底部被裁掉一大半的窗口视为无效）。
    /// </summary>
    public static bool CanRestore(double left, double top, double width, double height)
    {
        if (width < MinVisibleWidth || height < MinVisibleHeight) return false;
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        // 完全在桌面外：直接拒绝
        if (left >= vr || top >= vb || left + width <= vl || top + height <= vt) return false;
        // 与桌面的相交区域
        var ix = Math.Min(left + width, vr) - Math.Max(left, vl);
        var iy = Math.Min(top + height, vb) - Math.Max(top, vt);
        return ix >= MinVisibleWidth && iy >= MinVisibleHeight;
    }
}
