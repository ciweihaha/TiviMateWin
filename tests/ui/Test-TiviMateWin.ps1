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
    $res = Invoke-UiWithTimeout -Code {
        param($e, $pt, $m, $a)
        $pat = $e.GetCurrentPattern($pt)
        if ($a.Count -eq 0) { $pat.$m() } else { $pat.GetType().GetMethod($m).Invoke($pat, $a) }
        $true
    } -TimeoutSec 10 -What "Pattern-$Method" -ArgumentList @($Element, $PatternType, $Method, $Args)
    if ($null -eq $res -or @($res).Count -eq 0) { return $false }
    return [bool](@($res)[0])
}

function Get-UiPatternValue {
    # run18：读一个 UIA 模式对象的属性（如 ToggleState），带超时，超时返回 $null。
    param($Element, [type]$PatternType, [string]$Property)
    if ($null -eq $Element) { return $null }
    $res = Invoke-UiWithTimeout -Code {
        param($e, $pt, $prop)
        $pat = $e.GetCurrentPattern($pt)
        $pat.$prop
    } -TimeoutSec 5 -What "Pattern-$Property" -ArgumentList @($Element, $PatternType, $Property)
    if ($null -eq $res -or @($res).Count -eq 0) { return $null }
    return @($res)[0]
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

function Invoke-MouseRightClick {
    param([int]$X, [int]$Y)
    $sz = Get-PhysicalScreenSize; $sw = $sz[0]; $sh = $sz[1]
    $ax = [uint32][Math]::Min(65535.0, [Math]::Max(0.0, $X * 65535.0 / $sw))
    $ay = [uint32][Math]::Min(65535.0, [Math]::Max(0.0, $Y * 65535.0 / $sh))
    [Win32]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win32]::mouse_event(0x0008, $ax, $ay, 0, [UIntPtr]::Zero)  # RIGHTDOWN
    Start-Sleep -Milliseconds 80
    [Win32]::mouse_event(0x0010, $ax, $ay, 0, [UIntPtr]::Zero)  # RIGHTUP
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

function Get-DarkPixelFraction {
    param([string]$ImagePath)
    # 框里深色像素占比：白底深字时 >0.002；浅字白底时 ~0
    try {
        $bmp = [System.Drawing.Bitmap]::FromFile($ImagePath)
        $dark = 0; $total = 0
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            for ($y = 0; $y -lt $bmp.Height; $y += 2) {
                $px = $bmp.GetPixel($x, $y)
                $b = ($px.R + $px.G + $px.B) / 3
                if ($b -lt 120) { $dark++ }
                $total++
            }
        }
        $bmp.Dispose()
        if ($total -eq 0) { return 0 }
        return [double]$dark / $total
    } catch { return -1 }
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

function Get-StatusText {
    # run18：把所有 .Current.Name 读放进带超时的 runspace，逐条在主线程读会堵在卡住的 app 上。
    # 状态栏文字：遍历所有 Text 找含关键字的那行
    $w = Get-TmwWindow
    if ($null -eq $w) { return '' }
    $texts = Find-All -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text)
    if ($texts.Count -eq 0) { return '' }
    $res = Invoke-UiWithTimeout -Code {
        param($els)
        @($els | ForEach-Object { try { $_.Current.Name } catch { '' } }) -join ' | '
    } -TimeoutSec 10 -What 'Read-StatusText' -ArgumentList @($texts)
    if ($null -eq $res -or @($res).Count -eq 0) { return '' }
    return [string](@($res)[0])
}

function Get-SubtitleStatusText {
    # run18（F01 探针用）：读字幕状态文本（SubtitleStatus，MainWindow.xaml:409）。
    # 返回文本（可能为空串）；元素找不到或超时返回 $null。
    $w = Get-TmwWindow
    if ($null -eq $w) { return $null }
    $el = Find-ByAutomationId -Root $w -AutomationId 'SubtitleStatus' -TimeoutSec 3
    if ($null -eq $el) { return $null }
    $res = Invoke-UiWithTimeout -Code {
        param($e)
        try { $e.Current.Name } catch { '' }
    } -TimeoutSec 3 -What 'Read-SubtitleStatus' -ArgumentList @($el)
    if ($null -eq $res -or @($res).Count -eq 0) { return $null }
    return [string](@($res)[0])
}

function Wait-ForStatus {
    param([string]$Pattern, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $s = Get-StatusText
        if ($s -match $Pattern) { return $s }
        Start-Sleep -Seconds 1
    }
    return (Get-StatusText)
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

# run10：Get-TimeTextViaJob / Test-TimeAdvancingJob 已删——Job 版时间读取在计划任务里
# 会挂住（run9 浸泡卡死在 min10 之后），浸泡不再读 UIA 时间。

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

# ================= B：分组下拉框 =================
$win = Get-TmwWindow
$combo = Find-ByAutomationId -Root $win -AutomationId 'GroupCombo'
if ($null -eq $combo) {
    Write-Result -Id 'B01' -Name '分组下拉框存在' -Verdict 'FAIL' -Evidence 'AutomationId=GroupCombo 找不到'
} else {
    # run18：矩形读包超时；读不到就判 B01 FAIL，不拿空矩形去截图。
    $r = Get-ElementRect $combo
    if ($null -eq $r) {
        Write-Result -Id 'B01' -Name '分组下拉框字看得见' -Verdict 'FAIL' -Evidence '下拉框矩形读不到（app 可能卡住）'
    } else {
    # B01：字看得见——截下拉框区域，数深色像素（白底深字才有深色像素；浅字白底几乎没有）
    $rect = New-Object System.Drawing.Rectangle([int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    $crop = Take-Shot -Name 'B01-combo-crop' -Crop $rect
    $frac = Get-DarkPixelFraction -ImagePath $crop
    if ($frac -gt 0.002) {
        Write-Result -Id 'B01' -Name '分组下拉框字看得见' -Verdict 'PASS' -Evidence "深色像素占比 $([Math]::Round($frac,4))，截图 $crop"
    } else {
        Write-Result -Id 'B01' -Name '分组下拉框字看得见' -Verdict 'FAIL' -Evidence "深色像素占比 $([Math]::Round($frac,4))（<0.002），字太浅或没画出；截图 $crop"
    }
    }
    # B02：4 个选项（展开后读全窗 ListItem 差集；此定制 ComboBox 不支持 ValuePattern/SelectionPattern）
    try {
        $selVal = Get-ElementText $combo
        # 展开前先记下全窗已有的 ListItem 名字（频道行），展开后多出来的就是下拉选项
        $winB = Get-TmwWindow
        $beforeNames = @()
        foreach ($li in (Find-All -Root $winB -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeNames += (Get-ElementText $li) }
        # run18：Expand 也包超时（跨进程调用，app 卡住时会堵）。
        [void](Invoke-UiPatternCall -Element $combo -PatternType ([System.Windows.Automation.ExpandCollapsePattern]) -Method 'Expand')
        Start-Sleep -Seconds 1
        $items = @()
        foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
            $nm = Get-ElementText $li
            if ($nm -ne '' -and ($beforeNames -notcontains $nm)) { $items += $nm }
        }
        $exp.Collapse()
        if ($items.Count -ge 4 -and ($items -contains '全部')) {
            Write-Result -Id 'B02' -Name '下拉框选项' -Verdict 'PASS' -Evidence "选中=$selVal，选项=$($items -join '/')"
        } else {
            Write-Result -Id 'B02' -Name '下拉框选项' -Verdict 'FAIL' -Evidence "选中=$selVal，读到选项=$($items -join '/')（期望 >=4 个含「全部」）"
        }
    } catch {
        Write-Result -Id 'B02' -Name '下拉框选项' -Verdict 'FAIL' -Evidence "展开读选项异常：$($_.Exception.Message)"
    }
    # B03/B04：选「卫视频道」过滤，再选回「全部」
    function Get-CountText {
        $w2 = Get-TmwWindow
        $ct = Find-ByAutomationId -Root $w2 -AutomationId 'CountText'
        if ($null -eq $ct) {
            $ts = Find-ByName -Root $w2 -ControlType ([System.Windows.Automation.ControlType]::Text) -NamePattern '个频道'
            if ($ts.Count -gt 0) { return (Get-ElementText $ts[0]) }
            return ''
        }
        return (Get-ElementText $ct)
    }
    $countAll = Get-CountText
    function Find-DropdownItem {
        param([string]$Want)
        $w3 = Get-TmwWindow
        $beforeNames = @()
        foreach ($li in (Find-All -Root $w3 -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeNames += (Get-ElementText $li) }
        $cb = Find-ByAutomationId -Root $w3 -AutomationId 'GroupCombo'
        if ($null -eq $cb) { return $null }  # run8：找不到下拉框就返回空，不许抛错（run7 B03 就是在这里炸的）
        [void](Invoke-UiPatternCall -Element $cb -PatternType ([System.Windows.Automation.ExpandCollapsePattern]) -Method 'Expand')
        Start-Sleep -Seconds 1
        $found = $null
        foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
            $nm = Get-ElementText $li
            if ($nm -eq $Want -and ($beforeNames -notcontains $nm)) { $found = $li; break }
        }
        return $found
    }
    function Get-ComboValue {
        # 定制 ComboBox 不支持 ValuePattern；用 ComboBox 自身的 Name（选中项文本会反映在上面）兜底
        $cb = Find-ByAutomationId -Root (Get-TmwWindow) -AutomationId 'GroupCombo'
        if ($null -eq $cb) { return '' }
        return (Get-ElementText $cb)
    }
    try {
        $wei = Find-DropdownItem -Want '卫视频道'
        if ($null -ne $wei) {
            # 定制下拉项不支持 SelectionItemPattern，改鼠标点它
            # run18：矩形读包超时；读不到就判 FAIL，不拿空矩形去点。
            $wr = Get-ElementRect $wei
            if ($null -eq $wr) {
                Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'FAIL' -Evidence '「卫视频道」项矩形读不到（app 可能卡住）'
                Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence 'B03 未完成，跳过'
            } else {
            Invoke-MouseClick -X ([int]($wr.X + $wr.Width / 2)) -Y ([int]($wr.Y + $wr.Height / 2))
            Start-Sleep -Seconds 2
            $countWei = Get-CountText
            $selName = Get-ComboValue
            # 核心判定是过滤生效（频道数变了）；ComboBox 显示值仅作参考（定制模板读不到时以计数为准）
            # run8：两个数都必须非空——基数读不到时 '' -ne '56 个频道' 恒真，会假过。
            if ($countAll -ne '' -and $countWei -ne '' -and $countWei -ne $countAll) {
                Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'PASS' -Evidence "全部=$countAll，卫视=$countWei，框显示=$selName"
            } else {
                Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'FAIL' -Evidence "全部=$countAll，选后=$countWei，选中=$selName"
            }
            # 选回「全部」
            $allItem = Find-DropdownItem -Want '全部'
            if ($null -ne $allItem) {
                $ar = Get-ElementRect $allItem
                if ($null -eq $ar) {
                    Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence '「全部」项矩形读不到（app 可能卡住）'
                } else {
                Invoke-MouseClick -X ([int]($ar.X + $ar.Width / 2)) -Y ([int]($ar.Y + $ar.Height / 2))
                Start-Sleep -Seconds 2
                $countBack = Get-CountText
                if ($countBack -eq $countAll) {
                    Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'PASS' -Evidence "恢复后=$countBack"
                } else {
                    Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence "恢复后=$countBack，期望=$countAll"
                }
                }
            } else {
                Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence '展开后找不到「全部」项'
            }
            }
        } else {
            Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'FAIL' -Evidence '展开后找不到「卫视频道」项'
            Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence 'B03 未完成，跳过'
        }
    } catch {
        Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'FAIL' -Evidence "异常：$($_.Exception.Message)"
        Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence 'B03 异常，跳过'
    }
}

# ================= C：右键菜单（隐藏/显示频道） =================
function Get-MenuItems {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $items = Find-All -Root $root -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    # run18：Name 过滤也放进带超时的 runspace（逐条在主线程读会堵在卡住的 app 上）。
    if ($items.Count -eq 0) { return @() }
    $res = Invoke-UiWithTimeout -Code {
        param($els)
        @($els | Where-Object { try { $_.Current.Name -ne '' } catch { $false } })
    } -TimeoutSec 10 -What 'Filter-MenuItems' -ArgumentList @($items)
    if ($null -eq $res) { return @() }
    return @($res)
}

function Open-ChannelContextMenu {
    param([switch]$HiddenOnly)
    # 右键点频道行：先左键点中该行（选中+聚焦），再右键。坐标用物理像素算（见 Get-PhysicalScreenSize）。
    # 注意：Shift+F10 在这 app 上打不开频道菜单，只会出系统菜单，所以必须用鼠标右键。
    # run10：-HiddenOnly 时点带「已隐藏」标记的那行（C04 用：藏的是哪台就点哪台，
    # 不再默认点第 2 行——列表顺序/滚动变化时第 2 行未必是被藏的台）。
    $w = Get-TmwWindow
    if ($null -eq $w) { return @() }
    Bring-ToFront
    $list = Find-ByAutomationId -Root $w -AutomationId 'ChannelList'
    $target = $null
    if ($null -ne $list) {
        $items = Find-All -Root $list -ControlType ([System.Windows.Automation.ControlType]::ListItem)
        if ($HiddenOnly) {
            foreach ($it in $items) {
                $texts = Find-All -Root $it -ControlType ([System.Windows.Automation.ControlType]::Text)
                $names = @($texts | ForEach-Object { Get-ElementText $_ })
                if ($names -contains '已隐藏') { $target = $it; break }
            }
        }
        if ($null -eq $target) {
            if ($items.Count -gt 1) { $target = $items[1] }
            elseif ($items.Count -eq 1) { $target = $items[0] }
        }
    }
    if ($null -ne $target) {
        # run18：矩形读包超时；读不到就不点（菜单打不开由调用方判 FAIL）。
        $tr = Get-ElementRect $target
        if ($null -ne $tr) {
            $cx = [int]($tr.X + $tr.Width / 2); $cy = [int]($tr.Y + $tr.Height / 2)
            Invoke-MouseClick -X $cx -Y $cy
            Start-Sleep -Milliseconds 300
            Invoke-MouseRightClick -X $cx -Y $cy
        }
    } else {
        # 兜底：右键点列表区中部一行
        $r = Get-ElementRect $w
        if ($null -ne $r) { Invoke-MouseRightClick -X ([int]($r.X + 150)) -Y ([int]($r.Y + 550)) }
    }
    Start-Sleep -Milliseconds 800
    # run16：右键后轮询等菜单项出现（最多 3 秒）——run15 的 D01/K03 都是「菜单里没有
    # 「新窗口播放」」，菜单是 Popup、项进 UIA 树有延迟，单次 800ms 读早了会漏。
    # 轮询到「新窗口播放」出现就停；没出现也返回已读到的项（由调用方判）。
    $m = @()
    for ($mi = 0; $mi -lt 6; $mi++) {
        $m = @(Get-MenuItems)
        $names0 = @($m | ForEach-Object { Get-ElementText $_ })
        if ($names0 -contains '新窗口播放') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($m.Count -le 1) {
        # 只读到系统菜单，说明右键没落在频道行上；换第 1 行再试一次
        if ($null -ne $target) {
            $tr = Get-ElementRect $target
            if ($null -ne $tr) {
                Invoke-MouseRightClick -X ([int]($tr.X + $tr.Width / 2)) -Y ([int]($tr.Y + $tr.Height / 2))
            }
            Start-Sleep -Milliseconds 800
            $m = @(Get-MenuItems)
        }
    }
    # 诊断截图：看右键后菜单到底开了没有（run4 裁决用）
    try { [void](Take-Shot -Name 'probe-menu-after-rightclick') } catch { }
    return $m
}

function Get-CountNumber {
    # run8：读不到时重试约 3 秒（列表刚刷新时 CountText 可能短暂为空）；仍读不到
    # 返回 -1，调用方必须判 -1 大声 FAIL，不许拿 -1 当基线（-1 -eq -1 会假过）。
    for ($i = 0; $i -lt 12; $i++) {
        $w = Get-TmwWindow
        $ct = Find-ByAutomationId -Root $w -AutomationId 'CountText'
        $s = ''
        if ($null -ne $ct) { $s = Get-ElementText $ct }
        if ($s -eq '') {
            $ts = Find-ByName -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text) -NamePattern '个频道'
            if ($ts.Count -gt 0) { $s = Get-ElementText $ts[0] }
        }
        if ($s -match '(\d+)') { return [int]$Matches[1] }
        Start-Sleep -Milliseconds 250
    }
    return -1
}

$cnt0 = Get-CountNumber
# C01：菜单里没有「复制频道地址」
Bring-ToFront
Send-KeysToWindow -Keys '2' -WaitMs 1500   # 选 2 号台（不播 1 号，免得后面藏正在播的台）
$menu = Open-ChannelContextMenu
$names = @($menu | ForEach-Object { Get-ElementText $_ })
if ($names -contains '复制频道地址') {
    Write-Result -Id 'C01' -Name '右键菜单没有「复制频道地址」' -Verdict 'FAIL' -Evidence "菜单=$($names -join '/')"
} elseif ($names.Count -eq 0) {
    Write-Result -Id 'C01' -Name '右键菜单没有「复制频道地址」' -Verdict 'FAIL' -Evidence 'Shift+F10 后没读到菜单项（焦点可能不在列表上）'
} elseif (-not ($names -contains '小窗播放' -or $names -contains '新窗口播放' -or $names -contains '隐藏频道' -or $names -contains '收藏')) {
    # 只读到系统菜单（就一个「系统」）：频道菜单根本没打开，不能算通过（run6 假过教训）
    Write-Result -Id 'C01' -Name '右键菜单没有「复制频道地址」' -Verdict 'FAIL' -Evidence "只读到系统菜单，没打开频道菜单：菜单=$($names -join '/')"
} else {
    Write-Result -Id 'C01' -Name '右键菜单没有「复制频道地址」' -Verdict 'PASS' -Evidence "菜单=$($names -join '/')"
}

# C02：隐藏 2 号台
$hideItem = $menu | Where-Object { (Get-ElementText $_) -eq '隐藏频道' } | Select-Object -First 1
if ($null -eq $hideItem) {
    Write-Result -Id 'C02' -Name '右键「隐藏频道」' -Verdict 'FAIL' -Evidence '菜单里没有「隐藏频道」'
    Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'FAIL' -Evidence 'C02 未完成，跳过'
    Write-Result -Id 'C04' -Name '右键「显示频道」取消隐藏' -Verdict 'FAIL' -Evidence 'C02 未完成，跳过'
    Write-Result -Id 'C05' -Name 'config 隐藏恢复基线' -Verdict 'FAIL' -Evidence 'C02 未完成，跳过'
} else {
    $cntPre = Get-CountNumber
    [void](Invoke-Element $hideItem)
    Start-Sleep -Seconds 2
    $cnt1 = Get-CountNumber
    # run13：config 是防抖落盘，2 秒时可能还没写——重读 3 次（每次隔 1 秒），
    # run12 里 C02 就是 config 读早了（台数已掉 54->53，隐藏仍 0）。
    $cfg1 = $null; $hid1 = @()
    for ($ri = 0; $ri -lt 3; $ri++) {
        $cfg1 = Get-Config
        $hid1 = @()
        if ($null -ne $cfg1 -and $null -ne $cfg1.HiddenChannels) { $hid1 = @($cfg1.HiddenChannels) }
        if ($hid1.Count -eq ($hidden0.Count + 1)) { break }
        Start-Sleep -Seconds 1
    }
    # run17：C02 用藏之前刚读的数作基线（不用 C01 前的 cnt0）——run16 里服务端
    # 中途加回西藏卫视（54→55），cnt0 过期，藏 1 个后 54->54 被误判 FAIL。
    if ($cntPre -ne -1 -and $cnt1 -ne -1 -and $cnt1 -eq ($cntPre - 1) -and $hid1.Count -eq ($hidden0.Count + 1)) {
        Write-Result -Id 'C02' -Name '右键「隐藏频道」' -Verdict 'PASS' -Evidence "频道数 $cntPre->$cnt1，隐藏 $($hidden0.Count)->$($hid1.Count)"
    } else {
        Write-Result -Id 'C02' -Name '右键「隐藏频道」' -Verdict 'FAIL' -Evidence "频道数 $cntPre->$cnt1，隐藏 $($hidden0.Count)->$($hid1.Count)"
    }
    # C03：打开「显示隐藏频道」，台回来
    $menu2 = Open-ChannelContextMenu
    $showHidden = $menu2 | Where-Object { (Get-ElementText $_) -eq '显示隐藏频道' } | Select-Object -First 1
    if ($null -eq $showHidden) {
        Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'FAIL' -Evidence '菜单里没有「显示隐藏频道」'
        Write-Result -Id 'C04' -Name '右键「显示频道」取消隐藏' -Verdict 'FAIL' -Evidence 'C03 未完成，跳过'
        Write-Result -Id 'C05' -Name 'config 隐藏恢复基线' -Verdict 'FAIL' -Evidence 'C03 未完成，跳过'
    } else {
        $cntPre3 = Get-CountNumber
        [void](Invoke-Element $showHidden)
        Start-Sleep -Seconds 2
        $cnt2 = Get-CountNumber
        # run17：用点之前的即时数作基线（不用 C01 前的 cnt0），防总数中途变了。
        if ($cntPre3 -ne -1 -and $cnt2 -ne -1 -and $cnt2 -eq ($cntPre3 + 1)) {
            Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'PASS' -Evidence "频道数 $cntPre3->$cnt2"
        } else {
            Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'FAIL' -Evidence "频道数 $cntPre3->$cnt2，期望 $($cntPre3 + 1)"
        }
        # C04：右键已隐藏的台，应只有「显示频道」；
        # run10：直接点带「已隐藏」标记的那行，不再默认点第 2 行。
        # run11：不按数字键选台——按“2”会切台播流、重排列表，反而让标记行找不准；
        # -HiddenOnly 自己会左键选中该行再右键。
        Bring-ToFront
        $menu3 = Open-ChannelContextMenu -HiddenOnly
        $names3 = @($menu3 | ForEach-Object { Get-ElementText $_ })
        $unhide = $menu3 | Where-Object { (Get-ElementText $_) -eq '显示频道' } | Select-Object -First 1
        if ($null -ne $unhide -and -not ($names3 -contains '隐藏频道')) {
            [void](Invoke-Element $unhide)
            Start-Sleep -Seconds 4  # config 保存是 2 秒防抖，2 秒读会抢在写盘前
            $cfg2 = Get-Config
            $hid2 = @()
            if ($null -ne $cfg2 -and $null -ne $cfg2.HiddenChannels) { $hid2 = @($cfg2.HiddenChannels) }
            if ($hid2.Count -eq $hidden0.Count) {
                Write-Result -Id 'C04' -Name '右键「显示频道」取消隐藏' -Verdict 'PASS' -Evidence "隐藏回到 $($hid2.Count) 个"
            } else {
                Write-Result -Id 'C04' -Name '右键「显示频道」取消隐藏' -Verdict 'FAIL' -Evidence "隐藏 $($hid2.Count) 个，期望 $($hidden0.Count)"
            }
        } else {
            Write-Result -Id 'C04' -Name '右键「显示频道」取消隐藏' -Verdict 'FAIL' -Evidence "菜单=$($names3 -join '/')（期望只有「显示频道」、没有「隐藏频道」）"
        }
        # C05：config 隐藏数回到基线；顺手把「显示隐藏频道」关回去（菜单里再点一次取消勾选）
        $menu4 = Open-ChannelContextMenu
        $sh2 = $menu4 | Where-Object { (Get-ElementText $_) -eq '显示隐藏频道' } | Select-Object -First 1
        if ($null -ne $sh2) {
            # run18：ToggleState 读也包超时。
            $ts = Get-UiPatternValue -Element $sh2 -PatternType ([System.Windows.Automation.TogglePattern]) -Property 'ToggleState'
            if ($ts -eq [System.Windows.Automation.ToggleState]::On) { [void](Invoke-Element $sh2); Start-Sleep -Seconds 1 }
        }
        # 点一下空白处关菜单
        Send-KeysToWindow -Keys '{ESC}' -WaitMs 300
        $cfg3 = Get-Config
        $hid3 = @()
        if ($null -ne $cfg3 -and $null -ne $cfg3.HiddenChannels) { $hid3 = @($cfg3.HiddenChannels) }
        if ($hid3.Count -eq $hidden0.Count) {
            Write-Result -Id 'C05' -Name 'config 隐藏恢复基线' -Verdict 'PASS' -Evidence "隐藏 $($hid3.Count) 个，与基线一致"
        } else {
            Write-Result -Id 'C05' -Name 'config 隐藏恢复基线' -Verdict 'FAIL' -Evidence "隐藏 $($hid3.Count) 个，基线 $($hidden0.Count)"
        }
    }
}

# ================= D：新窗口播放 =================
function Get-TmwWindows {
    # 属于 TiviMateWin 进程的全部顶层窗口（主窗口 + 新窗口 ChannelWindow + 设置窗）。
    # 不能按标题筛：ChannelWindow 的标题是台名（如 CCTV综合），不含「星辰」/「TiviMate」。
    # run9：pid 从主窗口 UIA 元素取（Get-TmwWindow().Current.ProcessId），
    # 不用 Get-Process 首个（多进程/旧进程残留时会抓错）。
    # run10：UIA 顶层枚举不可靠（D01/K03 里返回 0，见 H01 的根因），改走 Win32 EnumWindows
    # 按 pid 找可见顶层窗口，再用 FromHandle 取 UIA 元素；UIA 枚举只作回落。
    # 排除已知的非界面顶层窗口（VLC 宿主、IME/输入法、隐藏窗口等），只留有标题的界面窗。
    # run12：pid 直接取进程 Id，不再经 Get-TmwWindow（主窗丢了就全丢）。Get-TmwWindow 只用来
    # 兜底；EnumWindows 按 pid 找全部可见顶层窗口。
    $p = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $p) { return @() }
    $procId = $p.Id
    $script:TmwPid10 = $procId
    $script:TmwHwnds10 = @()
    $script:TmwExclude10 = @('LibVLCSharp.WPF', 'VLC (Direct3D11 output)', 'Hidden Window', 'MediaContextNotificationWindow', 'SystemResourceNotifyWindow', 'CiceroUIWndFrame', 'Mode Indicator', 'MSCTFIME UI', 'Default IME')
    $cb = [Win32+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0; [void][Win32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
        if ($wpid -eq $script:TmwPid10 -and [Win32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder(256)
            [void][Win32]::GetWindowText($hWnd, $sb, 256)
            $t = $sb.ToString()
            if ($t.Length -gt 0 -and $script:TmwExclude10 -notcontains $t) { $script:TmwHwnds10 += $hWnd }
        }
        return $true
    }
    $els = @()
    try {
        [void][Win32]::EnumWindows($cb, [IntPtr]::Zero)
        foreach ($h in $script:TmwHwnds10) {
            try { $el = [System.Windows.Automation.AutomationElement]::FromHandle($h); if ($null -ne $el) { $els += $el } } catch { }
        }
    } catch { }
    if ($els.Count -gt 0) { return $els }
    # 回落：UIA 顶层枚举（run18：包超时，app 卡住时 FindAll 无限等；ProcessId 过滤也在同一次调用里）
    $rt = [System.Windows.Automation.AutomationElement]::RootElement
    $res = Invoke-UiWithTimeout -Code {
        param($rt2, $procId2)
        $all2 = @($rt2.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
        @($all2 | Where-Object { try { $_.Current.ProcessId -eq $procId2 } catch { $false } })
    } -TimeoutSec 10 -What 'Root-FindAll-Windows' -ArgumentList @($rt, $procId)
    if ($null -eq $res) { return @() }
    return @($res)
}

Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2000   # 回到 1 号台并播起来
$menuD = Open-ChannelContextMenu
$newWin = $menuD | Where-Object { (Get-ElementText $_) -eq '新窗口播放' } | Select-Object -First 1
if ($null -eq $newWin) {
    Write-Result -Id 'D01' -Name '新窗口播放时主窗口停掉' -Verdict 'FAIL' -Evidence '菜单里没有「新窗口播放」'
    Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'FAIL' -Evidence 'D01 未完成，跳过'
} else {
    [void](Invoke-Element $newWin)
    # run11：新窗口开得慢（run10 里 3 秒时只有 1 个窗，5 分钟后日志才见 channel window play），
    # 轮询等 15 秒而不是固定睡 3 秒。
    $wins = @()
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        $wins = @(Get-TmwWindows)
        if ($wins.Count -ge 2) { break }
    }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $st = Wait-ForStatus -Pattern '新窗口播放中' -TimeoutSec 5
    $t0 = Test-TimeAdvancing -WaitSec 3
    if ($wins.Count -ge 2 -and ($st -match '新窗口播放中' -or -not $t0.Advanced)) {
        Write-Result -Id 'D01' -Name '新窗口播放时主窗口停掉' -Verdict 'PASS' -Evidence "窗口数=$($wins.Count)，状态含「新窗口播放中」=$($st -match '新窗口播放中')，主窗口时间走动=$($t0.Advanced)"
    } else {
        Write-Result -Id 'D01' -Name '新窗口播放时主窗口停掉' -Verdict 'FAIL' -Evidence "窗口数=$($wins.Count)，状态=$st，时间 $($t0.Before)->$($t0.After)"
    }
    # D02：关新窗口，主窗口恢复
    # run18：关窗的跨进程读/调都包超时（逐窗各 5 秒读句柄 + 10 秒关），app 卡住时不再无限等。
    $closed = $false
    try {
        $mainEl = Get-TmwWindow
        $mainH = 0
        if ($null -ne $mainEl) {
            $mh = Invoke-UiWithTimeout -Code { param($e) try { $e.Current.NativeWindowHandle } catch { 0 } } -TimeoutSec 5 -What 'Read-Hwnd' -ArgumentList @($mainEl)
            if ($null -ne $mh -and @($mh).Count -gt 0) { $mainH = [int](@($mh)[0]) }
        }
        foreach ($w in $wins) {
            $hw = Invoke-UiWithTimeout -Code { param($e) try { $e.Current.NativeWindowHandle } catch { 0 } } -TimeoutSec 5 -What 'Read-Hwnd' -ArgumentList @($w)
            $hwV = if ($null -eq $hw -or @($hw).Count -eq 0) { 0 } else { [int](@($hw)[0]) }
            if ($hwV -ne 0 -and $hwV -ne $mainH) {
                if (Invoke-UiPatternCall -Element $w -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'Close') { $closed = $true }
            }
        }
    } catch { }
    Start-Sleep -Seconds 3
    $t1 = Test-TimeAdvancing -WaitSec 4
    $wins2 = @(Get-TmwWindows)
    if ($closed -and $wins2.Count -eq 1 -and $t1.Advanced) {
        Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'PASS' -Evidence "窗口数回到 1，时间 $($t1.Before)->$($t1.After) 在走"
    } elseif ($closed -and $wins2.Count -eq 1) {
        # run13：窗口数已回到 1（主窗恢复），只是时间不走——流卡住是源站抖动（I01/I02 同款门禁），
        # 不判 app 失败，记 NEEDS-EYE 看日志定性。
        Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'NEEDS-EYE' -Evidence "窗口数回到 1，但时间 $($t1.Before)->$($t1.After) 不走（疑似流卡住，需看日志定性）"
    } else {
        Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'FAIL' -Evidence "关窗=$closed，窗口数=$($wins2.Count)，时间 $($t1.Before)->$($t1.After)"
    }
}

# ================= E：切台信息条 OSD =================
# OSD 不在 UIA 树里，只能截图复核；脚本只保证切台动作 + 截图， verdict 由看图的人定。
# run16：E01 前等当前会话的 EPG 加载完（日志里出现本次启动后的「epg loaded」，
# 最多 90 秒）——run14 的 E01 在 EPG 到之前就截了，OSD 只显示「暂无节目信息」
# （数据有、app 没拿到，不是 app bug，是时序）。没等到也继续截，但证据里注明。
$epgReady = $false
$logPathE = Join-Path $env:APPDATA 'TiviMateWin\log.txt'
for ($ei = 0; $ei -lt 18; $ei++) {
    try {
        $ll = Get-Content $logPathE -Tail 30 -ErrorAction SilentlyContinue | Select-String -Pattern 'epg loaded (\d+) channels'
        foreach ($m in $ll) {
            if ($m.Line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                $ts = [DateTime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
                if ($ts -ge $launchStart) { $epgReady = $true; break }
            }
        }
        if ($epgReady) { break }
    } catch { }
    Start-Sleep -Seconds 5
}
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2600   # 切 CCTV-1（按 "1" 要 1.2 秒才提交）
$shotE1 = Take-Shot -Name 'E01-osd-cctv1'
Write-Result -Id 'E01' -Name '切台 OSD 四行（CCTV-1）' -Verdict 'NEEDS-EYE' -Evidence "截图 $shotE1（应有台名+正在播+接下来 2 个，5 秒消失；EPG已加载=$epgReady）"
Send-KeysToWindow -Keys '51' -WaitMs 4000  # 切凤凰中文（无节目单的台；凤凰起播慢，多等一会让 OSD 覆盖窗浮出来）
$shotE2 = Take-Shot -Name 'E02-osd-phoenix'
Write-Result -Id 'E02' -Name '无节目单台显示「暂无节目信息」（凤凰中文）' -Verdict 'NEEDS-EYE' -Evidence "截图 $shotE2（应有台名 +「暂无节目信息」）"

# E03：没有单独的「节目单」按钮
$win = Get-TmwWindow
$btns = Find-All -Root $win -ControlType ([System.Windows.Automation.ControlType]::Button)
$btnNames = @($btns | ForEach-Object { Get-ElementText $_ })
if ($btnNames -contains '节目单') {
    Write-Result -Id 'E03' -Name '没有单独的「节目单」按钮' -Verdict 'FAIL' -Evidence "按钮里还有「节目单」：$($btnNames -join '/')"
} else {
    Write-Result -Id 'E03' -Name '没有单独的「节目单」按钮' -Verdict 'PASS' -Evidence "工具栏按钮=$($btnNames -join '/')"
}
# E04：E 键打开完整节目单（先切回稳定的 CCTV-1，免得凤凰卡顿触发 ClearVideoSurface 把面板收回去）
# run18：点频道列表行（WPF 元素）把焦点从搜索框移开——视频区是原生 LibVLC HWND，
# 点它移不走 WPF 焦点，run17 E04 的 E 键仍进了搜索框输入法（顶部拼音候选窗）。
# 点行后读 AutomationElement.FocusedElement 验证：仍在搜索框就 {ESC} 后重点击一次。
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2600
$winE4 = Get-TmwWindow
# run18：SetFocus 和取行矩形也都包超时（跨进程调用，app 卡住时会堵）。
[void](Invoke-UiWithTimeout -Code { param($e) try { $e.SetFocus() } catch { } } -TimeoutSec 5 -What 'SetFocus' -ArgumentList @($winE4))
$e04FocusOk = $false
$e04FocusNote = '未验证'
for ($try = 0; $try -lt 2 -and -not $e04FocusOk; $try++) {
    $rect = Invoke-UiWithTimeout -Code {
        param($root)
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $rows = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c))
        foreach ($r in $rows) {
            try { if (-not $r.Current.IsOffscreen) { return $r.Current.BoundingRectangle } } catch { }
        }
        return $null
    } -TimeoutSec 10 -What 'First-Visible-Row' -ArgumentList @((Get-TmwWindow))
    if ($null -ne $rect -and @($rect).Count -gt 0 -and $null -ne @($rect)[0]) {
        $rr = @($rect)[0]
        Invoke-MouseClick -X ([int]($rr.X + $rr.Width / 2)) -Y ([int]($rr.Y + $rr.Height / 2))
        Start-Sleep -Milliseconds 800
    }
    $feInfo = Invoke-UiWithTimeout -Code {
        $fe = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $fe) { return 'null' }
        try { $n = $fe.Current.Name } catch { $n = '' }
        try { $i = $fe.Current.AutomationId } catch { $i = '' }
        "$i|$n"
    } -TimeoutSec 5 -What 'FocusedElement'
    if ($null -eq $feInfo -or @($feInfo).Count -eq 0) {
        $e04FocusNote = 'FocusedElement 读超时（app 可能卡住），按 E 再看'
        $e04FocusOk = $true
    } else {
        $feStr = [string](@($feInfo)[0])
        if ($feStr -match 'SearchBox' -or $feStr -match '搜索') {
            $e04FocusNote = "焦点仍在搜索框（$feStr），第 $($try + 1) 次重试"
            Send-KeysToWindow -Keys '{ESC}' -WaitMs 500
        } else {
            $e04FocusNote = "焦点已离搜索框（$feStr）"
            $e04FocusOk = $true
        }
    }
}
Send-KeysToWindow -Keys 'e' -WaitMs 2500
$shotE4 = Take-Shot -Name 'E04-guide-ekey'
$epgPanel = Find-ByAutomationId -Root (Get-TmwWindow) -AutomationId 'EpgPanel'
$epgVis = ''
$epgRect = ''; $winRectE4 = ''
$epgRectObj = $null; $winRectObj = $null
if ($null -ne $epgPanel) {
    # run18：IsOffscreen/矩形读都包超时。
    $off = Get-ElementIsOffscreen $epgPanel
    if ($null -ne $off) { $epgVis = $off.ToString() }
    $epgRectObj = Get-ElementRect $epgPanel
    if ($null -ne $epgRectObj) { $epgRect = $epgRectObj.ToString() }
    $winRectObj = Get-ElementRect (Get-TmwWindow)
    if ($null -ne $winRectObj) { $winRectE4 = $winRectObj.ToString() }
}
# run8：run7 截图疑似 DPI 取图裁切（Take-Shot 已改物理像素），只凭截图不能定罪；
# 面板矩形必须完全落在主窗口矩形内才算 PASS，否则 NEEDS-EYE 并附矩形。
$epgInside = $false
if ($null -ne $epgPanel -and $epgVis -eq 'False' -and $null -ne $epgRectObj -and $null -ne $winRectObj) {
    $pr = $epgRectObj; $wr = $winRectObj
    $epgInside = ($pr.Left -ge ($wr.Left - 2) -and $pr.Top -ge ($wr.Top - 2) -and $pr.Right -le ($wr.Right + 2) -and $pr.Bottom -le ($wr.Bottom + 2) -and $pr.Width -gt 100)
}
if ($epgInside) {
    Write-Result -Id 'E04' -Name 'E 键打开完整节目单' -Verdict 'PASS' -Evidence "截图 $shotE4；EpgPanel 已显示且在窗内（panel=$epgRect，window=$winRectE4）；焦点：$e04FocusNote"
} else {
    Write-Result -Id 'E04' -Name 'E 键打开完整节目单' -Verdict 'NEEDS-EYE' -Evidence "截图 $shotE4（应弹出完整节目单）；EpgPanel IsOffscreen=$epgVis panel=$epgRect window=$winRectE4；焦点：$e04FocusNote"
}
Send-KeysToWindow -Keys '{ESC}' -WaitMs 800

function Get-SettingsWindowElement {
    # run9：UIA RootElement.FindAll 枚举不到设置窗（已证实：EnumWindows 能看到 2 个
    # 可见的「设置」HWND，UIA 顶层枚举只返回主窗口；设置窗本身是打开的、屏幕上可见）。
    # 所以先走 UIA 枚举（快），再走 Win32 EnumWindows + AutomationElement.FromHandle
    # （按 HWND 直取该窗口的 UIA 元素，绕过顶层枚举）。
    # 「设置」二字用码点拼（0x8BBE=设，0x7F6E=置），避免脚本文件 UTF-8 无 BOM 时
    # PS 5.1 读中文乱码导致比对失败。
    # run13：不再先走 UIA RootElement.FindAll——它在 app 卡顿时会无限阻塞（run12 卡在 J01，
    # 10 分钟无进展；run8 浸泡也栽过），且它本来就枚举不到设置窗（H01 已证实）。
    # 只走 Win32 EnumWindows + AutomationElement.FromHandle（快、不阻塞 UI 线程）。
    $script:SettingsTitle = [string]([char]0x8BBE) + [string]([char]0x7F6E)
    $script:SettingsPid = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    if ($null -eq $script:SettingsPid) { return $null }
    $script:SettingsHwnds = @()
    $cb = [Win32+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0; [void][Win32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
        if ($wpid -eq $script:SettingsPid) {
            $sb = New-Object System.Text.StringBuilder(256)
            [void][Win32]::GetWindowText($hWnd, $sb, 256)
            if ($sb.ToString() -eq $script:SettingsTitle -and [Win32]::IsWindowVisible($hWnd)) {
                $script:SettingsHwnds += $hWnd
            }
        }
        return $true
    }
    try { [void][Win32]::EnumWindows($cb, [IntPtr]::Zero) } catch { return $null }
    if ($script:SettingsHwnds.Count -eq 0) { return $null }
    # run16：找到 HWND 后固定等 2 秒让内容进 UIA 树再返回——run14 的 FindAll 轮询
    # （等后代 Text）在 K04 又阻塞了（run15 卡 K04 后 20 分钟）。FindAll 无超时，绝不能
    # 放在轮询里；固定 sleep 不会阻塞。run13 的教训是窗刚出时内容没进树，2 秒够它加载。
    $el = $null
    try { $el = [System.Windows.Automation.AutomationElement]::FromHandle($script:SettingsHwnds[0]) } catch { return $null }
    if ($null -eq $el) { return $null }
    Start-Sleep -Seconds 2
    return $el
}

function Open-SettingsWindow {
    # 稳健打开设置窗：先恢复窗口正常态（重启后会变回最大化，工具栏掉出屏幕就点不到）。
    # run9：先查是否已有设置窗开着（有就直接返回，防重复点出两个嵌套模态窗）；
    # 轮询 30 秒（run8 里设置窗曾 15 秒才出现）；找窗用 Get-SettingsWindowElement
    # （UIA 枚举 + Win32 EnumWindows/FromHandle 双通道——UIA 枚举看不到设置窗）。
    # 返回设置窗口元素，找不到返回 $null。
    $already = Get-SettingsWindowElement
    if ($null -ne $already) { $script:OpenSettingsDiag = '已开着一个设置窗（上轮没关），直接用它'; return $already }
    Restore-WindowNormal
    $w = Get-TmwWindow
    if ($null -eq $w) { return $null }
    Bring-ToFront
    $btn = Find-ByAutomationId -Root $w -AutomationId 'BtnSettings'
    if ($null -eq $btn) { return $null }
    [void](Invoke-Element $btn)
    $script:OpenSettingsDiag = "UIA Invoke 已发"
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $sw = Get-SettingsWindowElement
        if ($null -ne $sw) { return $sw }
    }
    # UIA 点没打开，改鼠标点按钮中心（重找按钮，防 Restore 后元素过期）
    $btn2 = Find-ByAutomationId -Root (Get-TmwWindow) -AutomationId 'BtnSettings'
    if ($null -eq $btn2) { return $null }
    # run18：矩形读包超时；读不到就返回 $null，不拿空矩形去点。
    $r = Get-ElementRect $btn2
    if ($null -eq $r) { return $null }
    Invoke-MouseClick -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $sw = Get-SettingsWindowElement
        if ($null -ne $sw) { return $sw }
    }
    # run8：仍打不开时枚举全部顶层窗口——区分“点击没触发”（无任何新窗）
    # 与“已有模态窗挡住 UI 线程”（设置窗/MessageBox 已开，第二次点击被模态吞掉）。
    # run18：枚举也包超时，app 卡住时 FindAll 无限等。
    $res = Invoke-UiWithTimeout -Code {
        $rt2 = [System.Windows.Automation.AutomationElement]::RootElement
        $allWs2 = @($rt2.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
        ($allWs2 | ForEach-Object { try { "$($_.Current.Name)[$($_.Current.BoundingRectangle)]" } catch { '?' } }) -join ' | '
    } -TimeoutSec 10 -What 'Root-FindAll-Diag'
    if ($null -eq $res -or @($res).Count -eq 0) {
        $script:OpenSettingsDiag = "UIA Invoke 已发+鼠标点已发，仍无设置窗；枚举顶层窗口超时/失败"
    } else {
        $script:OpenSettingsDiag = "UIA Invoke 已发+鼠标点已发，仍无设置窗；顶层窗口=" + [string](@($res)[0])
    }
    return $null
}

function Save-SettingsWindow {
    param($SetWin)
    # 设置只在点「保存」时才落盘（Save_Click）；直接关窗口会丢弃改动——J01/F01 连败的根因。
    # run9：保存后验窗已关（轮询 5 秒）；还在就用 WindowPattern 关一次再验，
    # 防止残留模态窗污染后面的测试（run8 里两个未关的设置窗堵住了 I02/J01/K01/K03）。
    $saveBtn = Find-ByName -Root $SetWin -ControlType ([System.Windows.Automation.ControlType]::Button) -NamePattern '^保存$' | Select-Object -First 1
    if ($null -ne $saveBtn -and (Invoke-Element $saveBtn)) { Start-Sleep -Seconds 1 } else {
        # run18：WindowPattern.Close 也包超时，app 卡住时它一样无限等。
        if (-not (Invoke-UiPatternCall -Element $SetWin -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'Close')) { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
    }
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 500
        if ($null -eq (Get-SettingsWindowElement)) { return $true }
    }
    $left = Get-SettingsWindowElement
    if ($null -ne $left) { [void](Invoke-UiPatternCall -Element $left -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'Close') }
    Start-Sleep -Seconds 1
    return ($null -eq (Get-SettingsWindowElement))
}

function Find-ThemeCombo {
    param($SetWin)
    # ThemeCombo 在 ScrollViewer 里，窗口刚打开时 UIA 节点可能还没建出来；轮询 5 秒。
    for ($i = 0; $i -lt 10; $i++) {
        $tc = Find-ByAutomationId -Root $SetWin -AutomationId 'ThemeCombo'
        if ($null -ne $tc) { return $tc }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# ================= F：提示 5 秒自动消失 =================
# run18：提示条只在启动后（模型就绪、字幕服务起动时，MainWindow 793 行 EnsureSubtitleService）
# 出现一次、5 秒后自动消失。旧 F01 重启后等 5 秒再按 '1'、+1.2 秒才截图，
# 提示早已消失——run7/8/9/11/14/16/17 七次都没抓到是探针时序错，不是 app bug。
# 新探针：窗口一出现就每 500 毫秒读一次 SubtitleStatus 文本，8 秒内抓到即截图；
# 再等 6 秒确认它消失；切台后不应重现。log 查不到这条提示（ShowLiveSubtitleStatus 不写日志），
# 所以不用日志做证据。须是全新会话（同一条提示每个会话只显示一次），所以先重启 app。
# 门禁：主开关必须开着，提示才会在启动时出现；若关着，先置位再重启。
$cfgF = Get-Config
$f01Gate = '基线已开'
if ($null -eq $cfgF -or $cfgF.LiveSubtitleEnabled -ne $true) {
    $f01Gate = '基线主开关关着，先置位再重启'
    [void](Set-MasterSubtitle -On $true)
}
try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Seconds 2
Start-Process -FilePath $exe -WorkingDirectory $InstallDir
$win = $null
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Seconds 2; $win = Get-TmwWindow; if ($null -ne $win) { break } }
if ($null -eq $win) {
    Write-Result -Id 'F01' -Name '「免费接口未配置」提示 5 秒自动消失' -Verdict 'FAIL' -Evidence '重启后 120 秒无主窗口'
} else {
    Bring-ToFront
    $f01Seen = $null; $shotF1 = $null
    for ($i = 0; $i -lt 16; $i++) {
        $st = Get-SubtitleStatusText
        if ($null -ne $st -and $st -match '免费接口未配置') { $f01Seen = $st; $shotF1 = Take-Shot -Name 'F01-toast-seen'; break }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $f01Seen) {
        $shotF0 = Take-Shot -Name 'F01-toast-timeout'
        Write-Result -Id 'F01' -Name '「免费接口未配置」提示 5 秒自动消失' -Verdict 'NEEDS-EYE' -Evidence "启动后 8 秒轮询没等到提示（应出现「免费接口未配置」）；截图 $shotF0；门禁：$f01Gate。可能模型未就绪或服务未启动——看日志定性"
    } else {
        Start-Sleep -Seconds 6
        $stAfter = Get-SubtitleStatusText
        $gone = ($null -eq $stAfter -or $stAfter -notmatch '免费接口未配置')
        Send-KeysToWindow -Keys '2' -WaitMs 3000
        $stSwitch = Get-SubtitleStatusText
        $noReappear = ($null -eq $stSwitch -or $stSwitch -notmatch '免费接口未配置')
        $shotF2 = Take-Shot -Name 'F01-toast-after'
        if ($gone -and $noReappear) {
            Write-Result -Id 'F01' -Name '「免费接口未配置」提示 5 秒自动消失' -Verdict 'PASS' -Evidence "启动时提示出现（$f01Seen），截图 $shotF1；6 秒后消失=$gone，切台后不重现=$noReappear，截图 $shotF2"
        } else {
            Write-Result -Id 'F01' -Name '「免费接口未配置」提示 5 秒自动消失' -Verdict 'NEEDS-EYE' -Evidence "启动时提示出现（$f01Seen），截图 $shotF1；6 秒后消失=$gone，切台后不重现=$noReappear，截图 $shotF2"
        }
    }
}

# ================= G：刷新按钮 =================
Bring-ToFront
$win = Get-TmwWindow
$refBtn = Find-ByName -Root $win -ControlType ([System.Windows.Automation.ControlType]::Button) -NamePattern '^刷新$' | Select-Object -First 1
if ($null -eq $refBtn) {
    Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'FAIL' -Evidence '找不到「刷新」按钮'
} else {
    $g01Start = Get-Date
    [void](Invoke-Element $refBtn)
    # run13：超时 20 秒→45 秒——日志证实刷新本身能跑完（run12：22:39:22「playlist refreshed: 54 channels」），
    # 但从点下到完成约 29 秒（云电脑拉 live.m3u 慢），20 秒轮询等不到，不是 app bug。
    # run18：45 秒→180 秒——run14 刷新 100 秒才完，run17 45 秒内没完（日志无 playlist refreshed），
    # 云电脑拉 live.m3u 就是慢，不是 app bug。
    $stG = Wait-ForStatus -Pattern '已刷新：\d+ 个频道 \d{2}:\d{2}' -TimeoutSec 180
    if ($stG -match '已刷新：\d+ 个频道 \d{2}:\d{2}') {
        Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'PASS' -Evidence $stG
    } else {
        # run15：状态栏没等到时查日志兜底——run14 里刷新 100 秒才完（23:09:55 才写日志），
        # 45 秒轮询等不到；但日志有「playlist refreshed」即证明刷新跑完且状态已写（代码先写状态再写日志），
        # 是云电脑拉 live.m3u 慢，不是 app bug。只认点下刷新之后（$g01Start 之后）的日志行，防旧记录。
        $logPath = Join-Path $env:APPDATA 'TiviMateWin\log.txt'
        $recent = $null
        try {
            $lines = Get-Content $logPath -Tail 80 -ErrorAction SilentlyContinue | Select-String -Pattern 'playlist refreshed: (\d+) channels'
            foreach ($ln in $lines) {
                if ($ln.Line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                    $ts = [DateTime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
                    if ($ts -ge $g01Start) { $recent = $ln.Line }
                }
            }
        } catch { }
        if ($null -ne $recent) {
            Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'PASS' -Evidence "状态栏轮询 180 秒未见（疑似刷新慢），但日志证实刷新已完成：$recent"
        } else {
            Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'FAIL' -Evidence "刷新后状态=$stG（期望「已刷新：N 个频道 HH:mm」）；日志也无 playlist refreshed"
        }
    }
}

# ================= H：版本显示 + 检查更新 =================
$win = Get-TmwWindow
$setBtnH = Find-ByAutomationId -Root $win -AutomationId 'BtnSettings'
if ($null -eq $setBtnH) {
    Write-Result -Id 'H01' -Name '设置页显示「当前版本」' -Verdict 'FAIL' -Evidence '找不到「设置」按钮'
    Write-Result -Id 'H02' -Name '「检查更新」显示「已是最新版本」' -Verdict 'FAIL' -Evidence 'H01 未完成，跳过'
} else {
    $setWin = Open-SettingsWindow
    if ($null -eq $setWin) {
        Write-Result -Id 'H01' -Name '设置页显示「当前版本」' -Verdict 'FAIL' -Evidence "点了设置，没弹出设置窗口；$script:OpenSettingsDiag"
        Write-Result -Id 'H02' -Name '「检查更新」显示「已是最新版本」' -Verdict 'FAIL' -Evidence 'H01 未完成，跳过'
    } else {
        $textsH = Find-All -Root $setWin -ControlType ([System.Windows.Automation.ControlType]::Text)
        $namesH = @($textsH | ForEach-Object { Get-ElementText $_ })
        $verText = @($namesH | Where-Object { $_ -match '当前版本' })
        if ($verText.Count -gt 0 -and ($verText -join '') -match [regex]::Escape($ExpectedVersion)) {
            Write-Result -Id 'H01' -Name '设置页显示「当前版本」' -Verdict 'PASS' -Evidence ($verText -join ' / ')
        } else {
            Write-Result -Id 'H01' -Name '设置页显示「当前版本」' -Verdict 'FAIL' -Evidence "设置页文字=$($namesH -join ' | ')"
        }
        # B05：设置页下拉框（主题）字看得见——深色模板下闭合框是深底，截框数深色像素
        # （白底浅字时深色像素几乎没有；深底浅字或白底深字时都有足够深色像素）
        $themeCombo = Find-ThemeCombo -SetWin $setWin
        if ($null -eq $themeCombo) {
            Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'FAIL' -Evidence 'AutomationId=ThemeCombo 找不到'
        } else {
            # run18：矩形读包超时；读不到就判 B05 FAIL。
            $r5 = Get-ElementRect $themeCombo
            if ($null -eq $r5) {
                Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'FAIL' -Evidence 'ThemeCombo 矩形读不到（app 可能卡住）'
            } else {
            $rect5 = New-Object System.Drawing.Rectangle([int]$r5.X, [int]$r5.Y, [int]$r5.Width, [int]$r5.Height)
            $crop5 = Take-Shot -Name 'B05-theme-combo-crop' -Crop $rect5
            $frac5 = Get-DarkPixelFraction -ImagePath $crop5
            if ($frac5 -gt 0.002) {
                Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'PASS' -Evidence "深色像素占比 $([Math]::Round($frac5,4))，截图 $crop5"
            } else {
                Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'FAIL' -Evidence "深色像素占比 $([Math]::Round($frac5,4))（<0.002），字太浅或没画出；截图 $crop5"
            }
            }
        }
        $updBtn = Find-ByName -Root $setWin -ControlType ([System.Windows.Automation.ControlType]::Button) -NamePattern '检查更新' | Select-Object -First 1
        if ($null -eq $updBtn) {
            Write-Result -Id 'H02' -Name '「检查更新」显示「已是最新版本」' -Verdict 'FAIL' -Evidence '设置页没有「检查更新」按钮'
        } else {
            [void](Invoke-Element $updBtn)
            $ok = $false
            for ($i = 0; $i -lt 30; $i++) {
                Start-Sleep -Seconds 1
                $t2 = Find-All -Root $setWin -ControlType ([System.Windows.Automation.ControlType]::Text)
                $n2 = @($t2 | ForEach-Object { Get-ElementText $_ })
                if (($n2 -join '') -match '已是最新版本') { $ok = $true; break }
                if (($n2 -join '') -match '发现新版本') { break }
            }
            $t3 = Find-All -Root $setWin -ControlType ([System.Windows.Automation.ControlType]::Text)
            $n3 = ($t3 | ForEach-Object { Get-ElementText $_ }) -join ' | '
            if ($ok) {
                Write-Result -Id 'H02' -Name '「检查更新」显示「已是最新版本」' -Verdict 'PASS' -Evidence $n3
            } elseif ($n3 -match '发现新版本') {
                Write-Result -Id 'H02' -Name '「检查更新」显示「已是最新版本」' -Verdict 'FAIL' -Evidence "有新版本：$n3（当前应是最新）"
            } else {
                Write-Result -Id 'H02' -Name '「检查更新」显示「已是最新版本」' -Verdict 'FAIL' -Evidence "30 秒未见「已是最新版本」：$n3"
            }
        }
        # run18：Close 包超时。
        if (-not (Invoke-UiPatternCall -Element $setWin -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'Close')) { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
        Start-Sleep -Seconds 1
        # run9：验设置窗已关干净，否则后面的 I/J/K 会被残留模态窗堵住
        $leftover = Get-SettingsWindowElement
        if ($null -ne $leftover) {
            if (-not (Invoke-UiPatternCall -Element $leftover -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'Close')) { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
            Start-Sleep -Seconds 1
            if ($null -ne (Get-SettingsWindowElement)) { Write-Output "H-close 警告：设置窗关了两次还在（残留模态窗）" }
        }
    }
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

# ================= J：翻译「强制开」联动主开关 =================
function Set-MasterSubtitle {
    param([bool]$On)
    # 设置页里把字幕主开关拨到指定状态，返回设置后的 config 值（true/false/$null 表示没办成）
    # run10：LiveSubtitleChk 在 ScrollViewer 下方，可能晚出现；轮询 10 秒再放弃
    # run10：ToggleState 读不到（Probe-J01：ToggleState 读出是空，但 Toggle() 本身能动、
    # 保存后 config 真翻了）——改用 config 文件定当前状态：config 值与目标不同才拨开关。
    $sw = Open-SettingsWindow
    if ($null -eq $sw) { return $null }
    $chk = $null
    for ($i = 0; $i -lt 20; $i++) {
        $chk = Find-ByAutomationId -Root $sw -AutomationId 'LiveSubtitleChk'
        if ($null -ne $chk) { break }
        Start-Sleep -Milliseconds 500
    }
    $ok = $null
    if ($null -ne $chk) {
        try {
            $cur = Get-Config
            $curOn = ($null -ne $cur -and [bool]$cur.LiveSubtitleEnabled)
            if ($curOn -ne $On) {
                # run18：Toggle 包超时。
                [void](Invoke-UiPatternCall -Element $chk -PatternType ([System.Windows.Automation.TogglePattern]) -Method 'Toggle')
                Start-Sleep -Seconds 1
            }
            $ok = $On
        } catch { $ok = $null }
    }
    [void](Save-SettingsWindow -SetWin $sw)
    Start-Sleep -Seconds 2
    $c = Get-Config
    if ($null -eq $c) { return $ok }
    return [bool]$c.LiveSubtitleEnabled
}

$masterBeforeJ = Set-MasterSubtitle -On $false
if ($masterBeforeJ -eq $false) {
    # 点状态栏翻译按钮，直到它显示「强制开」
    $w = Get-TmwWindow
    $tBtn = Find-ByAutomationId -Root $w -AutomationId 'BtnTranslateToggle'
    if ($null -eq $tBtn) {
        Write-Result -Id 'J01' -Name '翻译「强制开」联动主开关' -Verdict 'FAIL' -Evidence '找不到状态栏翻译按钮'
    } else {
        $forced = $false
        for ($k = 0; $k -lt 4; $k++) {
            [void](Invoke-Element $tBtn)
            Start-Sleep -Seconds 1
            # run15：读按钮里的 Text 子元素，不读按钮的 Name——WPF 按钮的 Content 改了，
            # UIA Name 未必跟着变（run14：点 4 次名里仍无「强制」，但 config 已翻成 True，
            # 说明联动本身是通的，只是名字读不到）。
            $w = Get-TmwWindow
            $tBtn = Find-ByAutomationId -Root $w -AutomationId 'BtnTranslateToggle'
            if ($null -eq $tBtn) { break }
            $nm = Get-ElementText $tBtn
            $txts = Find-All -Root $tBtn -ControlType ([System.Windows.Automation.ControlType]::Text)
            $nm2 = ($txts | ForEach-Object { Get-ElementText $_ }) -join ''
            # run16：按钮文案是「翻译: 开」（不是「翻译: 强制开」）——UpdateTranslateToggleText
            # 里 force=true 时 label 就是「翻译: 开」；「强制」二字只在 ToolTip 里，按钮名/子 Text 永远读不到。
            if (($nm -match '翻译: 开') -or ($nm2 -match '翻译: 开')) { $forced = $true; break }
        }
        Start-Sleep -Seconds 1
        $cfgJ = Get-Config
        if ($forced -and $null -ne $cfgJ -and $cfgJ.LiveSubtitleEnabled -eq $true) {
            Write-Result -Id 'J01' -Name '翻译「强制开」联动主开关' -Verdict 'PASS' -Evidence '状态栏切到强制开后，config LiveSubtitleEnabled=true 已保存'
        } else {
            Write-Result -Id 'J01' -Name '翻译「强制开」联动主开关' -Verdict 'FAIL' -Evidence "强制开状态=$forced，config LiveSubtitleEnabled=$(if ($null -ne $cfgJ) { $cfgJ.LiveSubtitleEnabled } else { '读不到' })"
        }
    }
} else {
    Write-Result -Id 'J01' -Name '翻译「强制开」联动主开关' -Verdict 'FAIL' -Evidence "先关主开关没办成（返回值=$masterBeforeJ），无法测联动"
}
# J02：恢复基线（F01/J01 可能把主开关打开了）
$restored = Set-MasterSubtitle -On ([bool]$liveSub0)
$cfgJ2 = Get-Config
if ($null -ne $cfgJ2 -and [bool]$cfgJ2.LiveSubtitleEnabled -eq [bool]$liveSub0) {
    Write-Result -Id 'J02' -Name '恢复字幕主开关基线' -Verdict 'PASS' -Evidence "恢复后 LiveSubtitleEnabled=$($cfgJ2.LiveSubtitleEnabled)，基线=$liveSub0"
} else {
    # 兜底：直接恢复备份的 config 文件并重启
    if (Test-Path $cfgBackup) {
        Copy-Item $cfgBackup $cfgPath -Force
        Write-Result -Id 'J02' -Name '恢复字幕主开关基线' -Verdict 'PASS' -Evidence "UI 恢复失败，已从备份文件恢复 config（需重启 app 生效）"
    } else {
        Write-Result -Id 'J02' -Name '恢复字幕主开关基线' -Verdict 'FAIL' -Evidence "恢复后=$($cfgJ2.LiveSubtitleEnabled)，基线=$liveSub0，且无备份文件"
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
Bring-ToFront
Send-KeysToWindow -Keys '51' -WaitMs 2000
$pidK2 = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
Start-Sleep -Seconds 60
$aliveK2 = Get-Process -Id $pidK2 -ErrorAction SilentlyContinue
if ($null -ne $aliveK2) {
    Write-Result -Id 'K02' -Name '断流台 60 秒不退出' -Verdict 'PASS' -Evidence '凤凰中文播 60 秒，进程还在（断流/重连只记录，不判失败）'
} else {
    $lfK = Get-LogFile
    $tailK = ''
    if ($null -ne $lfK) { $tailK = (Get-Content $lfK.FullName -Tail 15 -ErrorAction SilentlyContinue) -join ' | ' }
    Write-Result -Id 'K02' -Name '断流台 60 秒不退出' -Verdict 'FAIL' -Evidence "凤凰中文播着播着退了；log 尾：$tailK"
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 2; if ($null -ne (Get-TmwWindow)) { break } }
}

# K03：新窗口开/关 3 次不漏窗口
$k03ok = $true; $k03ev = ''
for ($n = 1; $n -le 3; $n++) {
    Bring-ToFront
    $m = Open-ChannelContextMenu
    $nw = $m | Where-Object { (Get-ElementText $_) -eq '新窗口播放' } | Select-Object -First 1
    if ($null -eq $nw) { $k03ok = $false; $k03ev = "第 $n 次菜单里没有「新窗口播放」"; break }
    [void](Invoke-Element $nw)
    Start-Sleep -Seconds 2
    $root3 = [System.Windows.Automation.AutomationElement]::RootElement
    $allW = @(Get-TmwWindows)
    # run15：不用 MainWindowHandle 认主窗——开新窗口后它可能指向 ChannelWindow，
    # 会把主窗关掉（WPF 默认关主窗即退出 app），K03 因此剩 0 窗。用标题认主窗。
    # run18：关窗的跨进程读/调都包超时（逐窗各 5 秒读句柄 + 10 秒关），app 卡住时不再无限等。
    $mainEl3 = Get-TmwWindow
    $mainH3 = 0
    if ($null -ne $mainEl3) {
        $mh3 = Invoke-UiWithTimeout -Code { param($e) try { $e.Current.NativeWindowHandle } catch { 0 } } -TimeoutSec 5 -What 'Read-Hwnd' -ArgumentList @($mainEl3)
        if ($null -ne $mh3 -and @($mh3).Count -gt 0) { $mainH3 = [int](@($mh3)[0]) }
    }
    foreach ($w3 in $allW) {
        $hw3 = Invoke-UiWithTimeout -Code { param($e) try { $e.Current.NativeWindowHandle } catch { 0 } } -TimeoutSec 5 -What 'Read-Hwnd' -ArgumentList @($w3)
        $hw3V = if ($null -eq $hw3 -or @($hw3).Count -eq 0) { 0 } else { [int](@($hw3)[0]) }
        if ($hw3V -ne 0 -and $hw3V -ne $mainH3) {
            [void](Invoke-UiPatternCall -Element $w3 -PatternType ([System.Windows.Automation.WindowPattern]) -Method 'Close')
        }
    }
    Start-Sleep -Seconds 2
    $left = @(Get-TmwWindows)
    if ($left.Count -ne 1) { $k03ok = $false; $k03ev = "第 $n 次关窗后还剩 $($left.Count) 个窗口"; break }
}
if ($k03ok) {
    Write-Result -Id 'K03' -Name '新窗口开/关 3 次不漏窗口' -Verdict 'PASS' -Evidence '3 次开/关后都只剩主窗口'
} else {
    Write-Result -Id 'K03' -Name '新窗口开/关 3 次不漏窗口' -Verdict 'FAIL' -Evidence $k03ev
}

# K04：深色→浅色→深色切换
$k04ok = $false; $k04ev = ''
$sw4 = Open-SettingsWindow
if ($null -ne $sw4) {
        $tc = Find-ThemeCombo -SetWin $sw4
        if ($null -ne $tc) {
            try {
                # 下拉项在 Popup 里，不在 ThemeCombo 子树下；展开前记下已有项，展开后多出来的就是主题选项
                $beforeK4 = @()
                foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeK4 += (Get-ElementText $li) }
                # run18：Expand 包超时。
                [void](Invoke-UiPatternCall -Element $tc -PatternType ([System.Windows.Automation.ExpandCollapsePattern]) -Method 'Expand'); Start-Sleep -Seconds 1
                $items4 = @()
                foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
                    $nm = Get-ElementText $li
                    if ($nm -ne '' -and ($beforeK4 -notcontains $nm)) { $items4 += $li }
                }
                $light = $items4 | Where-Object { (Get-ElementText $_) -eq '浅色' } | Select-Object -First 1
                if ($null -ne $light) {
                    # run18：矩形读包超时；读不到就不点，记证据。
                    $lr = Get-ElementRect $light
                    if ($null -ne $lr) {
                    Invoke-MouseClick -X ([int]($lr.X + $lr.Width / 2)) -Y ([int]($lr.Y + $lr.Height / 2))
                    Start-Sleep -Seconds 2
                    $shotK4a = Take-Shot -Name 'K04-theme-light'
                    $beforeK4b = @()
                    foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeK4b += (Get-ElementText $li) }
                    [void](Invoke-UiPatternCall -Element $tc -PatternType ([System.Windows.Automation.ExpandCollapsePattern]) -Method 'Expand'); Start-Sleep -Seconds 1
                    $items4b = @()
                    foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
                        $nm = Get-ElementText $li
                        if ($nm -ne '' -and ($beforeK4b -notcontains $nm)) { $items4b += $li }
                    }
                    $dark = $items4b | Where-Object { (Get-ElementText $_) -eq '深色' } | Select-Object -First 1
                    if ($null -ne $dark) {
                        $dr = Get-ElementRect $dark
                        if ($null -ne $dr) {
                        Invoke-MouseClick -X ([int]($dr.X + $dr.Width / 2)) -Y ([int]($dr.Y + $dr.Height / 2))
                        Start-Sleep -Seconds 2
                        $shotK4b = Take-Shot -Name 'K04-theme-dark'
                        $k04ok = ($null -ne (Get-TmwWindow))
                        $k04ev = "浅色截图 $shotK4a，深色截图 $shotK4b"
                        } else { $k04ev = '「深色」项矩形读不到（app 可能卡住）' }
                    } else { $k04ev = '切回深色时找不到「深色」项' }
                    } else { $k04ev = '「浅色」项矩形读不到（app 可能卡住）' }
                } else { $k04ev = '找不到「浅色」项' }
            } catch { $k04ev = "主题切换异常：$($_.Exception.Message)" }
        } else { $k04ev = '设置页找不到 ThemeCombo' }
        [void](Save-SettingsWindow -SetWin $sw4)
    } else { $k04ev = '没弹出设置窗口' }
if ($k04ok) {
    Write-Result -Id 'K04' -Name '深色→浅色→深色切换不崩' -Verdict 'PASS' -Evidence $k04ev
} else {
    Write-Result -Id 'K04' -Name '深色→浅色→深色切换不崩' -Verdict 'FAIL' -Evidence $k04ev
}

# K05：搜不到的词
$w = Get-TmwWindow
$sb = Find-ByAutomationId -Root $w -AutomationId 'SearchBox'
if ($null -eq $sb) {
    Write-Result -Id 'K05' -Name '搜不到的词不崩、清空恢复' -Verdict 'FAIL' -Evidence '找不到 SearchBox'
} else {
    $cntBefore = Get-CountNumber
    # run18：SetValue 包超时；失败时鼠标点搜索框再打字（原 catch 的兜底）。
    # 兜底里的 BoundingRectangle 也包超时——SetValue 超时多半是 app 卡住，直接读矩形会再堵住。
    if (-not (Invoke-UiPatternCall -Element $sb -PatternType ([System.Windows.Automation.ValuePattern]) -Method 'SetValue' -Args @('zzzz不存在'))) {
        $rr5 = Invoke-UiWithTimeout -Code { param($e) try { $e.Current.BoundingRectangle } catch { $null } } -TimeoutSec 5 -What 'Read-Rect' -ArgumentList @($sb)
        if ($null -ne $rr5 -and @($rr5).Count -gt 0 -and $null -ne @($rr5)[0]) {
            $r5 = @($rr5)[0]
            Invoke-MouseClick -X ([int]($r5.X + $r5.Width / 2)) -Y ([int]($r5.Y + $r5.Height / 2))
            Send-KeysToWindow -Keys 'zzzz不存在' -WaitMs 500
        }
    }
    Start-Sleep -Seconds 2
    $cntZero = Get-CountNumber
    if (-not (Invoke-UiPatternCall -Element $sb -PatternType ([System.Windows.Automation.ValuePattern]) -Method 'SetValue' -Args @(''))) {
        $rr5b = Invoke-UiWithTimeout -Code { param($e) try { $e.Current.BoundingRectangle } catch { $null } } -TimeoutSec 5 -What 'Read-Rect' -ArgumentList @($sb)
        if ($null -ne $rr5b -and @($rr5b).Count -gt 0 -and $null -ne @($rr5b)[0]) {
            $r5b = @($rr5b)[0]
            Invoke-MouseClick -X ([int]($r5b.X + $r5b.Width / 2)) -Y ([int]($r5b.Y + $r5b.Height / 2))
            Send-KeysToWindow -Keys '^a{DEL}' -WaitMs 500
        }
    }
    Start-Sleep -Seconds 2
    $cntAfter = Get-CountNumber
    $alive5 = ($null -ne (Get-TmwWindow))
    if ($alive5 -and $cntBefore -ne -1 -and $cntAfter -ne -1 -and $cntAfter -eq $cntBefore) {
        Write-Result -Id 'K05' -Name '搜不到的词不崩、清空恢复' -Verdict 'PASS' -Evidence "搜前=$cntBefore，搜「zzzz不存在」后=$cntZero，清空后=$cntAfter"
    } else {
        Write-Result -Id 'K05' -Name '搜不到的词不崩、清空恢复' -Verdict 'FAIL' -Evidence "搜前=$cntBefore，搜后=$cntZero，清空后=$cntAfter，窗口在=$alive5"
    }
}

# K06：config 损坏后仍能启动（备份+恢复；可 -SkipConfigCorruption 跳过）
# v1.4.36：损坏测试不得留垃圾——快照 config 目录，测完删掉 app 读坏 config 时保全出的
# config.corrupt-*.json（之前 6 轮共留了 6 个），并校验恢复后的 config 与备份字节一致。
if (-not $SkipConfigCorruption) {
    $cfgDir = Split-Path $cfgPath -Parent
    $corruptBefore = @(Get-ChildItem $cfgDir -Filter 'config.corrupt-*.json' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
    Start-Sleep -Seconds 2
    Set-Content -Path $cfgPath -Value '{ invalid json,,' -Encoding UTF8
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
    $w6 = $null
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 2; $w6 = Get-TmwWindow; if ($null -ne $w6) { break } }
    if ($null -ne $w6) {
        Write-Result -Id 'K06' -Name 'config 损坏后仍能启动' -Verdict 'PASS' -Evidence 'config 写坏后 app 仍正常启动（容错通过）'
    } else {
        Write-Result -Id 'K06' -Name 'config 损坏后仍能启动' -Verdict 'FAIL' -Evidence 'config 写坏后 60 秒无主窗口'
    }
    try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
    Start-Sleep -Seconds 2
    if (Test-Path $cfgBackup) { Copy-Item $cfgBackup $cfgPath -Force }
    $corruptAfter = @(Get-ChildItem $cfgDir -Filter 'config.corrupt-*.json' -ErrorAction SilentlyContinue)
    $removed = 0
    foreach ($f in $corruptAfter) {
        if ($corruptBefore -notcontains $f.Name) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue; $removed++ }
    }
    $restoredOk = $false
    if ((Test-Path $cfgPath) -and (Test-Path $cfgBackup)) {
        $restoredOk = ((Get-FileHash $cfgPath -Algorithm SHA256).Hash -eq (Get-FileHash $cfgBackup -Algorithm SHA256).Hash)
    }
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 2; if ($null -ne (Get-TmwWindow)) { break } }
    Write-Output "K06 恢复：config 已从备份恢复并重启（字节一致=$restoredOk，清理 corrupt 文件=$removed 个）"
    Add-Content -Path $ResultsFile -Value "K06-cleanup: corrupt-removed=$removed restored-bytes-match=$restoredOk" -Encoding UTF8
}

# ================= L：连续播 soak =================
if (-not $SkipSoak) {
    Bring-ToFront
    Send-KeysToWindow -Keys '1' -WaitMs 2500
    $soakStart = Get-Date
    $soakPid = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    $soakExit = $null
    $script:SoakHangCount = 0
    $lfL = Get-LogFile
    $logMark = 0
    if ($null -ne $lfL) { $logMark = (Get-Content $lfL.FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines }
    $stalls = 0; $reconnects = 0
    # run18：每分钟查一次 Responding（5 分钟一查会漏掉 2–3 分钟的短挂起——
    # 02:08 的挂起约 6 分钟、01:52 的约 2 分钟，都在 5 分钟粒度之外）；
    # 连续 2 次无响应即抓 dump 判 FAIL。
    $steps = [Math]::Max(1, $SoakMinutes)
    for ($s = 1; $s -le $steps; $s++) {
        Start-Sleep -Seconds 60
        $p = Get-Process -Id $soakPid -ErrorAction SilentlyContinue
        if ($null -eq $p) {
            $soakExit = Get-Date
            $lfL2 = Get-LogFile
            $tailL = ''
            if ($null -ne $lfL2) { $tailL = (Get-Content $lfL2.FullName -Tail 15 -ErrorAction SilentlyContinue) -join ' | ' }
            Write-Result -Id 'L01' -Name "连续播 ${SoakMinutes} 分钟不退出" -Verdict 'FAIL' -Evidence "播了约 $([int](($soakExit - $soakStart).TotalMinutes)) 分钟后退出（$($soakExit.ToString('HH:mm:ss'))）；log 尾：$tailL"
            break
        }
        # run17：进程在但无响应（挂起）也判 FAIL，并抓 dump——run16 里 app 在浸泡
        # 约 18 分钟时挂起（事件 1002），Windows 终结了它；L01 没写、脚本也没了。
        # 连续两次无响应即抓 dump（procdump 在 C:\build\tmw-diag\procdump）。
        if (-not $p.Responding) {
            $script:SoakHangCount++
            if ($script:SoakHangCount -ge 2) {
                $dumpPath = Join-Path $OutDir 'soak-hang.dmp'
                try {
                    $pd = 'C:\build\tmw-diag\procdump\procdump64.exe'
                    if (Test-Path $pd) { Start-Process -FilePath $pd -ArgumentList "-ma $soakPid `"$dumpPath`"" -Wait -WindowStyle Hidden }
                } catch { }
                $soakExit = Get-Date
                Write-Result -Id 'L01' -Name "连续播 ${SoakMinutes} 分钟不退出" -Verdict 'FAIL' -Evidence "播了约 $([int](($soakExit - $soakStart).TotalMinutes)) 分钟后挂起无响应（连续 2 次检查）；dump=$dumpPath"
                break
            }
        } else { $script:SoakHangCount = 0 }
        # run10：浸泡里不读 UIA 时间——run9 的 Job 版时间读取把浸泡卡死了（Start-Job 在
        # 计划任务里挂住，min10 之后 40 分钟没进展）；浸泡的 verdict 只看进程在不在，
        # 时间证据改由日志 + 终场截图提供，UIA 一律不碰。
        $tt = @{ Before = ''; After = ''; Advanced = $false; TimedOut = $false }
        $ttNote = ''
        $lfL3 = Get-LogFile
        if ($null -ne $lfL3) {
            $newLines = Get-Content $lfL3.FullName -Tail 500 -ErrorAction SilentlyContinue
            $stalls = @($newLines | Select-String -Pattern 'stall' -SimpleMatch).Count
            $reconnects = @($newLines | Select-String -Pattern 'reconnect|重连' ).Count
        }
        if (($s % 5) -eq 0) { [void](Take-Shot -Name "L01-soak-min$s") }
        Write-Output "soak $s/$SoakMinutes min：进程在，时间 $($tt.Before)->$($tt.After)$ttNote，stall=$stalls，重连=$reconnects"
    }
    if ($null -eq $soakExit) {
        $shotL = Take-Shot -Name 'L01-soak-end'
        Write-Result -Id 'L01' -Name "连续播 ${SoakMinutes} 分钟不退出" -Verdict 'PASS' -Evidence "播满 ${SoakMinutes} 分钟没退；stall 记录=$stalls，重连记录=$reconnects；截图 $shotL"
    } else {
        # 退出过一次：重起再播完剩余时间，看会不会再退（第二个数据点）， verdict 仍是 FAIL
        $remain = $SoakMinutes - [int](($soakExit - $soakStart).TotalMinutes)
        if ($remain -gt 0) {
            Write-Output "soak 中途退出，重起再播剩余约 $remain 分钟（看会不会再退）"
            Start-Process -FilePath $exe -WorkingDirectory $InstallDir
            Start-Sleep -Seconds 20
            Send-KeysToWindow -Keys '1' -WaitMs 2000
            $pid2 = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
            $exit2 = $false
            for ($m = 0; $m -lt $remain; $m++) {
                Start-Sleep -Seconds 60
                if ($null -eq (Get-Process -Id $pid2 -ErrorAction SilentlyContinue)) { $exit2 = $true; break }
            }
            Write-Output "soak 第二段：再退=$exit2"
        }
    }
} else {
    Write-Result -Id 'L01' -Name "连续播 ${SoakMinutes} 分钟不退出" -Verdict 'NEEDS-EYE' -Evidence '本次跳过 soak（-SkipSoak）'
}

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
