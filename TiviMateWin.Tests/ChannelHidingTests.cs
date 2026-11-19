using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>隐藏频道测试（v1.4.35）：隐藏的频道必须能找回来显示；
/// 没开“显示隐藏频道”时隐藏的不显示，开了才显示；取消隐藏后正常显示。</summary>
public class ChannelHidingTests
{
    [Fact]
    public void IsHidden_ContainsUrl_ReturnsTrue()
    {
        var hidden = new List<string> { "http://a/1.m3u8" };
        Assert.True(ChannelHiding.IsHidden("http://a/1.m3u8", hidden));
        Assert.False(ChannelHiding.IsHidden("http://a/2.m3u8", hidden));
    }

    [Fact]
    public void Hide_AddsOnce_NoDuplicates()
    {
        var hidden = new List<string>();
        ChannelHiding.Hide(hidden, "http://a/1.m3u8");
        ChannelHiding.Hide(hidden, "http://a/1.m3u8");
        Assert.Single(hidden);
    }

    [Fact]
    public void Unhide_RemovesUrl_ReturnsTrue()
    {
        var hidden = new List<string> { "http://a/1.m3u8", "http://a/2.m3u8" };
        Assert.True(ChannelHiding.Unhide(hidden, "http://a/1.m3u8"));
        Assert.DoesNotContain("http://a/1.m3u8", hidden);
        Assert.Contains("http://a/2.m3u8", hidden);
    }

    [Fact]
    public void Unhide_NotHidden_ReturnsFalse()
    {
        var hidden = new List<string>();
        Assert.False(ChannelHiding.Unhide(hidden, "http://a/1.m3u8"));
    }

    [Fact]
    public void ShouldShow_NotHidden_AlwaysShows()
    {
        var hidden = new List<string> { "http://a/1.m3u8" };
        Assert.True(ChannelHiding.ShouldShow("http://a/2.m3u8", hidden, false));
        Assert.True(ChannelHiding.ShouldShow("http://a/2.m3u8", hidden, true));
    }

    [Fact]
    public void ShouldShow_Hidden_OnlyWhenShowHiddenOn()
    {
        // 之前 BBC 被隐藏后找不回来的根因：过滤无条件排除隐藏频道，
        // 列表里永远见不到它，右键“显示”也就无从点起。
        var hidden = new List<string> { "http://a/1.m3u8" };
        Assert.False(ChannelHiding.ShouldShow("http://a/1.m3u8", hidden, false));
        Assert.True(ChannelHiding.ShouldShow("http://a/1.m3u8", hidden, true));
    }
}
