namespace TiviMateWin.Services;

/// <summary>
/// 隐藏频道纯逻辑（v1.4.35，供 MainWindow 与测试共用）：
/// - 隐藏只记 URL（见 AppConfig.HiddenChannels），列表过滤时排除；
/// - 隐藏后频道从列表消失，必须有办法找回来：打开“显示隐藏频道”后它们重新出现
///   （行上带“已隐藏”标记），再对其中一个点“显示频道”取消隐藏；
/// - 没有“显示隐藏频道”开关时，隐藏的频道永远不在列表里，右键菜单里的
///   “显示频道”也就点不到——BBC 被隐藏后找不回来就是这个原因。
/// </summary>
public static class ChannelHiding
{
    /// <summary>该频道是否在隐藏列表里。</summary>
    public static bool IsHidden(string url, IReadOnlyCollection<string> hidden) => hidden.Contains(url);

    /// <summary>隐藏：已在列表里时不重复加。</summary>
    public static void Hide(ICollection<string> hidden, string url)
    {
        if (!hidden.Contains(url)) hidden.Add(url);
    }

    /// <summary>取消隐藏：不在列表里返回 false，不做任何事。</summary>
    public static bool Unhide(ICollection<string> hidden, string url) => hidden.Remove(url);

    /// <summary>列表里该不该显示这个频道：没隐藏一定显示；隐藏的只有打开
    /// “显示隐藏频道”开关才显示。</summary>
    public static bool ShouldShow(string url, IReadOnlyCollection<string> hidden, bool showHidden)
        => showHidden || !hidden.Contains(url);
}
