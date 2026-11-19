using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>微软 Azure Translator 免费接口翻译测试（v1.4.33 重构）：
/// 免费层 F0 每月 200 万字符。覆盖：响应解析（成功/失败/缺字段/非法 JSON）、
/// 请求地址与请求体、Key/区域头（区域只在填了时才发）、缓存、
/// HTTP 失败与超时返回 null（不抛异常，调用方只跳过该行）。</summary>
public class AzureTranslatorTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (Responder is not null) return await Responder(request, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"translations\":[{\"text\":\"你好，世界\",\"to\":\"zh-Hans\"}]}]",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void BuildUrl_UsesTranslatorEndpoint()
    {
        Assert.Equal(
            "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hans",
            AzureTranslator.BuildUrl());
    }

    [Fact]
    public void BuildRequestBody_HasTextArray()
    {
        var body = AzureTranslator.BuildRequestBody("Hello world");
        Assert.Contains("\"Text\":\"Hello world\"", body);
    }

    [Fact]
    public void ParseTranslatedText_Success()
    {
        Assert.Equal("你好，世界",
            AzureTranslator.ParseTranslatedText(
                "[{\"translations\":[{\"text\":\"你好，世界\",\"to\":\"zh-Hans\"}]}]"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"translations\":[]}]")]
    [InlineData("[{\"translations\":[{}]}]")]
    [InlineData("[{\"translations\":[{\"text\":\"   \"}]}]")]
    [InlineData("not json")]
    public void ParseTranslatedText_Failure_ReturnsNull(string json)
    {
        Assert.Null(AzureTranslator.ParseTranslatedText(json));
    }

    [Fact]
    public async Task TranslateAsync_Success_ReturnsChinese_AndSendsKeyHeader()
    {
        var handler = new FakeHandler();
        var t = new AzureTranslator("key123", "eastasia", new HttpClient(handler));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var keys));
        Assert.Contains("key123", keys);
        Assert.True(handler.LastRequest.Headers.TryGetValues("Ocp-Apim-Subscription-Region", out var regions));
        Assert.Contains("eastasia", regions);
    }

    [Fact]
    public async Task TranslateAsync_NoRegion_DoesNotSendRegionHeader()
    {
        var handler = new FakeHandler();
        var t = new AzureTranslator("key123", null, new HttpClient(handler));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.False(handler.LastRequest!.Headers.Contains("Ocp-Apim-Subscription-Region"));
    }

    [Fact]
    public async Task TranslateAsync_HttpError_ReturnsNull()
    {
        var handler = new FakeHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
        };
        var t = new AzureTranslator("key123", null, new HttpClient(handler));
        Assert.Null(await t.TranslateAsync("Hello world", CancellationToken.None));
    }

    [Fact]
    public async Task TranslateAsync_MissingKey_ReturnsNullWithoutHttp()
    {
        var calls = 0;
        var handler = new FakeHandler
        {
            Responder = (_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
        };
        var t = new AzureTranslator("", null, new HttpClient(handler));
        Assert.Null(await t.TranslateAsync("Hello", CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TranslateAsync_ChineseInput_ReturnsOriginalWithoutHttp()
    {
        var calls = 0;
        var handler = new FakeHandler
        {
            Responder = (_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
        };
        var t = new AzureTranslator("key123", null, new HttpClient(handler));
        Assert.Equal("你好世界", await t.TranslateAsync("你好世界", CancellationToken.None));
        Assert.Equal("", await t.TranslateAsync("   ", CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TranslateAsync_CacheHits_DoNotCallHttpTwice()
    {
        var calls = 0;
        var handler = new FakeHandler
        {
            Responder = (_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"translations\":[{\"text\":\"你好，世界\",\"to\":\"zh-Hans\"}]}]",
                        Encoding.UTF8, "application/json"),
                });
            },
        };
        var t = new AzureTranslator("key123", null, new HttpClient(handler));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.Equal(1, calls);
    }
}
