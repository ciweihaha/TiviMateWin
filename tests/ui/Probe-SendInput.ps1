$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win32SI {
  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  public struct POINT { public int X; public int Y; }
  public const uint INPUT_MOUSE = 0;
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
  public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
}
'@
$p=Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
if($null -eq $p -or $p.MainWindowHandle -eq 0){ Write-Output 'TiviMateWin not running'; exit 1 }
$win=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
Write-Output ("Window rect={0}" -f $win.Current.BoundingRectangle)
[Win32SI]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 500
$btn=$win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnSettings')))
if($null -eq $btn){ Write-Output 'BtnSettings NOT FOUND'; exit 1 }
$br=$btn.Current.BoundingRectangle
$cx=[int]($br.X + $br.Width/2); $cy=[int]($br.Y + $br.Height/2)
Write-Output ("BtnSettings center=({0},{1})" -f $cx,$cy)
$rr=[System.Windows.Automation.AutomationElement]::RootElement.Current.BoundingRectangle
$sw=[int]$rr.Width; $sh=[int]$rr.Height
Write-Output ("root rect={0}x{1}" -f $sw,$sh)
$ax=[int]($cx*65535/$sw); $ay=[int]($cy*65535/$sh)
Write-Output ("SendInput abs=({0},{1})" -f $ax,$ay)
function Send-Mouse([uint]$flags,[int]$x,[int]$y){
  $inp=New-Object Win32SI+INPUT
  $inp.type=[Win32SI]::INPUT_MOUSE
  $inp.mi.dx=$x; $inp.mi.dy=$y; $inp.mi.mouseData=0; $inp.mi.dwFlags=$flags; $inp.mi.time=0; $inp.mi.dwExtraInfo=[IntPtr]::Zero
  $n=[Win32SI]::SendInput(1, @($inp), [System.Runtime.InteropServices.Marshal]::SizeOf($inp))
  Write-Output ("SendInput flags={0} returned={1}" -f $flags,$n)
}
Send-Mouse ([Win32SI]::MOUSEEVENTF_MOVE -bor [Win32SI]::MOUSEEVENTF_ABSOLUTE) $ax $ay
Start-Sleep -Milliseconds 300
$pt=New-Object Win32SI+POINT
[Win32SI]::GetCursorPos([ref]$pt) | Out-Null
Write-Output ("GetCursorPos after SendInput MOVE=({0},{1})" -f $pt.X,$pt.Y)
Send-Mouse ([Win32SI]::MOUSEEVENTF_LEFTDOWN) $ax $ay
Start-Sleep -Milliseconds 100
Send-Mouse ([Win32SI]::MOUSEEVENTF_LEFTUP) $ax $ay
Start-Sleep -Seconds 2
$pt2=New-Object Win32SI+POINT
[Win32SI]::GetCursorPos([ref]$pt2) | Out-Null
Write-Output ("GetCursorPos after CLICK=({0},{1})" -f $pt2.X,$pt2.Y)
$root=[System.Windows.Automation.AutomationElement]::RootElement
$wins=@($root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
Write-Output ("top-level windows={0}" -f $wins.Count)
foreach($w in $wins){ Write-Output ("  win name='{0}'" -f $w.Current.Name) }
