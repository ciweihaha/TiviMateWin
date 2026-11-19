using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>频道搜索匹配测试（v1.4.33）：修旧版"搜索不好用"——纯数字搜不到台号、
/// CCTV1/央视一套这类别名搜不到、"央视"搜不到。空 query 全匹配。</summary>
public class ChannelSearchTests
{
    [Fact]
    public void ChineseName_SubstringMatches()
    {
        Assert.True(ChannelSearch.Matches("湖南卫视", "卫视频道", 31, "湖南"));
        Assert.True(ChannelSearch.Matches("凤凰中文", "其他地区直播", 51, "凤凰"));
        Assert.True(ChannelSearch.Matches("CCTV综合", "央视频道", 1, "央视"));
        Assert.True(ChannelSearch.Matches("CCTV财经", "央视频道", 2, "央视"));
        Assert.False(ChannelSearch.Matches("湖南卫视", "卫视频道", 31, "央视"));
    }

    [Theory]
    [InlineData("BBC 新闻", "BBC")]
    [InlineData("BBC 新闻", "bbc")]
    [InlineData("CNN International", "CNN")]
    [InlineData("CNN International", "cnn")]
    public void EnglishName_CaseInsensitiveMatches(string name, string q)
    {
        Assert.True(ChannelSearch.Matches(name, "其他地区直播", 56, q));
        Assert.False(ChannelSearch.Matches("CCTV综合", "央视频道", 1, "BBC"));
        Assert.False(ChannelSearch.Matches("BBC 新闻", "其他地区直播", 56, "CNN"));
    }

    [Theory]
    [InlineData("1", 1, true)]
    [InlineData("1", 2, false)]
    [InlineData("50", 50, true)]
    [InlineData("8", 8, true)]
    public void PureDigits_MatchChannelNumberOnly(string q, int number, bool expected)
    {
        // "8" 只按台号 8 匹配，不混进名字里的数字：CCTV8K超高清（台号 18 之类）不应命中。
        Assert.Equal(expected, ChannelSearch.Matches("CCTV8K超高清", "央视频道", number, q));
    }

    [Fact]
    public void PureDigits_DoesNotMatchNameContainingDigit()
    {
        Assert.False(ChannelSearch.Matches("CCTV8K超高清", "央视频道", 18, "8"));
        Assert.False(ChannelSearch.Matches("CCTV8K超高清", "央视频道", 56, "8"));
    }

    [Theory]
    [InlineData("CCTV1", "CCTV综合")]
    [InlineData("CCTV-1", "CCTV综合")]
    [InlineData("CCTV 1", "CCTV综合")]
    [InlineData("cctv1", "CCTV综合")]
    [InlineData("CCTV5+", "CCTV体育赛事")]
    [InlineData("CCTV13", "CCTV新闻")]
    [InlineData("央视1", "CCTV综合")]
    [InlineData("央视一套", "CCTV综合")]
    [InlineData("央视1套", "CCTV综合")]
    [InlineData("央视十三套", "CCTV新闻")]
    [InlineData("央视十七套", "CCTV农业农村")]
    public void CctvAlias_ExpandsToChineseName(string q, string expected)
    {
        Assert.Equal(expected, ChannelSearch.ExpandCctvAlias(q));
        Assert.True(ChannelSearch.Matches(expected, "央视频道", 1, q));
    }

    [Fact]
    public void CctvAlias_NonCctv_ReturnsNull()
    {
        Assert.Null(ChannelSearch.ExpandCctvAlias("湖南"));
        Assert.Null(ChannelSearch.ExpandCctvAlias("BBC"));
        Assert.Null(ChannelSearch.ExpandCctvAlias("CCTV99"));
    }

    [Fact]
    public void EmptyQuery_MatchesAll()
    {
        Assert.True(ChannelSearch.Matches("CCTV综合", "央视频道", 1, ""));
        Assert.True(ChannelSearch.Matches("湖南卫视", "卫视频道", 31, "  "));
    }

    [Fact]
    public void GroupSubstring_StillMatches()
    {
        Assert.True(ChannelSearch.Matches("湖南卫视", "卫视频道", 31, "卫视"));
        Assert.False(ChannelSearch.Matches("湖南卫视", "卫视频道", 31, "央视"));
    }
}
