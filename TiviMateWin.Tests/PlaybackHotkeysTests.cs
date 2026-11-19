using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>空格键暂停/继续测试（v1.4.34）：直播/点播都按空格暂停、再按继续；
/// 没播放器时不触发；焦点在搜索框等输入控件上时空格是打字，不抢。</summary>
public class PlaybackHotkeysTests
{
    [Fact]
    public void Space_WithPlayerAndNormalFocus_Toggles()
    {
        Assert.True(PlaybackHotkeys.ShouldTogglePauseOnSpace(true, false));
    }

    [Fact]
    public void Space_WithoutPlayer_NeverToggles()
    {
        Assert.False(PlaybackHotkeys.ShouldTogglePauseOnSpace(false, false));
        Assert.False(PlaybackHotkeys.ShouldTogglePauseOnSpace(false, true));
    }

    [Fact]
    public void Space_InTextInput_DoesNotToggle()
    {
        // 焦点在搜索框/下拉框/菜单里：空格是打字，不能抢去暂停。
        Assert.False(PlaybackHotkeys.ShouldTogglePauseOnSpace(true, true));
    }

    [Fact]
    public void TogglePaused_FlipsState()
    {
        Assert.True(PlaybackHotkeys.TogglePaused(false));
        Assert.False(PlaybackHotkeys.TogglePaused(true));
    }

    [Fact]
    public void TogglePaused_Twice_ReturnsToOriginal()
    {
        Assert.False(PlaybackHotkeys.TogglePaused(PlaybackHotkeys.TogglePaused(false)));
        Assert.True(PlaybackHotkeys.TogglePaused(PlaybackHotkeys.TogglePaused(true)));
    }
}
