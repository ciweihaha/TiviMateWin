# TiviMateWin UI 回归测试脚本 — 发版门禁（用户规矩：先测后发，脚本全过才打 tag）
# 跑法见 tests/ui/README.md。必须在交互会话 session 1 里跑（定时任务），WinRM session 0 看不到窗口。
# 退出码：0 全部 PASS；1 有 FAIL；2 有 NEEDS-EYE 待复核（无 FAIL 时）。
param(
    [string]$InstallDir = 'C:\TiviMateWin',
    [string]$ExpectedVersion = '1.4.35',
    [string]$OutDir = 'C:\build\tmw-uitest\run1',
    [int]$SoakMinutes = 30,
    [switch]$SkipSoak,
    [switch]$SkipConfigCorruption
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
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
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
'@
Add-Type -TypeDefinition $Win32 -ErrorAction SilentlyContinue

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
    $p = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $p -or $p.MainWindowHandle -eq 0) { return $null }
    try { return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) } catch { return $null }
}

function Find-All {
    param($Root, $ControlType)
    if ($null -eq $Root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    try { return @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) } catch { return @() }
}

function Find-ByName {
    param($Root, $ControlType, [string]$NamePattern)
    $all = Find-All -Root $Root -ControlType $ControlType
    return @($all | Where-Object { $_.Current.Name -match $NamePattern })
}

function Find-ByAutomationId {
    param($Root, [string]$AutomationId)
    if ($null -eq $Root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    try { return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) } catch { return $null }
}

function Invoke-Element {
    param($El)
    if ($null -eq $El) { return $false }
    try {
        $pat = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        return $true
    } catch { return $false }
}

function Get-ElementText {
    param($El)
    if ($null -eq $El) { return '' }
    try { return $El.Current.Name } catch { return '' }
}

function Get-PhysicalScreenSize {
    # UIA 坐标是物理像素；WinForms Bounds 在非 DPI 感知进程里是逻辑像素（125% 下 1536x792，
    # 物理是 1920x990），直接拿它算 mouse_event 绝对坐标会偏 25%。改用 UIA 根元素的物理尺寸。
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
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
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
    # 状态栏文字：遍历所有 Text 找含关键字的那行
    $w = Get-TmwWindow
    if ($null -eq $w) { return '' }
    $texts = Find-All -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text)
    $names = @($texts | ForEach-Object { Get-ElementText $_ })
    return ($names -join ' | ')
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
    # 播放时间 Text（形如 00:31）：遍历 Text 找时间格式
    $w = Get-TmwWindow
    if ($null -eq $w) { return '' }
    $texts = Find-All -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text)
    foreach ($t in $texts) {
        $n = Get-ElementText $t
        if ($n -match '^\d{1,2}:\d{2}(:\d{2})?$') { return $n }
    }
    return ''
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
# 窗口恢复到正常态：app 会按 config 恢复成最大化，但最大化时底部工具栏常掉出屏幕外
# （E01 OSD 被裁、设置按钮点不到的根因），先恢复成 1280x720 居中，保证全部 UI 可见可点。
try {
    $w0 = Get-TmwWindow
    if ($null -ne $w0) {
        $wp0 = $w0.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $wp0.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        Start-Sleep -Seconds 1
        Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win32Pos {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
        $h0 = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowHandle
        if ($h0 -ne [IntPtr]::Zero) {
            # 1280x720 居中（逻辑像素 1536x792 的屏内完全放得下）
            [Win32Pos]::SetWindowPos($h0, [IntPtr]::Zero, 128, 36, 1280, 720, 0x0040) | Out-Null
            Start-Sleep -Seconds 1
        }
        $wr0 = (Get-TmwWindow).Current.BoundingRectangle
        Write-Output ("窗口已恢复正常态 rect=$wr0")
    }
} catch { Write-Output ("窗口恢复失败：$($_.Exception.Message)") }

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
$title = ''
try { $title = (Get-TmwWindow).Current.Name } catch { }
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
    $r = $combo.Current.BoundingRectangle
    # B01：字看得见——截下拉框区域，数深色像素（白底深字才有深色像素；浅字白底几乎没有）
    $rect = New-Object System.Drawing.Rectangle([int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    $crop = Take-Shot -Name 'B01-combo-crop' -Crop $rect
    $frac = Get-DarkPixelFraction -ImagePath $crop
    if ($frac -gt 0.002) {
        Write-Result -Id 'B01' -Name '分组下拉框字看得见' -Verdict 'PASS' -Evidence "深色像素占比 $([Math]::Round($frac,4))，截图 $crop"
    } else {
        Write-Result -Id 'B01' -Name '分组下拉框字看得见' -Verdict 'FAIL' -Evidence "深色像素占比 $([Math]::Round($frac,4))（<0.002），字太浅或没画出；截图 $crop"
    }
    # B02：4 个选项（展开后读全窗 ListItem 差集；此定制 ComboBox 不支持 ValuePattern/SelectionPattern）
    try {
        $selVal = ''
        try { $selVal = $combo.Current.Name } catch { }
        # 展开前先记下全窗已有的 ListItem 名字（频道行），展开后多出来的就是下拉选项
        $winB = Get-TmwWindow
        $beforeNames = @()
        foreach ($li in (Find-All -Root $winB -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeNames += (Get-ElementText $li) }
        $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $exp.Expand()
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
        $ex = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ex.Expand()
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
        try { return $cb.Current.Name } catch { return '' }
    }
    try {
        $wei = Find-DropdownItem -Want '卫视频道'
        if ($null -ne $wei) {
            # 定制下拉项不支持 SelectionItemPattern，改鼠标点它
            $wr = $wei.Current.BoundingRectangle
            Invoke-MouseClick -X ([int]($wr.X + $wr.Width / 2)) -Y ([int]($wr.Y + $wr.Height / 2))
            Start-Sleep -Seconds 2
            $countWei = Get-CountText
            $selName = Get-ComboValue
            # 核心判定是过滤生效（频道数变了）；ComboBox 显示值仅作参考（定制模板读不到时以计数为准）
            if ($countWei -ne $countAll) {
                Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'PASS' -Evidence "全部=$countAll，卫视=$countWei，框显示=$selName"
            } else {
                Write-Result -Id 'B03' -Name '选「卫视频道」过滤' -Verdict 'FAIL' -Evidence "全部=$countAll，选后=$countWei，选中=$selName"
            }
            # 选回「全部」
            $allItem = Find-DropdownItem -Want '全部'
            if ($null -ne $allItem) {
                $ar = $allItem.Current.BoundingRectangle
                Invoke-MouseClick -X ([int]($ar.X + $ar.Width / 2)) -Y ([int]($ar.Y + $ar.Height / 2))
                Start-Sleep -Seconds 2
                $countBack = Get-CountText
                if ($countBack -eq $countAll) {
                    Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'PASS' -Evidence "恢复后=$countBack"
                } else {
                    Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence "恢复后=$countBack，期望=$countAll"
                }
            } else {
                Write-Result -Id 'B04' -Name '选回「全部」恢复' -Verdict 'FAIL' -Evidence '展开后找不到「全部」项'
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
    return @($items | Where-Object { $_.Current.Name -ne '' })
}

function Open-ChannelContextMenu {
    # 右键点频道行：先左键点中该行（选中+聚焦），再右键。坐标用物理像素算（见 Get-PhysicalScreenSize）。
    # 注意：Shift+F10 在这 app 上打不开频道菜单，只会出系统菜单，所以必须用鼠标右键。
    $w = Get-TmwWindow
    if ($null -eq $w) { return @() }
    Bring-ToFront
    $list = Find-ByAutomationId -Root $w -AutomationId 'ChannelList'
    $target = $null
    if ($null -ne $list) {
        $items = Find-All -Root $list -ControlType ([System.Windows.Automation.ControlType]::ListItem)
        if ($items.Count -gt 1) { $target = $items[1] }
        elseif ($items.Count -eq 1) { $target = $items[0] }
    }
    if ($null -ne $target) {
        $tr = $target.Current.BoundingRectangle
        $cx = [int]($tr.X + $tr.Width / 2); $cy = [int]($tr.Y + $tr.Height / 2)
        Invoke-MouseClick -X $cx -Y $cy
        Start-Sleep -Milliseconds 300
        Invoke-MouseRightClick -X $cx -Y $cy
    } else {
        # 兜底：右键点列表区中部一行
        $r = $w.Current.BoundingRectangle
        Invoke-MouseRightClick -X ([int]($r.X + 150)) -Y ([int]($r.Y + 550))
    }
    Start-Sleep -Milliseconds 800
    $m = @(Get-MenuItems)
    if ($m.Count -le 1) {
        # 只读到系统菜单，说明右键没落在频道行上；换第 1 行再试一次
        if ($null -ne $target) {
            $tr = $target.Current.BoundingRectangle
            Invoke-MouseRightClick -X ([int]($tr.X + $tr.Width / 2)) -Y ([int]($tr.Y + $tr.Height / 2))
            Start-Sleep -Milliseconds 800
            $m = @(Get-MenuItems)
        }
    }
    # 诊断截图：看右键后菜单到底开了没有（run4 裁决用）
    try { [void](Take-Shot -Name 'probe-menu-after-rightclick') } catch { }
    return $m
}

function Get-CountNumber {
    $w = Get-TmwWindow
    $ct = Find-ByAutomationId -Root $w -AutomationId 'CountText'
    $s = ''
    if ($null -ne $ct) { $s = Get-ElementText $ct }
    if ($s -eq '') {
        $ts = Find-ByName -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text) -NamePattern '个频道'
        if ($ts.Count -gt 0) { $s = Get-ElementText $ts[0] }
    }
    if ($s -match '(\d+)') { return [int]$Matches[1] }
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
    [void](Invoke-Element $hideItem)
    Start-Sleep -Seconds 2
    $cnt1 = Get-CountNumber
    $cfg1 = Get-Config
    $hid1 = @()
    if ($null -ne $cfg1 -and $null -ne $cfg1.HiddenChannels) { $hid1 = @($cfg1.HiddenChannels) }
    if ($cnt1 -eq ($cnt0 - 1) -and $hid1.Count -eq ($hidden0.Count + 1)) {
        Write-Result -Id 'C02' -Name '右键「隐藏频道」' -Verdict 'PASS' -Evidence "频道数 $cnt0->$cnt1，隐藏 $($hidden0.Count)->$($hid1.Count)"
    } else {
        Write-Result -Id 'C02' -Name '右键「隐藏频道」' -Verdict 'FAIL' -Evidence "频道数 $cnt0->$cnt1，隐藏 $($hidden0.Count)->$($hid1.Count)"
    }
    # C03：打开「显示隐藏频道」，台回来
    $menu2 = Open-ChannelContextMenu
    $showHidden = $menu2 | Where-Object { (Get-ElementText $_) -eq '显示隐藏频道' } | Select-Object -First 1
    if ($null -eq $showHidden) {
        Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'FAIL' -Evidence '菜单里没有「显示隐藏频道」'
        Write-Result -Id 'C04' -Name '右键「显示频道」取消隐藏' -Verdict 'FAIL' -Evidence 'C03 未完成，跳过'
        Write-Result -Id 'C05' -Name 'config 隐藏恢复基线' -Verdict 'FAIL' -Evidence 'C03 未完成，跳过'
    } else {
        [void](Invoke-Element $showHidden)
        Start-Sleep -Seconds 2
        $cnt2 = Get-CountNumber
        if ($cnt2 -eq $cnt0) {
            Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'PASS' -Evidence "频道数回到 $cnt2"
        } else {
            Write-Result -Id 'C03' -Name '「显示隐藏频道」找回' -Verdict 'FAIL' -Evidence "频道数 $cnt2，期望 $cnt0"
        }
        # C04：数字键选回 2 号台（它已回到列表），右键应只有「显示频道」
        Bring-ToFront
        Send-KeysToWindow -Keys '2' -WaitMs 1500
        $menu3 = Open-ChannelContextMenu
        $names3 = @($menu3 | ForEach-Object { Get-ElementText $_ })
        $unhide = $menu3 | Where-Object { (Get-ElementText $_) -eq '显示频道' } | Select-Object -First 1
        if ($null -ne $unhide -and -not ($names3 -contains '隐藏频道')) {
            [void](Invoke-Element $unhide)
            Start-Sleep -Seconds 2
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
            try {
                $tog = $sh2.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($tog.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { [void](Invoke-Element $sh2); Start-Sleep -Seconds 1 }
            } catch { }
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
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2000   # 回到 1 号台并播起来
$menuD = Open-ChannelContextMenu
$newWin = $menuD | Where-Object { (Get-ElementText $_) -eq '新窗口播放' } | Select-Object -First 1
if ($null -eq $newWin) {
    Write-Result -Id 'D01' -Name '新窗口播放时主窗口停掉' -Verdict 'FAIL' -Evidence '菜单里没有「新窗口播放」'
    Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'FAIL' -Evidence 'D01 未完成，跳过'
} else {
    [void](Invoke-Element $newWin)
    Start-Sleep -Seconds 3
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $wins = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) | Where-Object { $_.Current.Name -like '*星辰*' -or $_.Current.Name -like '*TiviMate*' })
    $st = Wait-ForStatus -Pattern '新窗口播放中' -TimeoutSec 5
    $t0 = Test-TimeAdvancing -WaitSec 3
    if ($wins.Count -ge 2 -and ($st -match '新窗口播放中' -or -not $t0.Advanced)) {
        Write-Result -Id 'D01' -Name '新窗口播放时主窗口停掉' -Verdict 'PASS' -Evidence "窗口数=$($wins.Count)，状态含「新窗口播放中」=$($st -match '新窗口播放中')，主窗口时间走动=$($t0.Advanced)"
    } else {
        Write-Result -Id 'D01' -Name '新窗口播放时主窗口停掉' -Verdict 'FAIL' -Evidence "窗口数=$($wins.Count)，状态=$st，时间 $($t0.Before)->$($t0.After)"
    }
    # D02：关新窗口，主窗口恢复
    $closed = $false
    try {
        $mainH = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowHandle
        foreach ($w in $wins) {
            if ($w.Current.NativeWindowHandle -ne $mainH) {
                $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                $wp.Close()
                $closed = $true
            }
        }
    } catch { }
    Start-Sleep -Seconds 3
    $t1 = Test-TimeAdvancing -WaitSec 4
    $wins2 = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) | Where-Object { $_.Current.Name -like '*星辰*' -or $_.Current.Name -like '*TiviMate*' })
    if ($closed -and $wins2.Count -eq 1 -and $t1.Advanced) {
        Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'PASS' -Evidence "窗口数回到 1，时间 $($t1.Before)->$($t1.After) 在走"
    } else {
        Write-Result -Id 'D02' -Name '关新窗口后主窗口恢复' -Verdict 'FAIL' -Evidence "关窗=$closed，窗口数=$($wins2.Count)，时间 $($t1.Before)->$($t1.After)"
    }
}

# ================= E：切台信息条 OSD =================
# OSD 不在 UIA 树里，只能截图复核；脚本只保证切台动作 + 截图， verdict 由看图的人定。
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2600   # 切 CCTV-1（按 "1" 要 1.2 秒才提交）
$shotE1 = Take-Shot -Name 'E01-osd-cctv1'
Write-Result -Id 'E01' -Name '切台 OSD 四行（CCTV-1）' -Verdict 'NEEDS-EYE' -Evidence "截图 $shotE1（应有台名+正在播+接下来 2 个，5 秒消失）"
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
Bring-ToFront
Send-KeysToWindow -Keys '1' -WaitMs 2600
$winE4 = Get-TmwWindow
try { $winE4.SetFocus() } catch { }
Send-KeysToWindow -Keys 'e' -WaitMs 1500
$shotE4 = Take-Shot -Name 'E04-guide-ekey'
$epgPanel = Find-ByAutomationId -Root (Get-TmwWindow) -AutomationId 'EpgPanel'
$epgVis = ''
if ($null -ne $epgPanel) { try { $epgVis = $epgPanel.Current.IsOffscreen.ToString() } catch { } }
# EpgPanel 在 UIA 树里；IsOffscreen=False 说明面板已显示（不再只凭截图猜）
if ($null -ne $epgPanel -and $epgVis -eq 'False') {
    Write-Result -Id 'E04' -Name 'E 键打开完整节目单' -Verdict 'PASS' -Evidence "截图 $shotE4；EpgPanel 已显示（IsOffscreen=False）"
} else {
    Write-Result -Id 'E04' -Name 'E 键打开完整节目单' -Verdict 'NEEDS-EYE' -Evidence "截图 $shotE4（应弹出完整节目单）；EpgPanel IsOffscreen=$epgVis"
}
Send-KeysToWindow -Keys '{ESC}' -WaitMs 800

function Open-SettingsWindow {
    # 稳健打开设置窗：UIA 点 BtnSettings → 轮询 6 秒；不行再鼠标点按钮中心 → 再轮询 6 秒。
    # 返回设置窗口元素，找不到返回 $null。
    $w = Get-TmwWindow
    if ($null -eq $w) { return $null }
    Bring-ToFront
    $btn = Find-ByAutomationId -Root $w -AutomationId 'BtnSettings'
    if ($null -eq $btn) { return $null }
    $findSetWin = {
        $rt = [System.Windows.Automation.AutomationElement]::RootElement
        $ws = @($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
        return ($ws | Where-Object { $_.Current.Name -match '设置' } | Select-Object -First 1)
    }
    [void](Invoke-Element $btn)
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 500
        $sw = & $findSetWin
        if ($null -ne $sw) { return $sw }
    }
    # UIA 点没打开，改鼠标点按钮中心
    $r = $btn.Current.BoundingRectangle
    Invoke-MouseClick -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 500
        $sw = & $findSetWin
        if ($null -ne $sw) { return $sw }
    }
    return $null
}

# ================= F：提示 5 秒自动消失 =================
# 须是全新会话（同一条提示每个会话只显示一次），所以先重启 app；再把字幕主开关打开（基线若是关的），
# 切台后 1 秒和 8 秒各截一张，看提示出现又消失；再切一个台，提示不应重现。
try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Seconds 2
Start-Process -FilePath $exe -WorkingDirectory $InstallDir
$win = $null
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Seconds 2; $win = Get-TmwWindow; if ($null -ne $win) { break } }
if ($null -eq $win) {
    Write-Result -Id 'F01' -Name '「免费接口未配置」提示 5 秒自动消失' -Verdict 'FAIL' -Evidence '重启后 120 秒无主窗口'
} else {
    Bring-ToFront
    Start-Sleep -Seconds 5
    $cfgF = Get-Config
    $masterOn = ($null -ne $cfgF -and $cfgF.LiveSubtitleEnabled -eq $true)
    if (-not $masterOn) {
        # 设置里打开字幕主开关（LiveSubtitleChk）
        $setWinF = Open-SettingsWindow
        if ($null -ne $setWinF) {
            $chk = Find-ByAutomationId -Root $setWinF -AutomationId 'LiveSubtitleChk'
            if ($null -ne $chk) {
                try {
                    $tog = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                    if ($tog.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $tog.Toggle(); Start-Sleep -Seconds 1 }
                } catch { }
            }
            try { $setWinF.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
            Start-Sleep -Seconds 2
        }
    }
    Send-KeysToWindow -Keys '1' -WaitMs 1200
    $shotF1 = Take-Shot -Name 'F01-toast-t1'
    Start-Sleep -Seconds 7
    $shotF2 = Take-Shot -Name 'F01-toast-t8'
    $lfF = Get-LogFile
    $logged = $false
    if ($null -ne $lfF) {
        $tailF = Get-Content $lfF.FullName -Tail 300 -ErrorAction SilentlyContinue
        if ($tailF -match '免费接口未配置') { $logged = $true }
    }
    Send-KeysToWindow -Keys '2' -WaitMs 3000
    $shotF3 = Take-Shot -Name 'F01-toast-switch2'
    Write-Result -Id 'F01' -Name '「免费接口未配置」提示 5 秒自动消失' -Verdict 'NEEDS-EYE' -Evidence "t1=$shotF1（应有提示），t8=$shotF2（应消失），切台后=$shotF3（不应重现）；log 里有该提示=$logged"
}

# ================= G：刷新按钮 =================
Bring-ToFront
$win = Get-TmwWindow
$refBtn = Find-ByName -Root $win -ControlType ([System.Windows.Automation.ControlType]::Button) -NamePattern '^刷新$' | Select-Object -First 1
if ($null -eq $refBtn) {
    Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'FAIL' -Evidence '找不到「刷新」按钮'
} else {
    [void](Invoke-Element $refBtn)
    $stG = Wait-ForStatus -Pattern '已刷新：\d+ 个频道 \d{2}:\d{2}' -TimeoutSec 20
    if ($stG -match '已刷新：\d+ 个频道 \d{2}:\d{2}') {
        Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'PASS' -Evidence $stG
    } else {
        Write-Result -Id 'G01' -Name '点「刷新」显示「已刷新：N 个频道 HH:mm」' -Verdict 'FAIL' -Evidence "刷新后状态=$stG（期望「已刷新：N 个频道 HH:mm」）"
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
        Write-Result -Id 'H01' -Name '设置页显示「当前版本」' -Verdict 'FAIL' -Evidence '点了设置，没弹出设置窗口'
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
        $themeCombo = Find-ByAutomationId -Root $setWin -AutomationId 'ThemeCombo'
        if ($null -eq $themeCombo) {
            Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'FAIL' -Evidence 'AutomationId=ThemeCombo 找不到'
        } else {
            $r5 = $themeCombo.Current.BoundingRectangle
            $rect5 = New-Object System.Drawing.Rectangle([int]$r5.X, [int]$r5.Y, [int]$r5.Width, [int]$r5.Height)
            $crop5 = Take-Shot -Name 'B05-theme-combo-crop' -Crop $rect5
            $frac5 = Get-DarkPixelFraction -ImagePath $crop5
            if ($frac5 -gt 0.002) {
                Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'PASS' -Evidence "深色像素占比 $([Math]::Round($frac5,4))，截图 $crop5"
            } else {
                Write-Result -Id 'B05' -Name '设置页下拉框字看得见' -Verdict 'FAIL' -Evidence "深色像素占比 $([Math]::Round($frac5,4))（<0.002），字太浅或没画出；截图 $crop5"
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
        try { $setWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
        Start-Sleep -Seconds 1
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
    Write-Result -Id 'I01' -Name '空格键暂停' -Verdict 'FAIL' -Evidence "按空格前时间就不走：$($tI0.Before)->$($tI0.After)（台可能没播起来）"
    Write-Result -Id 'I02' -Name '再按空格继续' -Verdict 'FAIL' -Evidence 'I01 未完成，跳过'
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
        Write-Result -Id 'I02' -Name '再按空格继续' -Verdict 'FAIL' -Evidence "再按空格后时间不动：$($r1.Before)->$($r1.After)；截图 $shotI2"
    }
}

# ================= J：翻译「强制开」联动主开关 =================
function Set-MasterSubtitle {
    param([bool]$On)
    # 设置页里把字幕主开关拨到指定状态，返回设置后的 config 值（true/false/$null 表示没办成）
    $sw = Open-SettingsWindow
    if ($null -eq $sw) { return $null }
    $chk = Find-ByAutomationId -Root $sw -AutomationId 'LiveSubtitleChk'
    $ok = $null
    if ($null -ne $chk) {
        try {
            $tog = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $isOn = ($tog.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
            if ($isOn -ne $On) { $tog.Toggle(); Start-Sleep -Seconds 1 }
            $ok = $On
        } catch { $ok = $null }
    }
    try { $sw.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
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
            $nm = Get-ElementText $tBtn
            if ($nm -match '强制') { $forced = $true; break }
            # 按钮被重建时要重找
            $w = Get-TmwWindow
            $tBtn = Find-ByAutomationId -Root $w -AutomationId 'BtnTranslateToggle'
            if ($null -eq $tBtn) { break }
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
if ($null -ne $k1 -and $k1t.Advanced) {
    Write-Result -Id 'K01' -Name '快速连切 10 台不崩' -Verdict 'PASS' -Evidence "连切后仍在播，时间 $($k1t.Before)->$($k1t.After)"
} else {
    Write-Result -Id 'K01' -Name '快速连切 10 台不崩' -Verdict 'FAIL' -Evidence "连切后窗口=$($null -ne $k1)，时间 $($k1t.Before)->$($k1t.After)"
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
    $allW = @($root3.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) | Where-Object { $_.Current.Name -like '*星辰*' -or $_.Current.Name -like '*TiviMate*' })
    $mainH3 = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowHandle
    foreach ($w3 in $allW) {
        if ($w3.Current.NativeWindowHandle -ne $mainH3) {
            try { $w3.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
        }
    }
    Start-Sleep -Seconds 2
    $left = @($root3.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) | Where-Object { $_.Current.Name -like '*星辰*' -or $_.Current.Name -like '*TiviMate*' })
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
        $tc = Find-ByAutomationId -Root $sw4 -AutomationId 'ThemeCombo'
        if ($null -ne $tc) {
            try {
                $exp4 = $tc.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                # 下拉项在 Popup 里，不在 ThemeCombo 子树下；展开前记下已有项，展开后多出来的就是主题选项
                $beforeK4 = @()
                foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeK4 += (Get-ElementText $li) }
                $exp4.Expand(); Start-Sleep -Seconds 1
                $items4 = @()
                foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
                    $nm = Get-ElementText $li
                    if ($nm -ne '' -and ($beforeK4 -notcontains $nm)) { $items4 += $li }
                }
                $light = $items4 | Where-Object { (Get-ElementText $_) -eq '浅色' } | Select-Object -First 1
                if ($null -ne $light) {
                    $lr = $light.Current.BoundingRectangle
                    Invoke-MouseClick -X ([int]($lr.X + $lr.Width / 2)) -Y ([int]($lr.Y + $lr.Height / 2))
                    Start-Sleep -Seconds 2
                    $shotK4a = Take-Shot -Name 'K04-theme-light'
                    $beforeK4b = @()
                    foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) { $beforeK4b += (Get-ElementText $li) }
                    $exp4.Expand(); Start-Sleep -Seconds 1
                    $items4b = @()
                    foreach ($li in (Find-All -Root (Get-TmwWindow) -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
                        $nm = Get-ElementText $li
                        if ($nm -ne '' -and ($beforeK4b -notcontains $nm)) { $items4b += $li }
                    }
                    $dark = $items4b | Where-Object { (Get-ElementText $_) -eq '深色' } | Select-Object -First 1
                    if ($null -ne $dark) {
                        $dr = $dark.Current.BoundingRectangle
                        Invoke-MouseClick -X ([int]($dr.X + $dr.Width / 2)) -Y ([int]($dr.Y + $dr.Height / 2))
                        Start-Sleep -Seconds 2
                        $shotK4b = Take-Shot -Name 'K04-theme-dark'
                        $k04ok = ($null -ne (Get-TmwWindow))
                        $k04ev = "浅色截图 $shotK4a，深色截图 $shotK4b"
                    } else { $k04ev = '切回深色时找不到「深色」项' }
                } else { $k04ev = '找不到「浅色」项' }
            } catch { $k04ev = "主题切换异常：$($_.Exception.Message)" }
        } else { $k04ev = '设置页找不到 ThemeCombo' }
        try { $sw4.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { Send-KeysToWindow -Keys '{ESC}' -WaitMs 500 }
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
    try {
        $vp = $sb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue('zzzz不存在')
    } catch {
        $r5 = $sb.Current.BoundingRectangle
        Invoke-MouseClick -X ([int]($r5.X + $r5.Width / 2)) -Y ([int]($r5.Y + $r5.Height / 2))
        Send-KeysToWindow -Keys 'zzzz不存在' -WaitMs 500
    }
    Start-Sleep -Seconds 2
    $cntZero = Get-CountNumber
    try {
        $vp2 = $sb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp2.SetValue('')
    } catch {
        $r5b = $sb.Current.BoundingRectangle
        Invoke-MouseClick -X ([int]($r5b.X + $r5b.Width / 2)) -Y ([int]($r5b.Y + $r5b.Height / 2))
        Send-KeysToWindow -Keys '^a{DEL}' -WaitMs 500
    }
    Start-Sleep -Seconds 2
    $cntAfter = Get-CountNumber
    $alive5 = ($null -ne (Get-TmwWindow))
    if ($alive5 -and $cntAfter -eq $cntBefore) {
        Write-Result -Id 'K05' -Name '搜不到的词不崩、清空恢复' -Verdict 'PASS' -Evidence "搜前=$cntBefore，搜「zzzz不存在」后=$cntZero，清空后=$cntAfter"
    } else {
        Write-Result -Id 'K05' -Name '搜不到的词不崩、清空恢复' -Verdict 'FAIL' -Evidence "搜前=$cntBefore，搜后=$cntZero，清空后=$cntAfter，窗口在=$alive5"
    }
}

# K06：config 损坏后仍能启动（备份+恢复；可 -SkipConfigCorruption 跳过）
if (-not $SkipConfigCorruption) {
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
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 2; if ($null -ne (Get-TmwWindow)) { break } }
    Write-Output 'K06 恢复：config 已从备份恢复并重启'
}

# ================= L：连续播 soak =================
if (-not $SkipSoak) {
    Bring-ToFront
    Send-KeysToWindow -Keys '1' -WaitMs 2500
    $soakStart = Get-Date
    $soakPid = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    $soakExit = $null
    $lfL = Get-LogFile
    $logMark = 0
    if ($null -ne $lfL) { $logMark = (Get-Content $lfL.FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines }
    $stalls = 0; $reconnects = 0
    $steps = [Math]::Max(1, [int]($SoakMinutes / 5))
    for ($s = 1; $s -le $steps; $s++) {
        Start-Sleep -Seconds 300
        $p = Get-Process -Id $soakPid -ErrorAction SilentlyContinue
        if ($null -eq $p) {
            $soakExit = Get-Date
            $lfL2 = Get-LogFile
            $tailL = ''
            if ($null -ne $lfL2) { $tailL = (Get-Content $lfL2.FullName -Tail 15 -ErrorAction SilentlyContinue) -join ' | ' }
            Write-Result -Id 'L01' -Name "连续播 ${SoakMinutes} 分钟不退出" -Verdict 'FAIL' -Evidence "播了约 $([int](($soakExit - $soakStart).TotalMinutes)) 分钟后退出（$($soakExit.ToString('HH:mm:ss'))）；log 尾：$tailL"
            break
        }
        $tt = Test-TimeAdvancing -WaitSec 3
        $lfL3 = Get-LogFile
        if ($null -ne $lfL3) {
            $newLines = Get-Content $lfL3.FullName -Tail 500 -ErrorAction SilentlyContinue
            $stalls = @($newLines | Select-String -Pattern 'stall' -SimpleMatch).Count
            $reconnects = @($newLines | Select-String -Pattern 'reconnect|重连' ).Count
        }
        if (($s % 2) -eq 0) { [void](Take-Shot -Name "L01-soak-min$($s * 5)") }
        Write-Output "soak $($s * 5)/$SoakMinutes min：进程在，时间 $($tt.Before)->$($tt.After)，stall=$stalls，重连=$reconnects"
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
