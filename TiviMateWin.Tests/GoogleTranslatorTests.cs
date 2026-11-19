using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>谷歌 Cloud Translation 免费接口翻译测试（v1.4.33 重构）：
/// 免费层每月前 50 万字符。覆盖：响应解析（成功/HTML 转义解码/失败/缺字段/非法 JSON）、
/// 请求地址与请求体、缓存、HTTP 失败与超时返回 null（不抛异常，调用方只跳过该行）。</summary>
public class GoogleTranslatorTests
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
                    "{\"data\":{\"translations\":[{\"translatedText\":\"你好，世界\"}]}}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void BuildUrl_UsesTranslateEndpointWithKey()
    {
        Assert.Equal(
            "https://translation.googleapis.com/language/translate/v2?key=key123",
            GoogleTranslator.BuildUrl("key123"));
    }

    [Fact]
    public void BuildRequestBody_HasQueryAndTarget()
    {
        var body = GoogleTranslator.BuildRequestBody("Hello world");
        Assert.Contains("\"q\":\"Hello world\"", body);
        Assert.Contains("\"target\":\"zh-CN\"", body);
    }

    [Fact]
    public void ParseTranslatedText_Success()
    {
        Assert.Equal("你好，世界",
            GoogleTranslator.ParseTranslatedText(
                "{\"data\":{\"translations\":[{\"translatedText\":\"你好，世界\"}]}}"));
    }

    [Fact]
    public void ParseTranslatedText_HtmlEscaped_Decodes()
    {
        Assert.Equal("It's fine",
            GoogleTranslator.ParseTranslatedText(
                "{\"data\":{\"translations\":[{\"translatedText\":\"It&#39;s fine\"}]}}"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"translations\":[]}}")]
    [InlineData("{\"data\":{\"translations\":[{}]}}")]
    [InlineData("{\"data\":{\"translations\":[{\"translatedText\":\"   \"}]}}")]
    [InlineData("not json")]
    public void ParseTranslatedText_Failure_ReturnsNull(string json)
    {
        Assert.Null(GoogleTranslator.ParseTranslatedText(json));
    }

    [Fact]
    public async Task TranslateAsync_Success_ReturnsChinese()
    {
        var handler = new FakeHandler();
        var t = new GoogleTranslator("key123", new HttpClient(handler));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("translation.googleapis.com/language/translate/v2",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task TranslateAsync_HttpError_ReturnsNull()
    {
        var handler = new FakeHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)),
        };
        var t = new GoogleTranslator("key123", new HttpClient(handler));
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
        var t = new GoogleTranslator("", new HttpClient(handler));
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
        var t = new GoogleTranslator("key123", new HttpClient(handler));
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
                        "{\"data\":{\"translations\":[{\"translatedText\":\"你好，世界\"}]}}",
                        Encoding.UTF8, "application/json"),
                });
            },
        };
        var t = new GoogleTranslator("key123", new HttpClient(handler));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.Equal("你好，世界", await t.TranslateAsync("Hello world", CancellationToken.None));
        Assert.Equal(1, calls);
    }
}
