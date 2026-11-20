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
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@
[void][W32]::SetProcessDPIAware()
$out='C:\build\tmw-uitest\fromhandle.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
$targetPid = (Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1).Id
L ('APP=' + $targetPid)
# minimize all powershell windows so the screenshot shows the app
$psProcs = Get-Process powershell -ErrorAction SilentlyContinue
foreach($pp in $psProcs){ try { if($pp.MainWindowHandle -ne 0){ [void][W32]::ShowWindow($pp.MainWindowHandle, 6) } } catch { } }
Start-Sleep -Seconds 1
# find the two 设置 HWNDs again, then try FromHandle on each
$hwnds = @()
$cb = [W32+EnumProc]{
  param($hWnd, $lParam)
  $wpid = 0; [void][W32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
  if ($wpid -eq $targetPid) {
    $sb = New-Object System.Text.StringBuilder(256)
    [void][W32]::GetWindowText($hWnd, $sb, 256)
    if ($sb.ToString() -eq '设置') { $script:hwnds += $hWnd }
  }
  return $true
}
[void][W32]::EnumWindows($cb, [IntPtr]::Zero)
L ('SETTINGS-HWNDS=' + $hwnds.Count)
foreach($h in $hwnds){
  L ('HWND=' + $h.ToString())
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    if ($null -eq $el) { L '  FromHandle=NULL' }
    else {
      L ('  FromHandle name=[' + $el.Current.Name + '] class=[' + $el.Current.ClassName + '] rect=' + $el.Current.BoundingRectangle)
      # try to find ThemeCombo inside it
      $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeCombo')
      $tc = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
      L ('  ThemeCombo found=' + ($null -ne $tc))
      # count descendants
      $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      L ('  descendants=' + $all.Count)
    }
  } catch { L ('  FromHandle FAILED: ' + $_.Exception.Message) }
}
# screenshot now (powershell minimized)
try {
  $sw=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
  $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save('C:\build\tmw-uitest\fromhandle.jpg', [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $g.Dispose(); $bmp.Dispose()
  L 'SHOT-OK'
} catch { L ('SHOT-FAIL: ' + $_.Exception.Message) }
L 'FROMHANDLE-DONE'
