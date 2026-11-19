using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>新窗口播放测试（v1.4.35）：开新窗口主窗口必须停（不能两个窗口同时出声）；
/// 最后一个新窗口关掉时，被新窗口暂停的主窗口恢复播放；用户手动接管后不恢复。</summary>
public class NewWindowPlaybackTests
{
    [Fact]
    public void ShouldPauseMain_PlayingAndNotPaused_Pauses()
    {
        Assert.True(NewWindowPlayback.ShouldPauseMain(mainIsPlaying: true, mainIsPaused: false));
    }

    [Fact]
    public void ShouldPauseMain_NotPlayingOrAlreadyPaused_DoesNotPause()
    {
        Assert.False(NewWindowPlayback.ShouldPauseMain(mainIsPlaying: false, mainIsPaused: false));
        Assert.False(NewWindowPlayback.ShouldPauseMain(mainIsPlaying: true, mainIsPaused: true));
        Assert.False(NewWindowPlayback.ShouldPauseMain(mainIsPlaying: false, mainIsPaused: true));
    }

    [Fact]
    public void ShouldResumeMain_AllConditions_Resumes()
    {
        Assert.True(NewWindowPlayback.ShouldResumeMain(
            pausedForNewWindow: true, mainIsPaused: true, hasCurrentChannel: true, openWindows: 0));
    }

    [Fact]
    public void ShouldResumeMain_OtherWindowStillOpen_DoesNotResume()
    {
        Assert.False(NewWindowPlayback.ShouldResumeMain(
            pausedForNewWindow: true, mainIsPaused: true, hasCurrentChannel: true, openWindows: 1));
    }

    [Fact]
    public void ShouldResumeMain_NotPausedByNewWindow_DoesNotResume()
    {
        // 主窗口本来就暂停着（不是新窗口暂停的）：关新窗口不应替用户恢复。
        Assert.False(NewWindowPlayback.ShouldResumeMain(
            pausedForNewWindow: false, mainIsPaused: true, hasCurrentChannel: true, openWindows: 0));
    }

    [Fact]
    public void ShouldResumeMain_UserResumedOrStopped_DoesNotResume()
    {
        // 用户中途手动恢复了播放（不再是暂停态）：不重复恢复。
        Assert.False(NewWindowPlayback.ShouldResumeMain(
            pausedForNewWindow: true, mainIsPaused: false, hasCurrentChannel: true, openWindows: 0));
        // 用户手动停播（当前频道已清）：没有可恢复的频道。
        Assert.False(NewWindowPlayback.ShouldResumeMain(
            pausedForNewWindow: true, mainIsPaused: true, hasCurrentChannel: false, openWindows: 0));
    }
}
