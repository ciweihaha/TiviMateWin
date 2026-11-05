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
        // 非有限值（NaN/Infinity）直接拒绝：NaN 比较恒为 false、Infinity 可能算出虚假相交面积，
        // 不靠后续比较间接兜底；DPI 缩放不在此处理——无相关证据，不补 DPI 逻辑。
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height)) return false;
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

    /// <summary>
    /// 把已通过 CanRestore 的位置尺寸钳制到虚拟桌面内（前置条件：四值有限且 CanRestore 为真，
    /// 否则调用方不得应用返回值、应回落默认位置）。已完全在屏内时原样返回；
    /// 部分出屏时先把宽高压到虚拟屏尺寸内，再把左上角压到 [桌面起点, 桌面终点-窗口尺寸] 内，
    /// 使窗口整体可见。虚拟屏本身小于最小可见尺寸时以虚拟屏为准，不强行放大。
    /// </summary>
    public static (double Left, double Top, double Width, double Height) ClampToVisible(
        double left, double top, double width, double height)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var w = Math.Min(width, vw);
        var h = Math.Min(height, vh);
        var l = Math.Min(Math.Max(left, vl), vl + Math.Max(vw - w, 0));
        var t = Math.Min(Math.Max(top, vt), vt + Math.Max(vh - h, 0));
        return (l, t, w, h);
    }
}
