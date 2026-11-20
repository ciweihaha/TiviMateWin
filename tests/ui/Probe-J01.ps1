Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class W32 {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[void][W32]::SetProcessDPIAware()
$out='C:\build\tmw-uitest\probe-j01.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
$cfgPath = Join-Path $env:APPDATA 'TiviMateWin\config.json'
function Get-CfgVal { try { (Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json).LiveSubtitleEnabled } catch { 'ERR' } }
L ('CFG-BEFORE=' + (Get-CfgVal))
# find main window + BtnSettings, click it
$proc = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
L ('APP=' + $proc.Id + ' Responding=' + $proc.Responding)
$win = $null
try { $win = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle) } catch { L 'MAIN-FROMHANDLE-FAIL'; exit 0 }
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')
$btn = $null
try { $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) } catch { }
if ($null -eq $btn) { L 'BTN-NOT-FOUND'; exit 0 }
L 'BTN-FOUND'
try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); L 'BTN-INVOKE-OK' } catch { L ('BTN-INVOKE-FAIL: ' + $_.Exception.Message) }
# find settings window via Win32 EnumWindows + FromHandle (same as run9 Get-SettingsWindowElement)
$settingsTitle = [string]([char]0x8BBE) + [string]([char]0x7F6E)
$pid0 = $proc.Id
$hwnds = @()
$cb = [W32+EnumProc]{
  param($hWnd, $lParam)
  $wpid = 0; [void][W32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
  if ($wpid -eq $pid0) {
    $sb = New-Object System.Text.StringBuilder(256)
    [void][W32]::GetWindowText($hWnd, $sb, 256)
    if ($sb.ToString() -eq $settingsTitle -and [W32]::IsWindowVisible($hWnd)) { $script:hwnds += $hWnd }
  }
  return $true
}
$setEl = $null
for ($i=0; $i -lt 60; $i++) {
  Start-Sleep -Milliseconds 500
  $hwnds = @()
  [void][W32]::EnumWindows($cb, [IntPtr]::Zero)
  if ($hwnds.Count -gt 0) {
    try { $setEl = [System.Windows.Automation.AutomationElement]::FromHandle($hwnds[0]); if ($null -ne $setEl) { break } } catch { }
  }
}
if ($null -eq $setEl) { L 'SETWIN-NOT-FOUND-30s'; exit 0 }
L ('SETWIN-FOUND name=[' + $setEl.Current.Name + '] rect=' + $setEl.Current.BoundingRectangle)
# ThemeCombo (control: should be findable, B05 passed)
$condTC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeCombo')
$tc = $null
try { $tc = $setEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condTC) } catch { }
L ('THEME-COMBO=' + ($null -ne $tc))
# LiveSubtitleChk: poll 10s
$condLS = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'LiveSubtitleChk')
$chk = $null
for ($i=0; $i -lt 20; $i++) {
  try { $chk = $setEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condLS) } catch { }
  if ($null -ne $chk) { break }
  Start-Sleep -Milliseconds 500
}
L ('LIVESUB-CHK=' + ($null -ne $chk))
if ($null -ne $chk) {
  try {
    $tog = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    L ('TOGGLE-STATE-BEFORE=' + $tog.ToggleState)
    $tog.Toggle()
    Start-Sleep -Seconds 1
    $tog2 = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    L ('TOGGLE-STATE-AFTER=' + $tog2.ToggleState)
  } catch { L ('TOGGLE-FAIL: ' + $_.Exception.Message) }
}
# count all descendants (is the dialog UIA tree complete?)
try { $all = $setEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition); L ('DESCENDANTS=' + $all.Count) } catch { L 'DESCENDANTS-FAIL' }
# save via the Save button
$condSave = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Save')
# name is Chinese; use AutomationId-free search by control type + name pattern via enumeration
$saved = $false
try {
  $btns = $setEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
  foreach ($b in $btns) {
    try { if ($b.Current.Name -eq ([string]([char]0x4FDD) + [string]([char]0x5B58))) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $saved = $true; L 'SAVE-CLICKED'; break } } catch { }
  }
} catch { L 'SAVE-SEARCH-FAIL' }
L ('SAVE-CLICKED=' + $saved)
Start-Sleep -Seconds 2
L ('CFG-AFTER=' + (Get-CfgVal))
L 'PROBE-J01-DONE'
