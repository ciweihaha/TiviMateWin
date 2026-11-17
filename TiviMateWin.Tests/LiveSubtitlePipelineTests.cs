using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>
/// 两级流水线测试：听写与翻译并行——听写下一窗的同时翻译上一窗。
/// 用注入的假听写/假翻译（不加载真模型、不联网）；真模型只在云电脑实测。
/// </summary>
public class LiveSubtitlePipelineTests
{
    /// <summary>非静音窗：交替正负大样点，能量门不过滤。</summary>
    private static short[] LoudWindow(int seconds = 3)
    {
        var mono = new short[16000 * seconds];
        for (var i = 0; i < mono.Length; i++) mono[i] = (short)(i % 2 == 0 ? 8000 : -8000);
        return mono;
    }

    [Fact]
    public async Task EnqueueAndProcess_EmitsTranslatedSubtitle_InOrder()
    {
        var emitted = new List<LiveSubtitleLine>();
        var done = new TaskCompletionSource();
        var pipeline = new LiveSubtitlePipeline(
            transcribe: (_, _) => Task.FromResult<string?>("hello world"),
            translate: (text, _) => Task.FromResult<string?>("你好世界"));
        pipeline.SubtitleReady += line =>
        {
            lock (emitted)
            {
                emitted.Add(line);
                if (emitted.Count == 3) done.TrySetResult();
            }
        };
        pipeline.Start();
        pipeline.EnqueueWindow(LoudWindow());
        pipeline.EnqueueWindow(LoudWindow());
        pipeline.EnqueueWindow(LoudWindow());
        await Task.WhenAny(done.Task, Task.Delay(5000));
        pipeline.Stop();
        Assert.Equal(3, emitted.Count);
        Assert.All(emitted, line =>
        {
            Assert.Equal("hello world", line.English);
            Assert.Equal("你好世界", line.Chinese);
        });
    }

    [Fact]
    public async Task Pipeline_OverlapsTranscribeAndTranslate()
    {
        // 听写 200ms + 翻译 200ms：串行两窗要 800ms；两级流水线第二条字幕应在 600ms 左右出来。
        var emitted = new List<LiveSubtitleLine>();
        var secondDone = new TaskCompletionSource();
        var pipeline = new LiveSubtitlePipeline(
            transcribe: async (_, ct) => { await Task.Delay(200, ct); return "hello"; },
            translate: async (_, ct) => { await Task.Delay(200, ct); return "你好"; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        pipeline.SubtitleReady += line =>
        {
            lock (emitted)
            {
                emitted.Add(line);
                if (emitted.Count == 2) secondDone.TrySetResult();
            }
        };
        pipeline.Start();
        pipeline.EnqueueWindow(LoudWindow());
        pipeline.EnqueueWindow(LoudWindow());
        await Task.WhenAny(secondDone.Task, Task.Delay(5000));
        var elapsed = sw.ElapsedMilliseconds;
        pipeline.Stop();
        Assert.Equal(2, emitted.Count);
        // 串行要 800ms；流水线重叠后约 600ms，留 150ms 余量给调度抖动。
        Assert.True(elapsed < 750, $"第二条字幕 {elapsed}ms 才出，未见流水线重叠（串行要 800ms）");
    }

    [Fact]
    public async Task Pipeline_TranslateQueueFull_DropsOldest_KeepsNewest()
    {
        // 听写 50ms、翻译 300ms：翻译跟不上，队列满时丢最旧，保最新。
        var emitted = new List<string>();
        var allDone = new TaskCompletionSource();
        var seq = 0;
        var pipeline = new LiveSubtitlePipeline(
            transcribe: async (_, ct) =>
            {
                await Task.Delay(50, ct);
                return "line " + Interlocked.Increment(ref seq);
            },
            translate: async (text, ct) =>
            {
                await Task.Delay(300, ct);
                return "译 " + text;
            });
        pipeline.SubtitleReady += line =>
        {
            lock (emitted)
            {
                emitted.Add(line.English);
                if (emitted.Count >= 3) allDone.TrySetResult();
            }
        };
        pipeline.Start();
        for (var i = 0; i < 6; i++) pipeline.EnqueueWindow(LoudWindow());
        await Task.WhenAny(allDone.Task, Task.Delay(8000));
        pipeline.Stop();
        Assert.True(pipeline.WindowsDropped > 0, "翻译跟不上时应丢窗");
        Assert.True(emitted.Count < 6, "6 窗全出说明没丢，与慢翻译矛盾");
        // 最后一条必须是最新窗（丢旧保新），且顺序递增。
        Assert.Equal("line 6", emitted[^1]);
        var numbers = emitted.Select(s => int.Parse(s["line ".Length..])).ToList();
        Assert.Equal(numbers, numbers.OrderBy(n => n).ToList());
    }

    [Fact]
    public async Task Pipeline_SilentWindow_Skipped()
    {
        var emitted = 0;
        var done = new TaskCompletionSource();
        var pipeline = new LiveSubtitlePipeline(
            transcribe: (_, _) => Task.FromResult<string?>("hello"),
            translate: (_, _) => Task.FromResult<string?>("你好"));
        pipeline.SubtitleReady += _ =>
        {
            Interlocked.Increment(ref emitted);
            done.TrySetResult();
        };
        pipeline.Start();
        pipeline.EnqueueWindow(new short[16000 * 3]); // 全零：静音窗
        pipeline.EnqueueWindow(LoudWindow());
        await Task.WhenAny(done.Task, Task.Delay(5000));
        await Task.Delay(300); // 给静音窗留足“本应处理”的时间
        pipeline.Stop();
        Assert.Equal(1, emitted);
    }

    [Fact]
    public async Task Pipeline_WholeMarkerText_Skipped()
    {
        // Whisper 整窗只出 "(upbeat music)" 这类标记：不显示、不翻译。
        var emitted = 0;
        var pipeline = new LiveSubtitlePipeline(
            transcribe: (_, _) => Task.FromResult<string?>("(upbeat music)"),
            translate: (_, _) => Task.FromResult<string?>("（欢快音乐）"));
        pipeline.SubtitleReady += _ => Interlocked.Increment(ref emitted);
        pipeline.Start();
        pipeline.EnqueueWindow(LoudWindow());
        await Task.Delay(800);
        pipeline.Stop();
        Assert.Equal(0, emitted);
    }

    [Fact]
    public async Task Pipeline_TranslatorBroken_RaisesEventAndStops()
    {
        var broken = new TaskCompletionSource();
        var emitted = 0;
        var pipeline = new LiveSubtitlePipeline(
            transcribe: (_, _) => Task.FromResult<string?>("hello"),
            translate: (_, _) => Task.FromResult<string?>("你好"),
            isTranslatorBroken: () => true);
        pipeline.TranslatorBroken += () => broken.TrySetResult();
        pipeline.SubtitleReady += _ => Interlocked.Increment(ref emitted);
        pipeline.Start();
        pipeline.EnqueueWindow(LoudWindow());
        await Task.WhenAny(broken.Task, Task.Delay(5000));
        // 熔断后不再出字幕：再喂两窗，等一会儿，计数不应再涨。
        var countAfterBreak = emitted;
        pipeline.EnqueueWindow(LoudWindow());
        pipeline.EnqueueWindow(LoudWindow());
        await Task.Delay(800);
        pipeline.Stop();
        Assert.True(broken.Task.IsCompleted, "翻译熔断应触发 TranslatorBroken 事件");
        Assert.Equal(countAfterBreak, emitted);
    }

    [Fact]
    public async Task Pipeline_EndToEndLatency_Recorded()
    {
        var done = new TaskCompletionSource();
        var pipeline = new LiveSubtitlePipeline(
            transcribe: async (_, ct) => { await Task.Delay(200, ct); return "hello"; },
            translate: async (_, ct) => { await Task.Delay(200, ct); return "你好"; });
        pipeline.SubtitleReady += _ => done.TrySetResult();
        pipeline.Start();
        pipeline.EnqueueWindow(LoudWindow());
        await Task.WhenAny(done.Task, Task.Delay(5000));
        pipeline.Stop();
        // 端到端 = 入队→发出，至少包含听写+翻译的 400ms。
        Assert.True(pipeline.LastEndToEndMs >= 350,
            $"端到端延迟 {pipeline.LastEndToEndMs}ms 小于听写+翻译耗时，不可信");
        Assert.True(pipeline.LastTranscribeMs >= 150, "应记下听写耗时");
        Assert.True(pipeline.LastTranslateMs >= 150, "应记下翻译耗时");
    }

    [Fact]
    public async Task Pipeline_Stop_NoMoreSubtitles()
    {
        var emitted = 0;
        var pipeline = new LiveSubtitlePipeline(
            transcribe: async (_, ct) => { await Task.Delay(300, ct); return "hello"; },
            translate: async (_, ct) => { await Task.Delay(300, ct); return "你好"; });
        pipeline.SubtitleReady += _ => Interlocked.Increment(ref emitted);
        pipeline.Start();
        pipeline.EnqueueWindow(LoudWindow());
        pipeline.Stop();
        await Task.Delay(1000);
        var count = emitted;
        pipeline.EnqueueWindow(LoudWindow());
        await Task.Delay(1000);
        Assert.Equal(count, emitted);
    }
}
