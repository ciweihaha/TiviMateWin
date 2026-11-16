using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace TiviMateWin.Services;

/// <summary>
/// 小窗托盘图标：小窗最小化隐藏时出现在系统托盘，单击/双击恢复小窗，
/// 右键菜单提供“显示小窗”和“退出”；窗口恢复显示或关闭时撤掉图标。
/// 可见状态由 <see cref="PipTrayIconState"/> 管（纯逻辑，可单元测试），
/// 这里只负责 WinForms NotifyIcon 的创建/事件/释放。
/// 注意：NotifyIcon 必须在 UI 线程创建与操作，调用方都在 UI 线程；
/// 万一从后台线程进来，用 Dispatcher 转回 UI 线程。
/// </summary>
public sealed class PipTrayIcon : IDisposable
{
    private readonly PipTrayIconState _state = new();
    private readonly Action _onRestore;
    private readonly Action _onExit;
    private NotifyIcon? _icon;
    private ContextMenuStrip? _menu;
    private bool _disposed;

    /// <summary>当前托盘图标是否应显示（与状态机一致）。</summary>
    public bool IconVisible => _state.IconVisible;

    public PipTrayIcon(Action onRestore, Action onExit)
    {
        _onRestore = onRestore;
        _onExit = onExit;
    }

    /// <summary>小窗隐藏时调用：显示托盘图标（已显示则什么都不做）。</summary>
    public void Show()
    {
        RunOnUiThread(() =>
        {
            _state.NotifyMinimized();
            if (_disposed || _icon != null) return;
            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem("显示小窗", null, (_, _) => _onRestore()));
            _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => _onExit()));
            _icon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "TiviMateWin - 小窗播放中",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            // 单击/双击都恢复小窗：恢复是幂等的，双击多触发一次 Click 也无害。
            _icon.Click += (_, _) => _onRestore();
            _icon.DoubleClick += (_, _) => _onRestore();
        });
    }

    /// <summary>小窗恢复显示时调用：撤掉托盘图标。</summary>
    public void Hide()
    {
        RunOnUiThread(() =>
        {
            _state.NotifyRestored();
            DisposeIcon();
        });
    }

    public void Dispose()
    {
        RunOnUiThread(() =>
        {
            _state.NotifyClosed();
            DisposeIcon();
            _disposed = true;
        });
    }

    private void DisposeIcon()
    {
        try { if (_icon != null) _icon.Visible = false; } catch { }
        try { _icon?.Dispose(); } catch { }
        _icon = null;
        try { _menu?.Dispose(); } catch { }
        _menu = null;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = WpfApplication.GetResourceStream(
                new Uri("pack://application:,,,/Assets/tivimate-icon.ico"))?.Stream;
            if (stream != null) return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
            return;
        }
        action();
    }
}
