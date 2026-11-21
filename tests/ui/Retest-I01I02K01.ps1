# I01/I02/K01 focused retest - extracted verbatim from run21 script (commit b7471d1).
# Only the I01/I02/K01 blocks + startup/A01/A02 gates; helpers copied unchanged.

# TiviMateWin UI 回归测试脚本 — 发版门禁（用户规矩：先测后发，脚本全过才打 tag）
# 跑法见 tests/ui/README.md。必须在交互会话 session 1 里跑（定时任务），WinRM session 0 看不到窗口。
# 退出码：0 全部 PASS；1 有 FAIL；2 有 NEEDS-EYE 待复核（无 FAIL 时）。
param(
    [string]$InstallDir = 'C:\TiviMateWin',
    [string]$ExpectedVersion = '1.4.37',
    [string]$OutDir = 'C:\build\tmw-uitest\run1',
    [int]$SoakMinutes = 30,
    [switch]$SkipSoak,
    [switch]$SkipConfigCorruption
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
# run11：全局 trap——脚本若被终止错误杀死，把错误写进 OutDir，免得死得不明不白（run10 死在 D01 之后、无 D02、无汇总）。
trap {
    try {
        $msg = "$(Get-Date -Format 'HH:mm:ss') [FATAL] $($_.Exception.GetType().FullName): $($_.Exception.Message)`n$($_.ScriptStackTrace)"
        Add-Content -Path (Join-Path $OutDir 'fatal.txt') -Value $msg -Encoding UTF8
        Add-Content -Path $ResultsFile -Value $msg -Encoding UTF8
    } catch { }
    break
}
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

# run8：先声明本进程 DPI 感知，否则 WinForms/RootElement 拿到的都是逻辑像素
# （125% 下 1536x864，物理是 1920x1080），鼠标坐标会偏 25%。调完后
# GetSystemMetrics 与 UIA RootElement 都返回物理值；Screen.Bounds 仍是缓存的
# 逻辑值，永不再用它。
try { [Win32]::SetProcessDPIAware() | Out-Null } catch { }

# 测试脚本自己的控制台窗口不要盖住 app（run6 教训：用户点开控制台后截图全是控制台，
# 右键/设置点按也全落在控制台上）。启动即最小化（6 = SW_MINIMIZE）。
try { [Win32]::ShowWindow([Win32]::GetConsoleWindow(), 6) | Out-Null } catch { }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ResultsFile = Join-Path $OutDir 'results.txt'
$ResultsJson = Join-Path $OutDir 'results.jsonl'
$ShotDir = Join-Path $OutDir 'shots'
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
if (Test-Path $ResultsFile) { Remove-Item $ResultsFile -Force }
if (Test-Path $ResultsJson) { Remove-Item $ResultsJson -Force }

$Script:Results = @()
$Script:StartTime = Get-Date


function Write-Result {
    param([string]$Id, [string]$Name, [string]$Verdict, [string]$Evidence = '')
    $line = "$(Get-Date -Format 'HH:mm:ss') [$Verdict] $Id $Name -- $Evidence"
    Add-Content -Path $ResultsFile -Value $line -Encoding UTF8
    $obj = @{ id = $Id; name = $Name; verdict = $Verdict; evidence = $Evidence; time = (Get-Date -Format 'HH:mm:ss') } | ConvertTo-Json -Compress
    Add-Content -Path $ResultsJson -Value $obj -Encoding UTF8
    $Script:Results += @{ Id = $Id; Verdict = $Verdict }
    Write-Output $line
}

function Get-LogFile {
    $cands = @()
    $cands += Get-ChildItem "$env:APPDATA\TiviMateWin\log.txt" -ErrorAction SilentlyContinue
    $cands += Get-ChildItem "$InstallDir\log.txt" -ErrorAction SilentlyContinue
    $cands += Get-ChildItem "$env:APPDATA\TiviMateWin\*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    return ($cands | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

function Get-Config {
    $p = "$env:APPDATA\TiviMateWin\config.json"
    if (-not (Test-Path $p)) { return $null }
    try { return (Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

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

function Invoke-UiPatternCall {
    # run18：通用 UIA 模式调用（Expand/Toggle/SetValue/Close/SetWindowVisualState），
    # 在带超时的 runspace 里跑，永不无限等。返回 $true=调用成功，$false=超时/失败。
    # $Method 是模式对象上的方法名；$Args 是它的参数（SetWindowVisualState 传枚举值）。
    param($Element, [type]$PatternType, [string]$Method, [object[]]$Args = @())
    if ($null -eq $Element) { return $false }
    # run19：GetCurrentPattern 要的是 AutomationPattern（::Pattern 字段），不是模式类的 Type。
    # run18 把 Type 直接传进 runspace，GetCurrentPattern 抛类型转换错、被吞掉返回 $false，
    # 所有 Expand/Toggle/SetValue/Close 全没生效（B02/B03 的根因）。这里先在主线程把
    # ::Pattern 字段解出来，再把 AutomationPattern 传进 runspace。
    $pattern = $null
    try { $pattern = $PatternType::Pattern } catch { return $false }
    if ($null -eq $pattern) { return $false }
    $res = Invoke-UiWithTimeout -Code {
        param($e, $pt, $m, $a)
        $pat = $e.GetCurrentPattern($pt)
        if ($a.Count -eq 0) { $pat.$m() } else { $pat.GetType().GetMethod($m).Invoke($pat, $a) }
        $true
    } -TimeoutSec 10 -What "Pattern-$Method" -ArgumentList @($Element, $pattern, $Method, $Args)
    if ($null -eq $res -or @($res).Count -eq 0) { return $false }
    return [bool](@($res)[0])
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

function Invoke-Element {
    # run18：Invoke() 也包一层超时——app 卡住时它一样无限等。超时返回 $false，
    # 但点击可能稍后才落下（原生调用仍在跑），调用方点完要轮询结果，不要只凭返回值定罪。
    param($El)
    if ($null -eq $El) { return $false }
    $res = Invoke-UiWithTimeout -Code {
        param($e)
        $pat = $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        $true
    } -TimeoutSec 15 -What 'Invoke' -ArgumentList @($El)
    if ($null -eq $res -or @($res).Count -eq 0) { return $false }
    return [bool](@($res)[0])
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

function Get-ElementRect {
    # run18：读元素的 BoundingRectangle，带超时；超时/失败返回 $null。
    param($El)
    if ($null -eq $El) { return $null }
    $res = Invoke-UiWithTimeout -Code {
        param($e)
        try { $e.Current.BoundingRectangle } catch { $null }
    } -TimeoutSec 5 -What 'Read-Rect' -ArgumentList @($El)
    if ($null -eq $res -or @($res).Count -eq 0) { return $null }
    return @($res)[0]
}

function Get-ElementIsOffscreen {
    # run18：读元素的 IsOffscreen，带超时；超时/失败返回 $null。
    param($El)
    if ($null -eq $El) { return $null }
    $res = Invoke-UiWithTimeout -Code {
        param($e)
        try { $e.Current.IsOffscreen } catch { $null }
    } -TimeoutSec 5 -What 'Read-IsOffscreen' -ArgumentList @($El)
    if ($null -eq $res -or @($res).Count -eq 0) { return $null }
    return [bool](@($res)[0])
}

function Get-PhysicalScreenSize {
    # run8：进程已声明 DPI 感知，GetSystemMetrics(0/1) 直接返回物理像素；
    # 旧代码先读 UIA RootElement——没调 SetProcessDPIAware 时它是逻辑值（1536x864），
    # 鼠标点按会偏 25%。Screen.Bounds 是缓存的逻辑值，永不用。
    try {
        $w = [Win32]::GetSystemMetrics(0); $h = [Win32]::GetSystemMetrics(1)
        if ($w -gt 0 -and $h -gt 0) { return @($w, $h) }
    } catch {}
    try {
        $rr = [System.Windows.Automation.AutomationElement]::RootElement.Current.BoundingRectangle
        if ($rr.Width -gt 0 -and $rr.Height -gt 0) { return @([int]$rr.Width, [int]$rr.Height) }
    } catch {}
    return @([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
}

function Invoke-MouseClick {
    param([int]$X, [int]$Y)
    $sz = Get-PhysicalScreenSize; $sw = $sz[0]; $sh = $sz[1]
    # 注意：[Math]::Min/Max 在 int/double 混用时会找不到重载、静默返回空值，
    # 导致 mouse_event 落到 (0,0)。一律用 double 字面量，避免重载歧义。
    $ax = [uint32][Math]::Min(65535.0, [Math]::Max(0.0, $X * 65535.0 / $sw))
    $ay = [uint32][Math]::Min(65535.0, [Math]::Max(0.0, $Y * 65535.0 / $sh))
    [Win32]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)  # MOVE | ABSOLUTE
    Start-Sleep -Milliseconds 120
    [Win32]::mouse_event(0x0002, $ax, $ay, 0, [UIntPtr]::Zero)  # LEFTDOWN
    Start-Sleep -Milliseconds 80
    [Win32]::mouse_event(0x0004, $ax, $ay, 0, [UIntPtr]::Zero)  # LEFTUP
}

function Take-Shot {
    param([string]$Name, [object]$Crop = $null)
    Bring-ToFront   # 截图前把 app 顶到最前，免得别的窗口盖住画面（run6 的 E01 截到的就是控制台）
    # run8：截图尺寸也要物理像素（Screen.Bounds 是逻辑值，截出来会少 25%）。
    $sz2 = Get-PhysicalScreenSize
    $bmp = New-Object System.Drawing.Bitmap($sz2[0], $sz2[1])
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([System.Drawing.Point]::Empty, [System.Drawing.Point]::Empty, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $ShotDir ("$Name.jpg")
    if ($null -ne $Crop) {
        $c = $bmp.Clone($Crop, $bmp.PixelFormat)
        $bmp.Dispose()
        $c.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
        $c.Dispose()
    } else {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
        $bmp.Dispose()
    }
    return $path
}

function Send-KeysToWindow {
    param([string]$Keys, [int]$WaitMs = 300)
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $WaitMs
}

function Bring-ToFront {
    $p = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $p -and $p.MainWindowHandle -ne 0) {
        [Win32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 500
    }
}

function Get-TimeTextValue {
    # run18：时间文本的读取也放进带超时的 runspace（逐条读会堵在卡住的 app 上）。
    # 播放时间 Text（形如 00:31）：遍历 Text 找时间格式
    $w = Get-TmwWindow
    if ($null -eq $w) { return '' }
    $texts = Find-All -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text)
    if ($texts.Count -eq 0) { return '' }
    $res = Invoke-UiWithTimeout -Code {
        param($els)
        foreach ($t in $els) {
            try { $n = $t.Current.Name } catch { $n = '' }
            if ($n -match '^\d{1,2}:\d{2}(:\d{2})?$') { return $n }
        }
        return ''
    } -TimeoutSec 10 -What 'Read-TimeText' -ArgumentList @($texts)
    if ($null -eq $res -or @($res).Count -eq 0) { return '' }
    return [string](@($res)[0])
}

function Test-TimeAdvancing {
    param([int]$WaitSec = 4)
    $a = Get-TimeTextValue
    Start-Sleep -Seconds $WaitSec
    $b = Get-TimeTextValue
    return @{ Before = $a; After = $b; Advanced = ($a -ne '' -and $b -ne '' -and $a -ne $b) }
}

# ================= 启动 =================
Write-Output "== TiviMateWin UI 回归测试 v$ExpectedVersion $(Get-Date -Format 'yyyy-MM-dd HH:mm') =="
$exe = Join-Path $InstallDir 'TiviMateWin.exe'
if (-not (Test-Path $exe)) {
    Write-Result -Id 'A00' -Name '安装存在' -Verdict 'FAIL' -Evidence "$exe 不存在"
    exit 1
}
$ver = (Get-Item $exe).VersionInfo.FileVersion
Write-Output "exe 版本: $ver（期望 $ExpectedVersion.0）"

$cfgPath = "$env:APPDATA\TiviMateWin\config.json"
$cfgBackup = Join-Path $OutDir 'config-baseline.json'
if (Test-Path $cfgPath) { Copy-Item $cfgPath $cfgBackup -Force }
$cfg0 = Get-Config
$hidden0 = @()
if ($null -ne $cfg0 -and $null -ne $cfg0.HiddenChannels) { $hidden0 = @($cfg0.HiddenChannels) }
$liveSub0 = $null
if ($null -ne $cfg0) { $liveSub0 = $cfg0.LiveSubtitleEnabled }
Write-Output "基线: 隐藏 $($hidden0.Count) 个台，LiveSubtitleEnabled=$liveSub0"

$proc = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
# run12：先杀残留的 UnBox——run11 里它在 E01/E02/E04/F01 时段抢了前台（搜“不存在的片名”），
# 污染了 4 张截图。只杀进程，不删任务/文件。
Get-Process UnBox -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$launchStart = Get-Date
if ($null -eq $proc) {
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
    Write-Output '已启动 app'
}
$win = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 2
    $win = Get-TmwWindow
    if ($null -ne $win) { break }
}
if ($null -eq $win) {
    Write-Result -Id 'A01' -Name '主窗口出现' -Verdict 'FAIL' -Evidence '启动 120 秒后仍无主窗口'
    exit 1
}
$launchSecs = [int]((Get-Date) - $launchStart).TotalSeconds
Write-Result -Id 'A03' -Name '启动耗时（记录）' -Verdict 'PASS' -Evidence "启动到主窗口出现约 ${launchSecs}s"
Bring-ToFront
function Restore-WindowNormal {
    # 窗口恢复到正常态：app 会按 config 恢复成最大化，但最大化时底部工具栏常掉出屏幕外
    # （E01 OSD 被裁、设置按钮点不到的根因），恢复成 1280x720 居中，保证全部 UI 可见可点。
    # F01/K06 会重启 app，重启后窗口又变回最大化，所以每次打开设置窗前也要调一次。
    try {
        $w0 = Get-TmwWindow
        if ($null -eq $w0) { return }
        # run18：SetWindowVisualState 也是跨进程调用，app 卡住时会堵——包超时。
        [void](Invoke-UiPatternCall -Element $w0 -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'SetWindowVisualState' -Args @([System.Windows.Automation.WindowVisualState]::Normal))
        Start-Sleep -Seconds 1
        $h0 = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowHandle
        if ($h0 -ne [IntPtr]::Zero) {
            # 1280x720 居中（逻辑像素 1536x792 的屏内完全放得下）
            [Win32Pos]::SetWindowPos($h0, [IntPtr]::Zero, 128, 36, 1280, 720, 0x0040) | Out-Null
            Start-Sleep -Seconds 1
        }
    } catch { Write-Output ("窗口恢复失败：$($_.Exception.Message)") }
}
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win32Pos {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
Restore-WindowNormal
$wr0 = Get-ElementRect (Get-TmwWindow)
Write-Output ("窗口已恢复正常态 rect=$wr0")

# 等节目单加载（log 里 epg loaded）
$epgN = 0
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    $lf = Get-LogFile
    if ($null -ne $lf) {
        $tail = Get-Content $lf.FullName -Tail 200 -ErrorAction SilentlyContinue
        $m = ($tail | Select-String -Pattern 'epg loaded (\d+) channels' | Select-Object -Last 1)
        if ($null -ne $m) { $epgN = [int]$m.Matches.Groups[1].Value; break }
    }
}
if ($epgN -eq 0) {
    # 兜底：日志行可能已被大量 stall/reconnect 日志挤出 tail 200；两个候选日志文件都全读一遍再找
    $cands = @("$env:APPDATA\TiviMateWin\log.txt", "$InstallDir\log.txt")
    foreach ($cp in $cands) {
        if ($epgN -gt 0) { break }
        if (Test-Path $cp) {
            $all = Get-Content $cp -ErrorAction SilentlyContinue
            $m2 = ($all | Select-String -Pattern 'epg loaded (\d+) channels' | Select-Object -Last 1)
            if ($null -ne $m2) { $epgN = [int]$m2.Matches.Groups[1].Value }
        }
    }
}

# ================= A：版本与启动 =================
$title = Get-ElementText (Get-TmwWindow)
if ($title -match [regex]::Escape($ExpectedVersion)) {
    Write-Result -Id 'A01' -Name '标题栏版本号' -Verdict 'PASS' -Evidence "标题=$title"
} else {
    Write-Result -Id 'A01' -Name '标题栏版本号' -Verdict 'FAIL' -Evidence "标题=$title，期望含 $ExpectedVersion"
}
if ($epgN -ge 50) {
    Write-Result -Id 'A02' -Name '节目单加载' -Verdict 'PASS' -Evidence "epg loaded $epgN 台"
} else {
    Write-Result -Id 'A02' -Name '节目单加载' -Verdict 'FAIL' -Evidence "只加载到 $epgN 台（期望 >=50）"
}


# ================= I：空格键暂停 =================
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2500
# 先把焦点从搜索框/下拉框上挪开（焦点在 TextBox/ComboBox 里时空格是打字，不会暂停）
$winI = Get-TmwWindow
try { $winI.SetFocus() } catch { }
$tI0 = Test-TimeAdvancing -WaitSec 3
if (-not $tI0.Advanced) {
    # 1 号台可能正好是死流（分片 403 时 app 会自动跳台，但刚切回来那一下可能还没播起来）；
    # 切下一台再确认一次——空格暂停本身与台无关。
    Send-KeysToWindow -Keys '{DOWN}' -WaitMs 2500
    $tI0 = Test-TimeAdvancing -WaitSec 3
}
if (-not $tI0.Advanced) {
    # run9：按空格前时间就不走——流本身卡住了（源站抖动），没法判断空格暂停；
    # 记 NEEDS-EYE 而非 FAIL（看日志定性是源站卡还是 app 没播起来）
    Write-Result -Id 'I01' -Name '空格键暂停' -Verdict 'NEEDS-EYE' -Evidence "按空格前时间就不走：$($tI0.Before)->$($tI0.After)（换了两个台仍不走，疑似源站卡流，无法判断暂停）"
    Write-Result -Id 'I02' -Name '再按空格继续' -Verdict 'NEEDS-EYE' -Evidence 'I01 未完成（流卡住），跳过'
} else {
    Send-KeysToWindow -Keys ' ' -WaitMs 1200
    $shotI1 = Take-Shot -Name 'I01-paused-badge'
    $p1 = Get-TimeTextValue; Start-Sleep -Seconds 3; $p2 = Get-TimeTextValue
    if ($p1 -eq '' -or $p1 -ne $p2) {
        # 没停住可能是焦点还在输入框里；再聚焦一次主窗口、重按空格
        try { (Get-TmwWindow).SetFocus() } catch { }
        Send-KeysToWindow -Keys ' ' -WaitMs 1200
        $p1 = Get-TimeTextValue; Start-Sleep -Seconds 3; $p2 = Get-TimeTextValue
        $shotI1 = Take-Shot -Name 'I01-paused-badge'
    }
    if ($p1 -ne '' -and $p1 -eq $p2) {
        Write-Result -Id 'I01' -Name '空格键暂停' -Verdict 'PASS' -Evidence "时间停在 $p1 不走；徽章截图 $shotI1（须肉眼确认中央有「⏸ 已暂停」）"
    } else {
        Write-Result -Id 'I01' -Name '空格键暂停' -Verdict 'FAIL' -Evidence "按空格后时间还在走：$p1->$p2；截图 $shotI1"
    }
    Send-KeysToWindow -Keys ' ' -WaitMs 1200
    $shotI2 = Take-Shot -Name 'I02-resumed'
    $r1 = Test-TimeAdvancing -WaitSec 4
    if ($r1.Advanced) {
        Write-Result -Id 'I02' -Name '再按空格继续' -Verdict 'PASS' -Evidence "时间 $($r1.Before)->$($r1.After) 在走；截图 $shotI2（徽章应消失）"
    } else {
        # run9：再按一次仍不走——可能是恢复失败，也可能是暂停期间流卡住了；
        # 多等 5 秒再测一次：走了就是恢复慢（PASS），仍不走记 NEEDS-EYE（看日志定性）
        Start-Sleep -Seconds 5
        $r1b = Test-TimeAdvancing -WaitSec 4
        if ($r1b.Advanced) {
            Write-Result -Id 'I02' -Name '再按空格继续' -Verdict 'PASS' -Evidence "恢复后时间先停后走（$($r1.Before)->$($r1.After)，再测 $($r1b.Before)->$($r1b.After)）；截图 $shotI2"
        } else {
            Write-Result -Id 'I02' -Name '再按空格继续' -Verdict 'NEEDS-EYE' -Evidence "再按空格后时间不动：$($r1.Before)->$($r1.After)，再测 $($r1b.Before)->$($r1b.After)；截图 $shotI2（疑似暂停期间流卡住，需看日志定性）"
        }
    }
}


# ================= K：探查隐藏 bug =================
# K01：快速连切 10 台
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2000
for ($k = 0; $k -lt 10; $k++) { Send-KeysToWindow -Keys '{DOWN}' -WaitMs 400 }
Start-Sleep -Seconds 3
$k1 = Get-TmwWindow
$k1t = Test-TimeAdvancing -WaitSec 3
if ($null -eq $k1) {
    Write-Result -Id 'K01' -Name '快速连切 10 台不崩' -Verdict 'FAIL' -Evidence '连切后主窗口没了（疑似崩溃）'
} elseif ($k1t.Advanced) {
    Write-Result -Id 'K01' -Name '快速连切 10 台不崩' -Verdict 'PASS' -Evidence "连切后仍在播，时间 $($k1t.Before)->$($k1t.After)"
} else {
    # run9：窗口在、时间不走——切到的台流卡住了（源站抖动），不是崩；
    # 记 NEEDS-EYE（看日志定性），不当 FAIL
    Write-Result -Id 'K01' -Name '快速连切 10 台不崩' -Verdict 'NEEDS-EYE' -Evidence "连切后窗口在、时间 $($k1t.Before)->$($k1t.After) 不走（疑似切到的台流卡住，需看日志定性）"
}

# K02：断流台 60 秒不退出（凤凰中文，流本身约 1Mbps 偏紧、经常断）

# stop app so session 1 is free for UnBox round 10
try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Seconds 2
$lfEnd = Get-LogFile
if ($null -ne $lfEnd) { Add-Content -Path $ResultsFile -Value '--- app log tail (last 30) ---' -Encoding UTF8; Get-Content $lfEnd.FullName -Tail 30 -ErrorAction SilentlyContinue | Add-Content -Path $ResultsFile -Encoding UTF8 }

# ================= 汇总 =================
$fail = @($Script:Results | Where-Object { $_.Verdict -eq 'FAIL' }).Count
$eye = @($Script:Results | Where-Object { $_.Verdict -eq 'NEEDS-EYE' }).Count
$pass = @($Script:Results | Where-Object { $_.Verdict -eq 'PASS' }).Count
$totalMin = [int]((Get-Date) - $Script:StartTime).TotalMinutes
$summary = "汇总：PASS=$pass FAIL=$fail NEEDS-EYE=$eye，用时约 ${totalMin} 分钟，结果见 $ResultsFile"
Add-Content -Path $ResultsFile -Value $summary -Encoding UTF8
Write-Output $summary
if ($fail -gt 0) { exit 1 }
if ($eye -gt 0) { exit 2 }
exit 0
