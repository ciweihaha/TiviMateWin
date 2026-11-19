using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>台语种判定与翻译开关测试（v1.4.33）：只翻英语（Whisper tiny.en 本来也只认英语）。
/// 覆盖：已知英语台（含混入中文的 "BBC 新闻"）、中文台、法语/德语/日语等非英语台不翻、
/// 模式 Off/All、手动强制优先、总开关关、非法模式回落默认。</summary>
public class ChannelLanguageTests
{
    [Theory]
    [InlineData("BBC 新闻")]
    [InlineData("BBC News")]
    [InlineData("CNN")]
    [InlineData("CNN International")]
    [InlineData("Sky News")]
    [InlineData("France 24 English")]
    [InlineData("NHK World")]
    [InlineData("CGTN")]
    [InlineData("Discovery")]
    [InlineData("ESPN")]
    public void EnglishChannel_IsEnglish(string name)
    {
        Assert.True(ChannelLanguage.IsEnglishChannel(name));
    }

    [Theory]
    [InlineData("CCTV综合")]
    [InlineData("CCTV-1 综合")]
    [InlineData("湖南卫视")]
    [InlineData("凤凰中文")]
    [InlineData("凤凰资讯")]
    [InlineData("深圳卫视")]
    [InlineData("浙江卫视")]
    [InlineData("TVB 翡翠台")]
    [InlineData("TV5MONDE")]
    [InlineData("France 24")]
    [InlineData("DW")]
    [InlineData("NHK")]
    [InlineData("ZDF")]
    [InlineData("日本 NHK 综合")]
    public void NonEnglishChannel_IsNotEnglish(string name)
    {
        Assert.False(ChannelLanguage.IsEnglishChannel(name));
    }

    [Fact]
    public void NullOrEmptyChannel_IsNotEnglish()
    {
        Assert.False(ChannelLanguage.IsEnglishChannel(null));
        Assert.False(ChannelLanguage.IsEnglishChannel(""));
        Assert.False(ChannelLanguage.IsEnglishChannel("   "));
    }

    [Fact]
    public void Resolve_DefaultMode_EnglishOnly()
    {
        // 默认仅英语台：BBC 开，CCTV 不开，法语台也不开（不是按"非中文"一刀切）。
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, "EnglishOnly", "BBC 新闻", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "EnglishOnly", "CCTV综合", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "EnglishOnly", "France 24", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "EnglishOnly", "DW", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "EnglishOnly", "NHK", null));
    }

    [Fact]
    public void Resolve_NullOrBadMode_FallsBackToEnglishOnly()
    {
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, null, "BBC 新闻", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, null, "CCTV综合", null));
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, "NonChinese", "CNN", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "NonChinese", "湖南卫视", null));
    }

    [Fact]
    public void Resolve_ModeOff_And_All()
    {
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "Off", "BBC 新闻", null));
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, "All", "CCTV综合", null));
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, "All", "France 24", null));
    }

    [Fact]
    public void Resolve_MasterSwitchOff_NeverTranslates()
    {
        Assert.False(ChannelLanguage.ResolveTranslationActive(false, "All", "BBC 新闻", null));
        Assert.False(ChannelLanguage.ResolveTranslationActive(false, "EnglishOnly", "BBC 新闻", true));
    }

    [Fact]
    public void Resolve_ForceOverride_WinsOverMode()
    {
        // 手动强制开：中文台也翻；手动强制关：英语台也不翻。
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, "EnglishOnly", "CCTV综合", true));
        Assert.False(ChannelLanguage.ResolveTranslationActive(true, "All", "BBC 新闻", false));
        Assert.True(ChannelLanguage.ResolveTranslationActive(true, "Off", "CNN", true));
    }

    [Fact]
    public void NormalizeMode_BadValue_FallsBackToEnglishOnly()
    {
        Assert.Equal("EnglishOnly", ChannelLanguage.NormalizeMode(null));
        Assert.Equal("EnglishOnly", ChannelLanguage.NormalizeMode(""));
        Assert.Equal("EnglishOnly", ChannelLanguage.NormalizeMode("NonChinese"));
        Assert.Equal("Off", ChannelLanguage.NormalizeMode("Off"));
        Assert.Equal("All", ChannelLanguage.NormalizeMode("All"));
        Assert.Equal("EnglishOnly", ChannelLanguage.DefaultMode);
    }
}
