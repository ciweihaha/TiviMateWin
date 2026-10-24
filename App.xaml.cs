using System.Windows;
using System.Windows.Threading;
using TiviMateWin.Services;

namespace TiviMateWin;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常捕获：任何未处理异常都记录日志并提示，避免静默崩溃。
        // F10 适配说明：审计原文提到的 _fetch / REQUEST_TIMEOUT / callerSignal 在当前仓库中不存在
        // （全仓 grep 均为 0），本文件也没有网络 fetch 逻辑，故无法按原文逐字改动，也未引入
        // JS 语义的超时错误类型。本次按实际存在的全局异常处理做同类意图适配：
        // （1）区分取消与真实异常：取消类异常（含内层）视为良性，只记日志、不弹窗；
        // （2）真实异常不再“静默标记已处理后继续运行”，而是记脱敏日志并弹窗提示；
        // （3）日志侧对异常文本做与 F1 相同的 URL 脱敏（AppConfig.RedactUrlsInText）。
        // 局限：全局处理拿不到调用方的 CancellationToken，无法区分“调用方取消”与
        // “HttpClient 超时”（两者在 .NET 中均为 TaskCanceledException 语义），故统一按取消类处理。
        DispatcherUnhandledException += (_, args) =>
        {
            var detail = AppConfig.RedactUrlsInText(args.Exception.ToString());
            if (IsCancellation(args.Exception))
            {
                AppLog.Error($"unhandled UI exception (cancelled): {detail}");
            }
            else
            {
                AppLog.Error($"unhandled UI exception: {detail}");
                try
                {
                    MessageBox.Show($"发生未处理异常，已记录日志。建议重启应用以恢复到干净状态：\n{TruncateForMessage(detail)}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
            args.Handled = true;   // 标记已处理，避免进程崩溃
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // 该处理无法设置 Handled，保持原行为，只做日志脱敏。
            AppLog.Error($"unhandled domain exception: {AppConfig.RedactUrlsInText(args.ExceptionObject?.ToString() ?? "")}");
        };

        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--selftest") { SelfTest.Run(null, false); Shutdown(); return; }
            if (e.Args[0] == "--playtest") { SelfTest.Run(e.Args.Length > 1 ? e.Args[1] : null, true); Shutdown(); return; }
        }
        base.OnStartup(e);
    }

    /// <summary>异常链中是否含取消类异常（OperationCanceledException / TaskCanceledException）。</summary>
    private static bool IsCancellation(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is OperationCanceledException) return true;
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>弹窗用异常文本截断，避免超长堆栈撑满对话框。</summary>
    private static string TruncateForMessage(string text) =>
        text.Length > 500 ? text[..500] + "…" : text;
}
