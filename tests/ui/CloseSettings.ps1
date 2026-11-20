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
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
[void][W32]::SetProcessDPIAware()
$out='C:\build\tmw-uitest\close-settings.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
$targetPid = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
L ('APP=' + $targetPid)
# find settings dialogs by size 460x520 (ASCII-only, no Chinese literals)
$dlg = @()
$cb = [W32+EnumProc]{
  param($hWnd, $lParam)
  $wpid = 0; [void][W32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
  if ($wpid -eq $targetPid) {
    $r = New-Object W32+RECT
    [void][W32]::GetWindowRect($hWnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -eq 460 -and $h -eq 520) { $script:dlg += $hWnd }
  }
  return $true
}
[void][W32]::EnumWindows($cb, [IntPtr]::Zero)
L ('DLG-BY-RECT=' + $dlg.Count)
foreach($h in $dlg){
  L ('HWND=' + $h.ToString())
  # FromHandle test (UIA by handle)
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    if ($null -eq $el) { L '  FromHandle=NULL' }
    else {
      L ('  FromHandle name=[' + $el.Current.Name + '] rect=' + $el.Current.BoundingRectangle)
      $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeCombo')
      $tc = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
      L ('  ThemeCombo found=' + ($null -ne $tc))
      $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      L ('  descendants=' + $all.Count)
    }
  } catch { L ('  FromHandle FAILED: ' + $_.Exception.Message) }
}
# close each dialog via WM_CLOSE (0x0010)
foreach($h in $dlg){
  [void][W32]::SendMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  L ('WM_CLOSE sent to ' + $h.ToString())
}
Start-Sleep -Seconds 3
# re-enumerate
$dlg = @()
[void][W32]::EnumWindows($cb, [IntPtr]::Zero)
L ('DLG-AFTER-CLOSE=' + $dlg.Count)
# screenshot the main window
try {
  $sw=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
  $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save('C:\build\tmw-uitest\close-settings.jpg', [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $g.Dispose(); $bmp.Dispose()
  L 'SHOT-OK'
} catch { L ('SHOT-FAIL: ' + $_.Exception.Message) }
L 'CLOSE-SETTINGS-DONE'
