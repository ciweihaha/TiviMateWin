$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-out6.txt'
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
try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Seconds 3
Start-Process -FilePath 'C:\TiviMateWin\TiviMateWin.exe' -WorkingDirectory 'C:\TiviMateWin'
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$mw=$null; $pid0=$null
for($i=0;$i -lt 30 -and $null -eq $mw;$i++){
  Start-Sleep -Seconds 2
  $p=(Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1)
  if($null -ne $p){
    $pid0=$p.Id
    $ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
    $mw=$ws | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } } | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
  }
}
if($null -eq $mw){ L "MAIN NOT FOUND"; L "PROBE-DONE"; exit }
L ("main rect=" + $mw.Current.BoundingRectangle + " pid=" + $pid0)
Start-Sleep -Seconds 8
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
  $pt=New-Object W32+POINT
  [void][W32]::GetCursorPos([ref]$pt)
  [W32]::mouse_event(0x0002, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [W32]::mouse_event(0x0004, $ax, $ay, 0, [IntPtr]::Zero)
  return "want=($cx,$cy) cursor=($($pt.X),$($pt.Y))"
}
function Count-PidWindows(){
  $ws2=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
  $mine=@($ws2 | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } })
  return $mine
}
# 1. PlayPause toggle (observable)
$pp=Get-El 'BtnPlayPause'
$ps=Get-El 'PlayState'
if($null -ne $pp -and $null -ne $ps){
  $before=$ps.Current.Name
  $c1=Click-Center $pp
  Start-Sleep -Seconds 2
  $after=(Get-El 'PlayState').Current.Name
  L ("PlayPause: beforeLen=" + $before.Length + " afterLen=" + $after.Length + " changed=" + ($before -ne $after) + " click=" + $c1)
  if($before -ne $after){ Click-Center (Get-El 'BtnPlayPause') | Out-Null; Start-Sleep -Seconds 1; L "toggled back to play" }
} else { L "PlayPause or PlayState NOT FOUND" }
# 2. Settings via mouse（run8 教训：设置窗可迟开约 15 秒，3 秒轮询会漏判；改轮询 20 秒并记迟开时间）
# 另：启动后工具栏可能还没建出来，BtnSettings 先轮询等 30 秒再判 NOT FOUND。
$btn=$null
for($k=0;$k -lt 15 -and $null -eq $btn;$k++){ $btn=Get-El 'BtnSettings'; if($null -eq $btn){ Start-Sleep -Seconds 2 } }
if($null -eq $btn){ L "BtnSettings NOT FOUND after 30s wait"; L "PROBE-DONE"; exit }
  $r=$btn.Current.BoundingRectangle
  L ("BtnSettings rect=" + $r + " enabled=" + $btn.Current.IsEnabled)
  $c2=Click-Center $btn
  $t0=Get-Date
  $mine=@(); $openedAt=$null
  for($k=0;$k -lt 10;$k++){
    Start-Sleep -Seconds 2
    $mine=Count-PidWindows
    if($mine.Count -gt 1){ $openedAt=[int]((Get-Date)-$t0).TotalSeconds; break }
  }
  L ("after MOUSE settings: pidWindows=" + $mine.Count + " openedAtSec=" + $openedAt + " click=" + $c2)
  foreach($w in $mine){ try { L ("  win handle=" + $w.Current.NativeWindowHandle + " mainHandle=" + $mw.Current.NativeWindowHandle + " rect=" + $w.Current.BoundingRectangle) } catch {} }
  if($mine.Count -le 1){
    try {
      $ip=($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) -as [System.Windows.Automation.InvokePattern])
      if($null -ne $ip){ $ip.Invoke(); L "UIA Invoke called" } else { L "InvokePattern NOT supported" }
    } catch { L ("Invoke FAILED: " + $_.Exception.Message) }
    $t1=Get-Date
    $mine2=@(); $openedAt2=$null
    for($k=0;$k -lt 10;$k++){
      Start-Sleep -Seconds 2
      $mine2=Count-PidWindows
      if($mine2.Count -gt 1){ $openedAt2=[int]((Get-Date)-$t1).TotalSeconds; break }
    }
    L ("after UIA Invoke settings: pidWindows=" + $mine2.Count + " openedAtSec=" + $openedAt2)
    $mine=$mine2
  }
  # 探针收尾：若设置窗开了，把它关掉（Cancel/ESC），免得残留模态窗挡住后继探针
  if($mine.Count -gt 1){
    $sw2=$mine | Where-Object { try { $_.Current.NativeWindowHandle -ne $mw.Current.NativeWindowHandle } catch { $false } } | Select-Object -First 1
    if($null -ne $sw2){
      try { $wp=$sw2.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.Close(); L "settings window closed via WindowPattern" } catch { L ("close via WindowPattern FAILED: " + $_.Exception.Message) }
      Start-Sleep -Seconds 2
      $mine3=Count-PidWindows
      L ("after close attempt: pidWindows=" + $mine3.Count)
    }
  }
L "PROBE-DONE"
