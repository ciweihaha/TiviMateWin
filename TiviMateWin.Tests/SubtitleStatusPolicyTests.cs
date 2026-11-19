using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>字幕状态提示策略（v1.4.35）：同一条提示每个会话只显示一次，5 秒自动消失。</summary>
public class SubtitleStatusPolicyTests
{
    [Fact]
    public void FirstTime_Shows()
    {
        Assert.True(SubtitleStatusPolicy.ShouldShow("免费接口未配置（去设置里选服务商、填 Key），只显示英文原文", null));
    }

    [Fact]
    public void SameMessageTwice_SkipsSecond()
    {
        const string msg = "免费接口未配置（去设置里选服务商、填 Key），只显示英文原文";
        Assert.True(SubtitleStatusPolicy.ShouldShow(msg, null));
        Assert.False(SubtitleStatusPolicy.ShouldShow(msg, msg));
    }

    [Fact]
    public void DifferentMessage_Shows()
    {
        Assert.True(SubtitleStatusPolicy.ShouldShow("语音模型下载失败，实时字幕暂不可用", "免费接口未配置（去设置里选服务商、填 Key），只显示英文原文"));
    }

    [Fact]
    public void EmptyMessage_NeverShows()
    {
        Assert.False(SubtitleStatusPolicy.ShouldShow("", null));
        Assert.False(SubtitleStatusPolicy.ShouldShow("", "something"));
    }

    [Fact]
    public void AutoHide_IsFiveSeconds()
    {
        Assert.Equal(5, SubtitleStatusPolicy.AutoHideSeconds);
    }
}
