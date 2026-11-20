Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# ---- user32 / mouse + foreground（SetCursorPos 会被 DPI 虚拟化 x1.25，点按用 mouse_event 绝对坐标）----
$Win32 = @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
'@
Add-Type -TypeDefinition $Win32 -ErrorAction SilentlyContinue

function Get-TmwWindow {
    # run12：不再用 Get-Process.MainWindowHandle——多窗口时它可能指向 ChannelWindow，
    # 关掉新窗口后 MainWindowHandle 会变 0/过期，导致 Get-TmwWindow 返回 null、Get-TmwWindows
    # 返回 0（D02/K03/K04/K05 的根因）。改走 EnumWindows 按标题找主窗（标题以「星辰直播」开头，
    # ChannelWindow 的标题是台名）；找不到再回落 MainWindowHandle。
    $p = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $p) { return $null }
    $mainTitle = [char]0x661F + [char]0x8FB0 + [char]0x76F4 + [char]0x64AD
    $found = $null
    $cb = [Win32+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0; [void][Win32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
        if ($wpid -eq $p.Id -and [Win32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder(256)
            [void][Win32]::GetWindowText($hWnd, $sb, 256)
            $t = $sb.ToString()
            if ($t.StartsWith($mainTitle)) { $script:TmwMainHwnd = $hWnd }
        }
        return $true
    }
    try { [void][Win32]::EnumWindows($cb, [IntPtr]::Zero) } catch { }
    if ($script:TmwMainHwnd -and $script:TmwMainHwnd -ne [IntPtr]::Zero) {
        try { return [System.Windows.Automation.AutomationElement]::FromHandle($script:TmwMainHwnd) } catch { }
    }
    if ($p.MainWindowHandle -eq 0) { return $null }
    try { return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) } catch { return $null }
}


function Invoke-UiWithTimeout {
    # run18：UIA 的 FindAll/FindFirst/Invoke/Close 在 app 卡顿时无限阻塞（run12 卡 J01 10 分钟、
    # run15 卡 K04 20 分钟、run17 卡 J01）——UIA 跨进程调用等不到回应就永远等下去。
    # 在独立 runspace 里跑，超时就放弃它、主线程继续。Start-Job 在计划任务里会挂住
    # （run9 浸泡卡死），所以用 in-process runspace，不起新进程。
    # 返回：成功时输出数组（可能为空）；超时或调用抛错时 $null。调用方按“没拿到结果”处理，绝不卡死。
    # 超时后被放弃的 runspace 线程可能仍卡在原生调用里，随进程退出而消失。
    # （探针 TMW-ProbeRunspace 已验证：Sleep 60 在 3 秒超时返回空；挂起的 app 上
    # FindAll/FindFirst 15 秒超时返回空，不再无限等。）
    param([scriptblock]$Code, [int]$TimeoutSec = 15, [string]$What = 'UIA', [object[]]$ArgumentList = @())
    $ps = [powershell]::Create()
    [void]$ps.AddScript($Code)
    foreach ($a in $ArgumentList) { [void]$ps.AddArgument($a) }
    $async = $null
    try { $async = $ps.BeginInvoke() } catch { try { $ps.Dispose() } catch { } return $null }
    $done = $async.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSec))
    if ($done) {
        try { $res = $ps.EndInvoke($async) } catch { $res = $null }
        try { $ps.Dispose() } catch { }
        if ($null -eq $res) { return @() }
        return @($res)
    }
    return $null
}


function Find-All {
    param($Root, $ControlType, [int]$TimeoutSec = 15)
    if ($null -eq $Root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    $res = Invoke-UiWithTimeout -Code {
        param($r, $c)
        @($r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c))
    } -TimeoutSec $TimeoutSec -What 'FindAll' -ArgumentList @($Root, $cond)
    if ($null -eq $res) { return @() }
    return @($res)
}

function Find-ByName {
    # run18：.Current.Name 是跨进程读，app 卡住时也会堵——过滤也放进带超时的 runspace。
    param($Root, $ControlType, [string]$NamePattern)
    $all = Find-All -Root $Root -ControlType $ControlType
    if ($all.Count -eq 0) { return @() }
    $res = Invoke-UiWithTimeout -Code {
        param($els, $pat)
        @($els | Where-Object { try { $_.Current.Name -match $pat } catch { $false } })
    } -TimeoutSec 10 -What 'Filter-ByName' -ArgumentList @($all, $NamePattern)
    if ($null -eq $res) { return @() }
    return @($res)
}

function Find-ByAutomationId {
    param($Root, [string]$AutomationId, [int]$TimeoutSec = 15)
    if ($null -eq $Root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $res = Invoke-UiWithTimeout -Code {
        param($r, $c)
        $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    } -TimeoutSec $TimeoutSec -What 'FindFirst' -ArgumentList @($Root, $cond)
    if ($null -eq $res -or @($res).Count -eq 0) { return $null }
    return @($res)[0]
}


function Get-ElementText {
    # run18：单条 .Current.Name 也包超时（3 秒）——app 卡住时逐条读会把循环拖成几分钟。
    # 循环里每条都 3 秒封顶，最坏也是有界的（不再无限等），正常时每次约几毫秒。
    param($El)
    if ($null -eq $El) { return '' }
    $res = Invoke-UiWithTimeout -Code {
        param($e)
        try { $e.Current.Name } catch { '' }
    } -TimeoutSec 3 -What 'Read-Name' -ArgumentList @($El)
    if ($null -eq $res -or @($res).Count -eq 0) { return '' }
    return [string](@($res)[0])
}


# ---- probe ----
$w = Get-TmwWindow
if ($null -eq $w) { 'PROBE: no main window'; exit 1 }
'PROBE: main window found'
$list = Find-ByAutomationId -Root $w -AutomationId 'ChannelList'
if ($null -eq $list) { 'PROBE: no ChannelList'; exit 1 }
$items = Find-All -Root $list -ControlType ([System.Windows.Automation.ControlType]::ListItem)
'PROBE: Find-All ListItems count=' + $items.Count
$names1 = @($items | ForEach-Object { Get-ElementText $_ })
$nz1 = @($names1 | Where-Object { $_ -ne '' })
'PROBE: one-by-one non-empty names=' + $nz1.Count + ' first5=' + (($nz1 | Select-Object -First 5) -join '|')
$res = Invoke-UiWithTimeout -Code {
    param($els)
    @($els | Where-Object { try { $_.Current.Name -ne '' } catch { $false } })
} -TimeoutSec 10 -What 'Filter-Probe' -ArgumentList @($items)
if ($null -eq $res) { 'PROBE: filter runspace returned NULL (timeout/error)' }
else {
    $kept = @($res)
    'PROBE: filter runspace kept=' + $kept.Count
    $names2 = @($kept | ForEach-Object { Get-ElementText $_ })
    $nz2 = @($names2 | Where-Object { $_ -ne '' })
    'PROBE: filter kept non-empty names=' + $nz2.Count + ' first5=' + (($nz2 | Select-Object -First 5) -join '|')
}
$combo = Find-ByAutomationId -Root $w -AutomationId 'GroupCombo'
if ($null -ne $combo) {
    $r2 = Invoke-UiWithTimeout -Code {
        param($e, $pt)
        try { $pat = $e.GetCurrentPattern($pt); 'GOT-PATTERN' } catch { 'THREW: ' + $_.Exception.Message }
    } -TimeoutSec 10 -What 'Pattern-Type-Probe' -ArgumentList @($combo, [System.Windows.Automation.ExpandCollapsePattern])
    'PROBE: GetCurrentPattern(Type) => ' + @($r2)[0]
    $r3 = Invoke-UiWithTimeout -Code {
        param($e, $pt)
        try { $pat = $e.GetCurrentPattern($pt); 'GOT-PATTERN' } catch { 'THREW: ' + $_.Exception.Message }
    } -TimeoutSec 10 -What 'Pattern-Field-Probe' -ArgumentList @($combo, [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    'PROBE: GetCurrentPattern(::Pattern) => ' + @($r3)[0]
} else { 'PROBE: no GroupCombo' }
'PROBE-DONE'
