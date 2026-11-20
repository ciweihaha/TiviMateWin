$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$Win32 = @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@
Add-Type -TypeDefinition $Win32 -ErrorAction SilentlyContinue
try { [Win32]::SetProcessDPIAware() | Out-Null } catch { }
$out='C:\build\tmw-uitest\probe-getwins.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
L 'START'
$p = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $p) { L 'NO-APP'; exit 0 }
$pid0 = $p.Id
L ('PID=' + $pid0)
$script:TmwPid10 = $pid0
$script:TmwHwnds10 = @()
$script:TmwExclude10 = @('LibVLCSharp.WPF', 'VLC (Direct3D11 output)', 'Hidden Window', 'MediaContextNotificationWindow', 'SystemResourceNotifyWindow', 'CiceroUIWndFrame', 'Mode Indicator', 'MSCTFIME UI', 'Default IME')
try {
  $cb = [Win32+EnumWindowsProc]{
    param($hWnd, $lParam)
    $wpid = 0; [void][Win32]::GetWindowThreadProcessId($hWnd, [ref]$wpid)
    if ($wpid -eq $script:TmwPid10 -and [Win32]::IsWindowVisible($hWnd)) {
      $sb = New-Object System.Text.StringBuilder(256)
      [void][Win32]::GetWindowText($hWnd, $sb, 256)
      $t = $sb.ToString()
      if ($t.Length -gt 0 -and $script:TmwExclude10 -notcontains $t) { $script:TmwHwnds10 += $hWnd }
    }
    return $true
  }
  L 'CALLBACK-CREATED'
  [void][Win32]::EnumWindows($cb, [IntPtr]::Zero)
  L ('ENUM-OK count=' + $script:TmwHwnds10.Count)
  foreach ($h in $script:TmwHwnds10) {
    try {
      $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
      L ('  HWND=' + $h.ToString() + ' name=[' + $el.Current.Name + ']')
    } catch { L ('  HWND=' + $h.ToString() + ' FromHandle-FAIL: ' + $_.Exception.Message) }
  }
} catch {
  L ('EXCEPTION: ' + $_.Exception.GetType().FullName + ' : ' + $_.Exception.Message)
}
L 'PROBE-GETWINS-DONE'
