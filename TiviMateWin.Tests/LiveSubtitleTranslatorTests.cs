using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>
/// 实时字幕翻译器测试：提示词格式、译文清洗、中文判定、缓存、失败熔断。
/// 推理用注入的假函数（不加载真模型、不联网）；真模型只在云电脑实测。
/// </summary>
public class LiveSubtitleTranslatorTests
{
    private static (LiveSubtitleTranslator Translator, Func<int> Calls) TranslatorWithFake(
        Func<string, CancellationToken, Task<string?>> generate)
    {
        var calls = 0;
        Func<string, CancellationToken, Task<string?>> counting = (text, ct) =>
        {
            calls++;
            return generate(text, ct);
        };
        var translator = new LiveSubtitleTranslator(
            modelPath: Path.Combine(Path.GetTempPath(), "no-such-model.gguf"),
            generateOverride: counting);
        return (translator, () => calls);
    }

    [Fact]
    public void BuildPrompt_ChatMlFormat()
    {
        var prompt = LiveSubtitleTranslator.BuildPrompt("Hello world");
        Assert.StartsWith("<|im_start|>system\n", prompt);
        Assert.Contains("subtitle translator", prompt);
        Assert.Contains("<|im_start|>user\nHello world<|im_end|>", prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
    }

    [Theory]
    [InlineData("  你好世界  ", "你好世界")]
    [InlineData("\"你好世界\"", "你好世界")]
    [InlineData("“你好世界”", "你好世界")]
    [InlineData("翻译：你好世界", "你好世界")]
    [InlineData("翻译:你好世界", "你好世界")]
    [InlineData("Translation: 你好世界", "你好世界")]
    [InlineData("你好世界<|im_end|>", "你好世界")]
    [InlineData("你好世界<|endoftext|>", "你好世界")]
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
        var (translator, _) = TranslatorWithFake((_, _) => Task.FromResult<string?>("  \"你好世界\" "));
        var result = await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Equal("你好世界", result);
    }

    [Fact]
    public async Task TranslateAsync_SameTextTwice_GeneratesOnce()
    {
        var (translator, calls) = TranslatorWithFake((_, _) => Task.FromResult<string?>("你好世界"));
        await translator.TranslateAsync("Hello world", CancellationToken.None);
        await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_DoesNotGenerate()
    {
        var (translator, calls) = TranslatorWithFake((_, _) => Task.FromResult<string?>("你好世界"));
        var result = await translator.TranslateAsync("   ", CancellationToken.None);
        Assert.Equal("", result);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task TranslateAsync_ChineseText_ReturnsAsIsWithoutGenerate()
    {
        var (translator, calls) = TranslatorWithFake((_, _) => Task.FromResult<string?>("你好世界"));
        var result = await translator.TranslateAsync("你好世界", CancellationToken.None);
        Assert.Equal("你好世界", result);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task TranslateAsync_GenerateThrows_ReturnsNull()
    {
        var (translator, _) = TranslatorWithFake((_, _) => throw new InvalidOperationException("boom"));
        var result = await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Null(result);
        Assert.False(translator.IsBroken);
    }

    [Fact]
    public async Task TranslateAsync_EmptyResult_ReturnsNull()
    {
        var (translator, _) = TranslatorWithFake((_, _) => Task.FromResult<string?>("   "));
        var result = await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TranslateAsync_ThreeConsecutiveFailures_Breaks()
    {
        var (translator, calls) = TranslatorWithFake((_, _) => throw new InvalidOperationException("boom"));
        Assert.Null(await translator.TranslateAsync("one", CancellationToken.None));
        Assert.Null(await translator.TranslateAsync("two", CancellationToken.None));
        Assert.False(translator.IsBroken);
        Assert.Null(await translator.TranslateAsync("three", CancellationToken.None));
        Assert.True(translator.IsBroken);
        Assert.Equal(3, translator.ConsecutiveFailures);
        // 熔断后不再调推理
        Assert.Null(await translator.TranslateAsync("four", CancellationToken.None));
        Assert.Equal(3, calls());
    }

    [Fact]
    public async Task TranslateAsync_SuccessResetsFailureCount()
    {
        var fail = true;
        var (translator, _) = TranslatorWithFake((_, _) => fail
            ? throw new InvalidOperationException("boom")
            : Task.FromResult<string?>("你好世界"));
        await translator.TranslateAsync("one", CancellationToken.None);
        await translator.TranslateAsync("two", CancellationToken.None);
        Assert.Equal(2, translator.ConsecutiveFailures);
        fail = false;
        Assert.Equal("你好世界", await translator.TranslateAsync("three", CancellationToken.None));
        Assert.Equal(0, translator.ConsecutiveFailures);
        Assert.False(translator.IsBroken);
    }

    [Fact]
    public async Task TranslateAsync_NoModelFileNoOverride_ReturnsNullWithoutThrow()
    {
        // 没有注入假推理、模型文件也不存在：直接返回 null，不抛异常、不下载。
        var translator = new LiveSubtitleTranslator(
            modelPath: Path.Combine(Path.GetTempPath(), "no-such-model.gguf"));
        var result = await translator.TranslateAsync("Hello world", CancellationToken.None);
        Assert.Null(result);
    }
}
