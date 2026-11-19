$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-out5.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
}
'@
[void][W32]::SetProcessDPIAware()
$sw=[W32]::GetSystemMetrics(0); $sh=[W32]::GetSystemMetrics(1)
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$pid0=(Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
if($null -eq $pid0){ L "APP NOT RUNNING"; L "PROBE-DONE"; exit }
$ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
$mw=$ws | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } } | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
if($null -eq $mw){ L "MAIN NOT FOUND"; L "PROBE-DONE"; exit }
[void][W32]::SetForegroundWindow([IntPtr]$mw.Current.NativeWindowHandle)
Start-Sleep -Seconds 1
function Get-El($aid){ $mw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid))) }
function Click-Center($el){
  $r=$el.Current.BoundingRectangle
  $cx=[int]($r.X + $r.Width/2); $cy=[int]($r.Y + $r.Height/2)
  $ax=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cx * 65535.0 / $sw))
  $ay=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cy * 65535.0 / $sh))
  [W32]::mouse_event(0x8001, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  [W32]::mouse_event(0x0002, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [W32]::mouse_event(0x0004, $ax, $ay, 0, [IntPtr]::Zero)
  return "$cx,$cy"
}
# 1. BtnPlayPause toggle test
$pp=Get-El 'BtnPlayPause'
$ps=Get-El 'PlayState'
if($null -ne $pp -and $null -ne $ps){
  $before=$ps.Current.Name
  $pt=Click-Center $pp
  Start-Sleep -Seconds 2
  $ps2=Get-El 'PlayState'
  $after=$ps2.Current.Name
  L ("PlayPause click at $pt : PlayState before=[$before] after=[$after] toggle=" + ($before -ne $after))
  $pp2=Get-El 'BtnPlayPause'
  L ("BtnPlayPause content after=" + $pp2.Current.Name)
  if($before -ne $after){ Click-Center (Get-El 'BtnPlayPause') | Out-Null; Start-Sleep -Seconds 1; L "resumed after toggle-back" }
} else { L "BtnPlayPause or PlayState NOT FOUND" }
# 2. BtnSettings click, enumerate ALL pid windows
$btn=Get-El 'BtnSettings'
if($null -ne $btn){
  $pt=Click-Center $btn
  Start-Sleep -Seconds 3
  $ws2=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
  $mine=$ws2 | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } }
  L ("BtnSettings click at $pt : pid windows count=" + @($mine).Count)
  foreach($w in $mine){ try { L ("  win name=" + $w.Current.Name + " rect=" + $w.Current.BoundingRectangle + " handle=" + $w.Current.NativeWindowHandle) } catch {} }
} else { L "BtnSettings NOT FOUND" }
L "PROBE-DONE"
