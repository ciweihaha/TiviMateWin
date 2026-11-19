using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>免费接口翻译器工厂测试（v1.4.33 重构）：服务商归一化与凭据缺失时的 null 回退。</summary>
public class FreeApiTranslatorFactoryTests
{
    [Theory]
    [InlineData(null, "Azure")]
    [InlineData("", "Azure")]
    [InlineData("Bogus", "Azure")]
    [InlineData("Azure", "Azure")]
    [InlineData("azure", "Azure")]
    [InlineData("Google", "Google")]
    [InlineData("google", "Google")]
    [InlineData("Cloudflare", "Cloudflare")]
    [InlineData("CLOUDFLARE", "Cloudflare")]
    public void NormalizeProvider_BadValue_FallsBackToAzure(string? input, string expected)
    {
        Assert.Equal(expected, FreeApiTranslatorFactory.NormalizeProvider(input));
    }

    [Fact]
    public void Create_AzureWithoutKey_ReturnsNull()
    {
        Assert.Null(FreeApiTranslatorFactory.Create("Azure", "", null, null, null));
        Assert.Null(FreeApiTranslatorFactory.Create("Azure", null, null, null, null));
    }

    [Fact]
    public void Create_AzureWithKey_ReturnsAzureTranslator()
    {
        using var t = FreeApiTranslatorFactory.Create("Azure", "key123", "eastasia", null, null);
        Assert.IsType<AzureTranslator>(t);
        Assert.Equal("Azure", t!.ProviderName);
    }

    [Fact]
    public void Create_GoogleWithoutKey_ReturnsNull()
    {
        Assert.Null(FreeApiTranslatorFactory.Create("Google", "", null, null, null));
    }

    [Fact]
    public void Create_GoogleWithKey_ReturnsGoogleTranslator()
    {
        using var t = FreeApiTranslatorFactory.Create("Google", "key123", null, null, null);
        Assert.IsType<GoogleTranslator>(t);
        Assert.Equal("Google", t!.ProviderName);
    }

    [Fact]
    public void Create_CloudflareWithoutCredentials_ReturnsNull()
    {
        Assert.Null(FreeApiTranslatorFactory.Create("Cloudflare", null, null, "acct", ""));
        Assert.Null(FreeApiTranslatorFactory.Create("Cloudflare", null, null, "", "tok"));
        Assert.Null(FreeApiTranslatorFactory.Create("Cloudflare", null, null, null, null));
    }

    [Fact]
    public void Create_CloudflareWithCredentials_ReturnsCloudflareTranslator()
    {
        using var t = FreeApiTranslatorFactory.Create("Cloudflare", null, null, "acct", "tok");
        Assert.IsType<CloudflareTranslator>(t);
        Assert.Equal("Cloudflare", t!.ProviderName);
    }

    [Fact]
    public void Create_UnknownProvider_FallsBackToAzure()
    {
        using var t = FreeApiTranslatorFactory.Create("Bogus", "key123", null, null, null);
        Assert.IsType<AzureTranslator>(t);
    }
}
