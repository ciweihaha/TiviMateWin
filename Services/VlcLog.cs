using System;
using System.IO;

namespace TiviMateWin.Services;

/// <summary>LibVLC 详细日志：%APPDATA%\TiviMateWin\vlc-log.txt，超 5MB 轮转为 .old；
/// 仅在配置 VlcVerbose=true 时写入；写日志永不抛异常</summary>
public static class VlcLog
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiviMateWin", "vlc-log.txt");

    public static void Write(string level, string module, string msg)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5_000_000)
                {
                    var old = LogPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogPath, old);
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{module}] {msg}\r\n");
            }
        }
        catch { }
    }
}
