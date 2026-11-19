namespace TiviMateWin.Services;

/// <summary>
/// 状态栏翻译强制开关的切换逻辑（v1.4.34，供 MainWindow 与测试共用）：
/// 自动 → 强制开 → 强制关 → 自动；只对当前台有效（切台后清零恢复自动）。
/// 点到"强制开"时总开关必须一起开——ResolveTranslationActive 先看总开关，
/// 总开关关着时强制开也被一票否决：按钮文案变成了"翻译: 开"，字幕却永远不出，
/// 用户看到的就是"开关打不开、点了没反应"（台式机 2026-11-19 实测根因：
/// LiveSubtitleEnabled=false 时点了十几次翻译强制都只改文案、不生效）。
/// 点到"关/自动"不动总开关（只影响当前台）。
/// </summary>
public static class TranslationToggle
{
    /// <summary>按一次开关：返回（新的总开关，新的手动强制）。</summary>
    public static (bool MasterEnabled, bool? ForceOverride) Cycle(bool masterEnabled, bool? forceOverride)
    {
        bool? next = forceOverride switch
        {
            null => true,
            true => false,
            _ => null,
        };
        if (next == true && !masterEnabled) masterEnabled = true;
        return (masterEnabled, next);
    }
}
