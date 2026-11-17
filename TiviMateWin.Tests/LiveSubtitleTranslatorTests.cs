using System.Net;
using System.Text;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>实时字幕翻译器测试：译文清洗、中文判定、缓存、模型检查（用假 HTTP  handler，不联网）。</summary>
public class LiveSubtitleTranslatorTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static LiveSubtitleTranslator TranslatorWithFakeChat(FakeHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        return new LiveSubtitleTranslator("http://127.0.0.1:11434", "qwen2.5:1.5b", http);
    }

    [Theory]
    [InlineData("  你好世界  ", "你好世界")]
    [InlineData("\"你好世界\"", "你好世界")]
    [InlineData("“你好世界”", "你好世界")]
    [InlineData("翻译：你好世界", "你好世界")]
    [InlineData("翻译:你好世界", "你好世界")]
    [InlineData("Translation: 你好世界", "你好世界")]
    public void CleanTranslation_StripsWrapping(string input, string expected)
    {
        Assert.Equal(expected, LiveSubtitleTranslator.CleanTranslation(input));
    }

    [Theory]
    [InlineData("你好", true)]
    [InlineData("Hello 你好", true)]
    [InlineData("Hello world", false)]
    [InlineData("", false)]
    public void IsChineseText_DetectsHan(string input, bool expected)
    {
        Assert.Equal(expected, LiveSubtitleTranslator.IsChineseText(input));
    }

    [Fact]
    public async Task TranslateAsync_ReturnsCleanedTranslation()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            "{\"message\":{\"content\":\"  \\\"你好世界\\\" \"}}"));
        var translator = TranslatorWithFakeChat(handler);
        var result = await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Equal("你好世界", result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TranslateAsync_SameTextTwice_CallsHttpOnce()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            "{\"message\":{\"content\":\"你好世界\"}}"));
        var translator = TranslatorWithFakeChat(handler);
        await translator.TranslateAsync("Hello world", CancellationToken.None);
        await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_DoesNotCallHttp()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            "{\"message\":{\"content\":\"\"}}"));
        var translator = TranslatorWithFakeChat(handler);
        var result = await translator.TranslateAsync("   ", CancellationToken.None);
        Assert.Equal("", result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TranslateAsync_ChineseText_ReturnsAsIsWithoutHttp()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            "{\"message\":{\"content\":\"\"}}"));
        var translator = TranslatorWithFakeChat(handler);
        var result = await translator.TranslateAsync("你好世界", CancellationToken.None);
        Assert.Equal("你好世界", result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TranslateAsync_HttpError_ReturnsNull()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var translator = TranslatorWithFakeChat(handler);
        var result = await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task CheckModelAsync_ModelPresent_ReturnsTrue()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            "{\"models\":[{\"name\":\"qwen2.5:1.5b\"}]}"));
        var translator = TranslatorWithFakeChat(handler);
        Assert.True(await translator.CheckModelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckModelAsync_ModelMissing_ReturnsFalse()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            "{\"models\":[{\"name\":\"qwen2.5:3b\"}]}"));
        var translator = TranslatorWithFakeChat(handler);
        Assert.False(await translator.CheckModelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckModelAsync_HttpError_ReturnsFalse()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var translator = TranslatorWithFakeChat(handler);
        Assert.False(await translator.CheckModelAsync(CancellationToken.None));
    }
}
