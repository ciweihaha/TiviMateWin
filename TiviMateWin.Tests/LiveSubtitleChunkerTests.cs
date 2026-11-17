using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>实时字幕切块器测试：重采样、定长切窗、能量门、WAV 构造、非语音标记过滤。</summary>
public class LiveSubtitleChunkerTests
{
    [Fact]
    public void Resample_Stereo48kToMono16k_AveragesThreeFrames()
    {
        // 3 个立体声帧（L,R 交错），每帧值相同：均值应为该值
        short[] stereo = { 1000, 1000, 2000, 2000, 3000, 3000 };
        var mono = LiveSubtitleChunker.ResampleStereo48kToMono16k(stereo);
        Assert.Single(mono);
        Assert.Equal(2000, mono[0]);
    }

    [Fact]
    public void Resample_OneSecondStereo_Yields16000MonoSamples()
    {
        var stereo = new short[48000 * 2];
        for (var i = 0; i < stereo.Length; i++) stereo[i] = 1000;
        var mono = LiveSubtitleChunker.ResampleStereo48kToMono16k(stereo);
        Assert.Equal(16000, mono.Length);
        Assert.All(mono, s => Assert.Equal(1000, s));
    }

    [Fact]
    public void Resample_DownmixesChannels()
    {
        // 左 1000、右 3000 交错：单声道应为左右均值 2000
        short[] stereo = { 1000, 3000, 1000, 3000, 1000, 3000 };
        var mono = LiveSubtitleChunker.ResampleStereo48kToMono16k(stereo);
        Assert.Single(mono);
        Assert.Equal(2000, mono[0]);
    }

    [Fact]
    public void Feed_WindowNotFull_ReturnsNull()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 3);
        var mono = new short[16000]; // 1 秒
        Assert.Null(chunker.Feed(mono));
    }

    [Fact]
    public void Feed_WindowFull_ReturnsWindow()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 3);
        var mono = new short[48000]; // 3 秒
        var window = chunker.Feed(mono);
        Assert.NotNull(window);
        Assert.Equal(48000, window!.Length);
    }

    [Fact]
    public void Feed_PartialWindows_AccumulateAcrossCalls()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 3);
        Assert.Null(chunker.Feed(new short[16000]));
        Assert.Null(chunker.Feed(new short[16000]));
        var window = chunker.Feed(new short[16000]);
        Assert.NotNull(window);
        Assert.Equal(48000, window!.Length);
    }

    [Fact]
    public void Feed_Overflow_StartsNextWindow()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 3);
        var window = chunker.Feed(new short[48000 + 16000]); // 4 秒：满一窗，余 1 秒
        Assert.NotNull(window);
        Assert.Equal(48000, window!.Length);
        Assert.Null(chunker.Feed(new short[16000])); // 再 1 秒，余量 2 秒，未满
        Assert.NotNull(chunker.Feed(new short[16000])); // 再 1 秒，满
    }

    [Fact]
    public void Feed_MultiWindowInput_QueuesSecondWindow()
    {
        var chunker = new LiveSubtitleChunker(windowSeconds: 3);
        // 7 秒一次喂入：满两窗，余 1 秒在缓冲里
        var first = chunker.Feed(new short[16000 * 7]);
        Assert.NotNull(first);
        Assert.Equal(48000, first!.Length);
        // 第二窗已排队：喂空数据也能取出来（不丢）
        var second = chunker.Feed(Array.Empty<short>());
        Assert.NotNull(second);
        Assert.Equal(48000, second!.Length);
        // 余 1 秒，再喂 2 秒满第三窗
        Assert.Null(chunker.Feed(new short[16000]));
        Assert.NotNull(chunker.Feed(new short[16000]));
    }

    [Fact]
    public void IsSilent_AllZero_ReturnsTrue()
    {
        Assert.True(LiveSubtitleChunker.IsSilent(new short[16000]));
    }

    [Fact]
    public void IsSilent_LoudSpeech_ReturnsFalse()
    {
        var mono = new short[16000];
        for (var i = 0; i < mono.Length; i++) mono[i] = (short)(i % 2 == 0 ? 8000 : -8000);
        Assert.False(LiveSubtitleChunker.IsSilent(mono));
    }

    [Fact]
    public void BuildWav_HasRiffHeaderAndData()
    {
        var mono = new short[16000];
        var wav = LiveSubtitleChunker.BuildWav(mono);
        Assert.Equal(44 + 16000 * 2, wav.Length);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);
        // 采样率 16000 写在字节 24-27（小端）
        Assert.Equal(16000, wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24));
        // 声道数 1 写在字节 22-23
        Assert.Equal(1, wav[22] | (wav[23] << 8));
    }

    [Theory]
    [InlineData("[music]", true)]
    [InlineData("[applause]", true)]
    [InlineData("[inaudible]", true)]
    [InlineData("Hello world", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void IsNonSpeechMarker_FiltersBracketedMarkers(string text, bool expected)
    {
        Assert.Equal(expected, LiveSubtitleChunker.IsNonSpeechMarker(text));
    }
}
