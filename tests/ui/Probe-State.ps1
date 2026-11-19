$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@
[void][W32]::SetProcessDPIAware()
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$procs=Get-Process TiviMateWin -ErrorAction SilentlyContinue
Write-Output ("procs=" + @($procs).Count)
foreach($p in $procs){ Write-Output ("pid=" + $p.Id + " start=" + $p.StartTime + " responding=" + $p.Responding) }
$ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
foreach($w in $ws){
  try {
    $pid=$w.Current.ProcessId
    $pname=(Get-Process -Id $pid -ErrorAction SilentlyContinue).ProcessName
    if($pname -eq 'TiviMateWin'){
      $h=$w.Current.NativeWindowHandle
      Write-Output ("TMW win name=" + $w.Current.Name + " rect=" + $w.Current.BoundingRectangle + " iconic=" + [W32]::IsIconic([IntPtr]$h) + " zoomed=" + [W32]::IsZoomed([IntPtr]$h))
    }
  } catch { Write-Output ("win err: " + $_.Exception.Message) }
}
# screenshot
Add-Type -AssemblyName System.Drawing
$bmp=New-Object System.Drawing.Bitmap(1920,1080)
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0,0,0,0,$bmp.Size)
$g.Dispose()
$enc=[System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep=New-Object System.Drawing.Imaging.EncoderParameters(1)
$ep.Param[0]=New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 55)
$bmp.Save('C:\build\tmw-uitest\state.jpg', $enc, $ep)
$bmp.Dispose()
Write-Output "SHOT-DONE"
