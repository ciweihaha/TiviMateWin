$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-out3.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
}
'@
[void][W32]::SetProcessDPIAware()
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$pid0=(Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
$ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
$mw=$ws | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } } | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
if($null -eq $mw){ L "MAIN NOT FOUND"; L "PROBE-DONE"; exit }
$fg=[W32]::GetForegroundWindow()
L ("foreground hwnd=" + $fg + " main hwnd=" + $mw.Current.NativeWindowHandle + " fgIsMain=" + ($fg -eq $mw.Current.NativeWindowHandle))
$btn=$mw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')))
if($null -eq $btn){ L "BtnSettings NOT FOUND"; L "PROBE-DONE"; exit }
$r=$btn.Current.BoundingRectangle
L ("BtnSettings rect=" + $r + " IsEnabled=" + $btn.Current.IsEnabled + " IsOffscreen=" + $btn.Current.IsOffscreen + " HasKeyboardFocus=" + $btn.Current.HasKeyboardFocus)
$cx=[int]($r.X + $r.Width/2); $cy=[int]($r.Y + $r.Height/2)
$pt=New-Object System.Windows.Point($cx,$cy)
$el=[System.Windows.Automation.AutomationElement]::FromPoint($pt)
if($null -eq $el){ L "FromPoint($cx,$cy)=NULL" } else {
  L ("FromPoint aid=" + $el.Current.AutomationId + " name=" + $el.Current.Name + " type=" + $el.Current.ControlType.ProgrammaticName + " pid=" + $el.Current.ProcessId)
  $p=$el; $chain=@()
  for($i=0;$i -lt 5 -and $null -ne $p;$i++){ try { $chain+=($p.Current.ControlType.ProgrammaticName + ":" + $p.Current.AutomationId + ":" + $p.Current.Name) } catch { $chain+=("(?)") }; try { $p=[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($p) } catch { $p=$null } }
  L ("ancestor chain: " + ($chain -join ' < '))
}
try { $btn.SetFocus(); L "SetFocus OK" } catch { L ("SetFocus FAILED: " + $_.Exception.Message) }
Start-Sleep -Seconds 1
L ("after SetFocus HasKeyboardFocus=" + $btn.Current.HasKeyboardFocus)
[System.Windows.Forms.SendKeys]::SendWait(' ')
Start-Sleep -Seconds 3
$ws2=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
$swin=$ws2 | Where-Object { try { $_.Current.ProcessId -eq $pid0 -and $_.Current.NativeWindowHandle -ne $mw.Current.NativeWindowHandle } catch { $false } } | Select-Object -First 1
L ("settings after SPACE on focused button=" + ($null -ne $swin))
if($null -ne $swin){ L ("opened window name=" + $swin.Current.Name + " rect=" + $swin.Current.BoundingRectangle); [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 1 }
L "PROBE-DONE"
