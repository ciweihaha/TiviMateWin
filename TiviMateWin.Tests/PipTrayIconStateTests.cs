using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>托盘图标状态机测试：小窗最小化隐藏→图标出现；恢复显示/关闭→图标消失；全屏隐藏不触动图标。</summary>
public class PipTrayIconStateTests
{
    [Fact]
    public void Minimized_ShowsTrayIcon()
    {
        var state = new PipTrayIconState();
        Assert.False(state.IconVisible);
        state.NotifyMinimized();
        Assert.True(state.IconVisible);
    }

    [Fact]
    public void Restored_HidesTrayIcon()
    {
        var state = new PipTrayIconState();
        state.NotifyMinimized();
        state.NotifyRestored();
        Assert.False(state.IconVisible);
    }

    [Fact]
    public void Closed_HidesTrayIcon()
    {
        var state = new PipTrayIconState();
        state.NotifyMinimized();
        state.NotifyClosed();
        Assert.False(state.IconVisible);
    }

    [Fact]
    public void FullscreenHidden_DoesNotShowTrayIcon()
    {
        var state = new PipTrayIconState();
        state.NotifyFullscreenHidden();
        Assert.False(state.IconVisible);
    }

    [Fact]
    public void FullscreenHiddenWhileMinimized_KeepsTrayIcon()
    {
        var state = new PipTrayIconState();
        state.NotifyMinimized();
        state.NotifyFullscreenHidden();
        Assert.True(state.IconVisible);
    }

    [Fact]
    public void DoubleMinimized_StillVisible()
    {
        var state = new PipTrayIconState();
        state.NotifyMinimized();
        state.NotifyMinimized();
        Assert.True(state.IconVisible);
    }

    [Fact]
    public void MinimizedAfterClosed_StaysHidden()
    {
        var state = new PipTrayIconState();
        state.NotifyClosed();
        state.NotifyMinimized();
        Assert.False(state.IconVisible);
    }
}
