using System.Windows;
using TiviMateWin.Services;

namespace TiviMateWin;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--selftest") { SelfTest.Run(null, false); Shutdown(); return; }
            if (e.Args[0] == "--playtest") { SelfTest.Run(e.Args.Length > 1 ? e.Args[1] : null, true); Shutdown(); return; }
        }
        base.OnStartup(e);
    }
}
