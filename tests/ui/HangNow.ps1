Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
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
$targetPid = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
$out='C:\build\tmw-uitest\hang-now.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
L ('APP=' + $targetPid + ' Responding=' + (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Responding)
$found = @()
$cb = [W32+EnumProc]{
  param($hWnd, $lParam)
  $wpid = 0; [void][W32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
  if ($wpid -eq $targetPid) {
    $sb = New-Object System.Text.StringBuilder(256)
    [void][W32]::GetWindowText($hWnd, $sb, 256)
    $script:found += ('HWND=' + $hWnd.ToString() + ' title=[' + $sb.ToString() + '] visible=' + [W32]::IsWindowVisible($hWnd))
  }
  return $true
}
[void][W32]::EnumWindows($cb, [IntPtr]::Zero)
L ('ENUM-WINDOWS-FOR-PID=' + $found.Count)
$found | ForEach-Object { L $_ }
# UIA top-level windows (session 1)
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
$mine=@($ws | Where-Object { try { $_.Current.ProcessId -eq $targetPid } catch { $false } })
L ('UIA-WINDOWS-FOR-PID=' + $mine.Count)
foreach($w in $mine){ try { L ('  UIA name=[' + $w.Current.Name + '] rect=' + $w.Current.BoundingRectangle) } catch { } }
# screenshot
try {
  $sw=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
  $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save('C:\build\tmw-uitest\hang-now.jpg', [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $g.Dispose(); $bmp.Dispose()
  L 'SHOT-OK'
} catch { L ('SHOT-FAIL: ' + $_.Exception.Message) }
# diag log: any dialog returned yet?
$log = Join-Path $env:APPDATA 'TiviMateWin\log.txt'
$lines = Get-Content $log -Tail 60 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'settings dialog returned' }
L ('DIALOG-RETURNED-LINES=' + $lines.Count)
L 'HANG-NOW-DONE'
