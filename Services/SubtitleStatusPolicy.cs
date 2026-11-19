namespace TiviMateWin.Services;

/// <summary>实时字幕状态提示的显示策略（v1.4.35）。
/// 根因：字幕服务每次（重）启动时 PrepareAsync 都会发一条状态
/// （如“免费接口未配置，只显示英文原文”），ShowLiveSubtitleStatus
/// 只管显示、从不清掉；中文台听不到英文、没有字幕行触发 5 秒隐藏，
/// 这条圆角提示就一直挂在视频上；切到英语台服务重建又会再弹一次。
/// 规则：同一条提示每个会话只显示一次；显示后 5 秒自动消失
/// （UnBox 0.9.16 的报错提示同款做法）。计时器在 MainWindow 里。</summary>
public static class SubtitleStatusPolicy
{
    /// <summary>状态提示显示几秒后自动消失。</summary>
    public const int AutoHideSeconds = 5;

    /// <summary>这条提示现在该不该显示：空串不显示；和本会话已显示过的
    /// 同一条不重复显示（切台服务重建时不再反复弹）。</summary>
    public static bool ShouldShow(string status, string? lastShownMessage) =>
        !string.IsNullOrEmpty(status) && status != lastShownMessage;
}
