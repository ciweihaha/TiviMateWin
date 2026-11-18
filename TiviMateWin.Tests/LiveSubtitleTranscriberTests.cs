using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>
/// 听写防停滞回归测试（v1.4.30）：噪声/音乐窗在默认温度回退下要重解多遍、个别窗
/// 卡 5–12 秒——单段输出 + 每段 token 上限 + 单窗超时兜底，超时放弃当窗不卡管线。
/// 真模型推理只在真机上验证；这里覆盖不依赖模型的常量与计数口径。
/// </summary>
public class LiveSubtitleTranscriberTests
{
    [Fact]
    public void TranscribeTimeout_IsSixSeconds()
    {
        // 正常窗约 1–2 秒；6 秒兜底只截病态窗，不影响正常听写。
        Assert.Equal(6, LiveSubtitleTranscriber.TranscribeTimeoutSeconds);
    }

    [Fact]
    public void MaxTokensPerSegment_CapsHallucinatedRuns()
    {
        // 1.5 秒窗正常约 6–15 个 token；64 留足余量，同时截住默认 448 token 的长生成。
        Assert.Equal(64, LiveSubtitleTranscriber.MaxTokensPerSegment);
        Assert.True(LiveSubtitleTranscriber.MaxTokensPerSegment > 32);
    }

    [Fact]
    public void Counters_StartAtZero()
    {
        var transcriber = new LiveSubtitleTranscriber(
            modelPath: Path.Combine(Path.GetTempPath(), "tivimatewin-test-missing", "ggml-tiny.en.bin"));
        Assert.Equal(0, transcriber.TranscribeTimeouts);
        Assert.Equal(0, transcriber.LastSetupMs);
        Assert.Equal(0, transcriber.LastTranscribeMs);
        Assert.False(transcriber.ModelReady);
    }

    [Fact]
    public async Task TranscribeAsync_ModelMissing_ReturnsNullQuickly()
    {
        var transcriber = new LiveSubtitleTranscriber(
            modelPath: Path.Combine(Path.GetTempPath(), "tivimatewin-test-missing", "ggml-tiny.en.bin"));
        var result = await transcriber.TranscribeAsync(new short[24000], CancellationToken.None);
        Assert.Null(result);
        // 没模型直接返回，不应记超时。
        Assert.Equal(0, transcriber.TranscribeTimeouts);
    }

    [Fact]
    public void Dispose_NoModel_DoesNotThrow_AndIsIdempotent()
    {
        // v1.4.31：关字幕时 Stop 后立刻 Dispose——释放前要等在途听写收尾（等门）；
        // 无模型/无在途听写时应立刻返回、不抛异常；二次 Dispose 也安全。
        var transcriber = new LiveSubtitleTranscriber(
            modelPath: Path.Combine(Path.GetTempPath(), "tivimatewin-test-missing", "ggml-tiny.en.bin"));
        var ex = Record.Exception(() => transcriber.Dispose());
        Assert.Null(ex);
        ex = Record.Exception(() => transcriber.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task TranscribeAsync_AfterDispose_ReturnsNull()
    {
        // v1.4.31：Dispose 后再调听写直接返回 null，不碰已释放的门/工厂。
        var transcriber = new LiveSubtitleTranscriber(
            modelPath: Path.Combine(Path.GetTempPath(), "tivimatewin-test-missing", "ggml-tiny.en.bin"));
        transcriber.Dispose();
        var result = await transcriber.TranscribeAsync(new short[24000], CancellationToken.None);
        Assert.Null(result);
    }
}
