$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win32D {
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  public struct POINT { public int X; public int Y; }
}
'@
function Get-PhysSize {
    $rr = [System.Windows.Automation.AutomationElement]::RootElement.Current.BoundingRectangle
    return @([int]$rr.Width, [int]$rr.Height)
}
$root=[System.Windows.Automation.AutomationElement]::RootElement
$rr=$root.Current.BoundingRectangle
Write-Output ("RootElement rect={0}" -f $rr)
Write-Output ("WinForms Bounds={0}x{1}" -f [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
$p=Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
if($null -eq $p -or $p.MainWindowHandle -eq 0){ Write-Output 'TiviMateWin not running'; exit 1 }
$win=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
Write-Output ("Window name={0} rect={1}" -f $win.Current.Name, $win.Current.BoundingRectangle)
[Win32D]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 500
$btn=$win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')))
if($null -eq $btn){ Write-Output 'BtnSettings NOT FOUND'; exit 1 }
$br=$btn.Current.BoundingRectangle
Write-Output ("BtnSettings rect={0} name='{1}'" -f $br, $btn.Current.Name)
$cx=[int]($br.X + $br.Width/2); $cy=[int]($br.Y + $br.Height/2)
Write-Output ("click target center=({0},{1})" -f $cx,$cy)
$sz=Get-PhysSize; $sw=$sz[0]; $sh=$sz[1]
Write-Output ("phys size used={0}x{1}" -f $sw,$sh)
$ax=[uint][Math]::Min(65535.0,[Math]::Max(0.0,$cx*65535.0/$sw)); $ay=[uint][Math]::Min(65535.0,[Math]::Max(0.0,$cy*65535.0/$sh))
Write-Output ("mouse abs=({0},{1})" -f $ax,$ay)
[Win32D]::mouse_event(0x8001,$ax,$ay,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 300
$pt=New-Object Win32D+POINT
[Win32D]::GetCursorPos([ref]$pt) | Out-Null
Write-Output ("GetCursorPos after MOVE=({0},{1})" -f $pt.X,$pt.Y)
[Win32D]::mouse_event(0x0002,$ax,$ay,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[Win32D]::mouse_event(0x0004,$ax,$ay,0,[UIntPtr]::Zero)
Start-Sleep -Seconds 2
$pt2=New-Object Win32D+POINT
[Win32D]::GetCursorPos([ref]$pt2) | Out-Null
Write-Output ("GetCursorPos after CLICK=({0},{1})" -f $pt2.X,$pt2.Y)
$wins=@($root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
Write-Output ("top-level windows={0}" -f $wins.Count)
foreach($w in $wins){ Write-Output ("  win name='{0}'" -f $w.Current.Name) }
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp=New-Object System.Drawing.Bitmap($b.Width,$b.Height)
$g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); $g.Dispose()
$bmp.Save('C:\build\tmw-uitest\probe-click.jpg',[System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
Write-Output 'shot saved probe-click.jpg'
