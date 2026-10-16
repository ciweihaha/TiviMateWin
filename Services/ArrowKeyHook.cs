using System.Runtime.InteropServices;

namespace TiviMateWin.Services;

/// <summary>
/// 全局低级键盘钩子：无论键盘焦点在 WPF 控件还是 VLC 原生视频窗口（VLC 自有线程的 HWND），
/// ↑↓ 都能回调到应用。仅当应用窗口前台时拦截，后台时全部放行。
/// </summary>
internal static class ArrowKeyHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr _hookId = IntPtr.Zero;
    private static readonly LowLevelKeyboardProc Proc = HookCallback;

    /// <summary>↑(-1) / ↓(+1) 换台请求回调（在 UI 线程触发）</summary>
    public static Action<int>? OnArrow;

    /// <summary>返回 true 表示当前应拦截方向键（应用前台且非输入态）</summary>
    public static Func<bool>? ShouldIntercept;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        using var cur = System.Diagnostics.Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, Proc, GetModuleHandle(mod.ModuleName!), 0);
    }

    public static void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private static bool IsAppForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var vk = Marshal.ReadInt32(lParam) & 0xFF;
                if ((vk == VK_DOWN || vk == VK_UP) && IsAppForeground() && ShouldIntercept?.Invoke() == true)
                {
                    var delta = vk == VK_DOWN ? 1 : -1;
                    OnArrow?.Invoke(delta);
                    return (IntPtr)1; // 吞掉，防止焦点所在控件再响应
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
}
