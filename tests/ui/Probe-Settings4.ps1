$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-out4.txt'
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
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
  public struct POINT { public int X; public int Y; }
}
'@
[void][W32]::SetProcessDPIAware()
$sw=[W32]::GetSystemMetrics(0); $sh=[W32]::GetSystemMetrics(1)
L ("physical screen: " + $sw + "x" + $sh)
try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Seconds 3
Start-Process -FilePath 'C:\TiviMateWin\TiviMateWin.exe' -WorkingDirectory 'C:\TiviMateWin'
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$mw=$null
for($i=0;$i -lt 30 -and $null -eq $mw;$i++){
  Start-Sleep -Seconds 2
  $p=(Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1)
  if($null -ne $p){
    $ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
    $mw=$ws | Where-Object { try { $_.Current.ProcessId -eq $p.Id } catch { $false } } | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
  }
}
if($null -eq $mw){ L "MAIN NOT FOUND after fresh start"; L "PROBE-DONE"; exit }
$pid0=(Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
L ("fresh main rect=" + $mw.Current.BoundingRectangle + " pid=" + $pid0)
Start-Sleep -Seconds 5
[void][W32]::SetForegroundWindow([IntPtr]$mw.Current.NativeWindowHandle)
Start-Sleep -Seconds 1
$btn=$mw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')))
if($null -eq $btn){ L "BtnSettings NOT FOUND"; L "PROBE-DONE"; exit }
$r=$btn.Current.BoundingRectangle
L ("BtnSettings rect=" + $r + " IsEnabled=" + $btn.Current.IsEnabled)
$cx=[int]($r.X + $r.Width/2); $cy=[int]($r.Y + $r.Height/2)
$ax=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cx * 65535.0 / $sw))
$ay=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cy * 65535.0 / $sh))
[W32]::mouse_event(0x8001, $ax, $ay, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 200
$pt=New-Object W32+POINT
[void][W32]::GetCursorPos([ref]$pt)
L ("cursor after move=(" + $pt.X + "," + $pt.Y + ") want=(" + $cx + "," + $cy + ")")
[W32]::mouse_event(0x0002, $ax, $ay, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 100
[W32]::mouse_event(0x0004, $ax, $ay, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 3
$ws2=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
$swin=$ws2 | Where-Object { try { $_.Current.ProcessId -eq $pid0 -and $_.Current.NativeWindowHandle -ne $mw.Current.NativeWindowHandle } catch { $false } } | Select-Object -First 1
L ("settings after fresh MOUSE click=" + ($null -ne $swin))
if($null -ne $swin){
  L ("opened name=" + $swin.Current.Name + " rect=" + $swin.Current.BoundingRectangle)
  $all=$swin.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $ids=@()
  foreach($e in $all){ try { $a=$e.Current.AutomationId; if($a -ne ''){ $ids+=$a } } catch {} }
  L ("settings ids (" + $ids.Count + "): " + ($ids -join ','))
  $tc=$swin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeCombo')))
  L ("ThemeCombo found=" + ($null -ne $tc))
  $sv=$swin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '保存')))
  L ("Save button found=" + ($null -ne $sv))
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Seconds 1
} else {
  L "fresh click ALSO failed; windows now:"
  foreach($w in $ws2){ try { L ("  win name=" + $w.Current.Name + " pid=" + $w.Current.ProcessId) } catch {} }
}
L "PROBE-DONE"
