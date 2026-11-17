using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TiviMateWin.Services;

/// <summary>
/// 模型文件路径处理：llama.cpp / whisper.cpp 是原生代码，在含中文（非 ASCII）
/// 的路径下打不开模型文件（如台式机用户文件夹名是中文，翻译模型加载失败后
/// 静默回退只显示英文，听写 5 秒多、翻译 0ms、端到端 10 秒）。
/// .NET 自己的读写（下载模型）不受影响，只有传给原生加载器的路径需要处理。
/// 优先零复制的 8.3 短路径；没有短路径时把模型复制一份到纯英文缓存目录
/// （程序目录 models 下，写不进去时退到 C:\ProgramData\TiviMateWin\models），
/// 复制后复用（长度一致不再复制）；实在没辙才返回原路径、由调用方记日志。
/// 路径本身已是纯英文时原样返回，不做任何事。
/// </summary>
internal static class LiveSubtitleModelPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>路径是否全是 ASCII 字符（原生加载器能直接打开）。</summary>
    public static bool IsAscii(string path)
    {
        foreach (var ch in path)
        {
            if (ch > 127) return false;
        }
        return true;
    }

    /// <summary>
    /// 返回能传给原生模型加载器的路径：纯英文原样返回；含中文时先试 8.3 短路径，
    /// 再试复制到纯英文缓存目录；都失败返回原路径（调用方按加载失败记日志）。
    /// </summary>
    public static string ResolveForNativeLoad(string modelPath)
    {
        if (IsAscii(modelPath)) return modelPath;

        // 1. 8.3 短路径（零复制；系统盘没开 8.3 时返回原长路径，走下一步）。
        try
        {
            var sb = new StringBuilder(4096);
            var len = GetShortPathName(modelPath, sb, (uint)sb.Capacity);
            if (len > 0 && len < sb.Capacity)
            {
                var shortPath = sb.ToString();
                if (IsAscii(shortPath) && File.Exists(shortPath))
                {
                    AppLog.Info($"model path has non-ASCII characters, using short path: {shortPath}");
                    return shortPath;
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 Windows（单元测试跑在 Linux 上）：没有 kernel32，直接走复制兜底。
        }

        // 2. 复制到纯英文缓存目录（按顺序试，第一个写得进去的为准）。
        var fileName = Path.GetFileName(modelPath);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TiviMateWin", "models"),
        };
        foreach (var dir in candidates)
        {
            if (!IsAscii(dir)) continue;
            try
            {
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, fileName);
                var source = new FileInfo(modelPath);
                if (!File.Exists(dest) || new FileInfo(dest).Length != source.Length)
                    File.Copy(modelPath, dest, overwrite: true);
                AppLog.Info($"model path has non-ASCII characters, using ASCII copy: {dest}");
                return dest;
            }
            catch (Exception ex)
            {
                AppLog.Error($"model ASCII copy failed [{dir}]: {ex.Message}");
            }
        }

        AppLog.Error($"model path has non-ASCII characters and no ASCII fallback worked: {modelPath}");
        return modelPath;
    }
}
