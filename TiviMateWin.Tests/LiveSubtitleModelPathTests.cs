using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>
/// 模型路径解析测试：纯英文原样返回；含中文时（8.3 短路径不可用时）复制到
/// 纯英文缓存目录复用。不加载真模型、不联网。
/// </summary>
public class LiveSubtitleModelPathTests
{
    [Theory]
    [InlineData(@"C:\TiviMateWin\models\qwen2.5-1.5b-instruct-q4_k_m.gguf", true)]
    [InlineData(@"C:\Users\test\AppData\Local\TiviMateWin\models\ggml-tiny.en.bin", true)]
    [InlineData(@"C:\Users\中文用户\AppData\Local\TiviMateWin\models\qwen2.5-1.5b-instruct-q4_k_m.gguf", false)]
    [InlineData("/home/test/models/ggml-tiny.en.bin", true)]
    [InlineData("/home/中文用户/models/ggml-tiny.en.bin", false)]
    public void IsAscii_DetectsNonAscii(string path, bool expected)
    {
        Assert.Equal(expected, LiveSubtitleModelPath.IsAscii(path));
    }

    [Fact]
    public void ResolveForNativeLoad_AsciiPath_ReturnedAsIs()
    {
        var path = Path.Combine(Path.GetTempPath(), "ascii-model-test.gguf");
        Assert.Same(path, LiveSubtitleModelPath.ResolveForNativeLoad(path));
    }

    [Fact]
    public void ResolveForNativeLoad_NonAsciiPath_CopiesToAsciiLocation()
    {
        // 源文件放在含中文的临时目录里（模拟台式机中文用户文件夹）；
        // Linux CI 上没有 kernel32，走复制兜底；Windows 上若有 8.3 短路径则走短路径。
        var fileName = "modelpath-test-" + Guid.NewGuid().ToString("N") + ".gguf";
        var sourceDir = Path.Combine(Path.GetTempPath(), "中文用户-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, fileName);
        var content = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(source, content);
        string? resolved = null;
        try
        {
            resolved = LiveSubtitleModelPath.ResolveForNativeLoad(source);
            Assert.True(LiveSubtitleModelPath.IsAscii(resolved), $"resolved path is not ASCII: {resolved}");
            Assert.True(File.Exists(resolved), $"resolved path does not exist: {resolved}");
            Assert.Equal(content, File.ReadAllBytes(resolved));
            // 第二次解析复用同一路径（复制过的副本不再重拷）。
            Assert.Equal(resolved, LiveSubtitleModelPath.ResolveForNativeLoad(source));
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
            try
            {
                if (resolved is not null && !string.Equals(resolved, source, StringComparison.Ordinal))
                    File.Delete(resolved);
            }
            catch { }
        }
    }
}
