$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-out2.txt'
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
L ("physical screen: " + $sw + "x" + $sh)
$rt=[System.Windows.Automation.AutomationElement]::RootElement
function Find-Win($namePat){
  $ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
  return ($ws | Where-Object { $_.Current.Name -match $namePat } | Select-Object -First 1)
}
$mw=Find-Win '星辰'
if($null -eq $mw){ L "MAIN NOT FOUND"; L "PROBE-DONE"; exit }
L ("main rect=" + $mw.Current.BoundingRectangle)
[void][W32]::SetForegroundWindow([IntPtr]$mw.Current.NativeWindowHandle)
Start-Sleep -Seconds 1
$btn=$mw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')))
$r=$btn.Current.BoundingRectangle
$cx=[int]($r.X + $r.Width/2); $cy=[int]($r.Y + $r.Height/2)
$ax=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cx * 65535.0 / $sw))
$ay=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cy * 65535.0 / $sh))
L ("mouse click BtnSettings physical ($cx,$cy) abs=($ax,$ay)")
[W32]::mouse_event(0x8001, $ax, $ay, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 120
[W32]::mouse_event(0x0002, $ax, $ay, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[W32]::mouse_event(0x0004, $ax, $ay, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 3
$swin=Find-Win '设置'
L ("settings after MOUSE click=" + ($null -ne $swin))
if($null -ne $swin){
  L ("settings rect=" + $swin.Current.BoundingRectangle)
  $all=$swin.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $ids=@()
  foreach($e in $all){ try { $a=$e.Current.AutomationId; if($a -ne ''){ $ids+=$a } } catch {} }
  L ("settings ids (" + $ids.Count + "): " + ($ids -join ','))
  $tc=$swin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeCombo')))
  L ("ThemeCombo found=" + ($null -ne $tc))
  if($null -ne $tc){ L ("ThemeCombo rect=" + $tc.Current.BoundingRectangle + " name=" + $tc.Current.Name) }
  $sv=$swin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '保存')))
  L ("Save button found=" + ($null -ne $sv))
  if($null -ne $sv){ L ("Save rect=" + $sv.Current.BoundingRectangle) }
  $chk=$swin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'LiveSubtitleChk')))
  L ("LiveSubtitleChk found=" + ($null -ne $chk))
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Seconds 1
  L ("settings closed after ESC=" + ($null -eq (Find-Win '设置')))
} else {
  L "mouse click ALSO failed; try UIA Invoke with foreground"
  try { $pat=$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $pat.Invoke(); L "UIA Invoke called" } catch { L ("UIA Invoke FAILED: " + $_.Exception.Message) }
  Start-Sleep -Seconds 3
  $swin2=Find-Win '设置'
  L ("settings after UIA Invoke (fg)=" + ($null -ne $swin2))
  if($null -ne $swin2){ [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 1 }
}
L "PROBE-DONE"
