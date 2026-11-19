$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-out.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
}
'@
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
L ("Bounds BEFORE: " + $b.Width + "x" + $b.Height)
$rr=[System.Windows.Automation.AutomationElement]::RootElement.Current.BoundingRectangle
L ("RootElement BEFORE: " + [int]$rr.Width + "x" + [int]$rr.Height)
L ("GetSystemMetrics BEFORE: " + [W32]::GetSystemMetrics(0) + "x" + [W32]::GetSystemMetrics(1))
[void][W32]::SetProcessDPIAware()
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
L ("Bounds AFTER: " + $b.Width + "x" + $b.Height)
L ("GetSystemMetrics AFTER: " + [W32]::GetSystemMetrics(0) + "x" + [W32]::GetSystemMetrics(1))
$rr=[System.Windows.Automation.AutomationElement]::RootElement.Current.BoundingRectangle
L ("RootElement AFTER: " + [int]$rr.Width + "x" + [int]$rr.Height)
$procs=@(Get-Process TiviMateWin -ErrorAction SilentlyContinue)
L ("TiviMateWin procs: " + $procs.Count + " ids=" + (($procs | ForEach-Object { $_.Id }) -join ','))
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$wins=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
L ("top-level windows: " + $wins.Count)
foreach($w in $wins){ try { L ("  win name=" + $w.Current.Name + " pid=" + $w.Current.ProcessId + " rect=" + $w.Current.BoundingRectangle) } catch { L "  win (unreadable)" } }
$pid0=($procs | Select-Object -First 1).Id
$mw=$wins | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } } | Select-Object -First 1
if($null -eq $mw){ L "MAIN-WINDOW-NOT-FOUND"; L "PROBE-DONE"; exit }
L ("main rect=" + $mw.Current.BoundingRectangle)
$btn=$mw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')))
if($null -eq $btn){ L "BtnSettings NOT FOUND"; L "PROBE-DONE"; exit }
$r=$btn.Current.BoundingRectangle
L ("BtnSettings rect=" + $r + " name=" + $btn.Current.Name)
try { $pat=$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $pat.Invoke(); L "UIA Invoke called" } catch { L ("UIA Invoke FAILED: " + $_.Exception.Message) }
Start-Sleep -Seconds 3
$wins2=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
$sw=$wins2 | Where-Object { $_.Current.Name -match '设置' } | Select-Object -First 1
if($null -ne $sw){
  L ("settings OPEN name=" + $sw.Current.Name + " rect=" + $sw.Current.BoundingRectangle)
  $all=$sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $ids=@()
  foreach($e in $all){ try { $a=$e.Current.AutomationId; if($a -ne ''){ $ids+=$a } } catch {} }
  L ("settings ids (" + $ids.Count + "): " + ($ids -join ','))
  $tc=$sw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeCombo')))
  L ("ThemeCombo found=" + ($null -ne $tc))
  if($null -ne $tc){ L ("ThemeCombo rect=" + $tc.Current.BoundingRectangle) }
  $sv=$sw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '保存')))
  L ("Save button found=" + ($null -ne $sv))
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Seconds 1
} else {
  L "settings DID NOT OPEN via UIA Invoke"
  $sz=@([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
  $cx=[int]($r.X + $r.Width/2); $cy=[int]($r.Y + $r.Height/2)
  $ax=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cx * 65535.0 / $sz[0]))
  $ay=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cy * 65535.0 / $sz[1]))
  L ("mouse click physical ($cx,$cy) abs=($ax,$ay) screensize=" + $sz[0] + "x" + $sz[1])
  [W32]::mouse_event(0x8001, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 120
  [W32]::mouse_event(0x0002, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W32]::mouse_event(0x0004, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Seconds 3
  $wins3=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
  $sw2=$wins3 | Where-Object { $_.Current.Name -match '设置' } | Select-Object -First 1
  L ("settings after mouse click=" + ($null -ne $sw2))
  if($null -ne $sw2){ [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 1 }
}
L "PROBE-DONE"
