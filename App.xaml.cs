using System.Windows;
using System.Windows.Threading;
using TiviMateWin.Services;

namespace TiviMateWin;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常捕获：任何未处理异常都记录日志并提示，避免静默崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error($"unhandled UI exception: {args.Exception}");
            args.Handled = true;   // 标记已处理，避免进程崩溃
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error($"unhandled domain exception: {args.ExceptionObject}");
        };

        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--selftest") { SelfTest.Run(null, false); Shutdown(); return; }
            if (e.Args[0] == "--playtest") { SelfTest.Run(e.Args.Length > 1 ? e.Args[1] : null, true); Shutdown(); return; }
        }
        base.OnStartup(e);
    }
}
