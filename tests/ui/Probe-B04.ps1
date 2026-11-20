# Probe-B04：分组下拉框选回「全部」到底是 app 不复位，还是脚本点了重复的「全部」项。
# run8 B04 FAIL：选卫视后 29，点回全部仍 29（期望 54）；B02 证据里下拉选项各出现两次。
# 本探针：完整复现 B03→B04，展开时把所有同名项的矩形/IsOffscreen/ClassName 都记下来，
# 只点“可见（矩形非空且 IsOffscreen=False）”的那个，再读 CountText。
# 跑法：run8 跑完后，定时任务 session 1 跑本文件；结果写 C:\build\tmw-uitest\probe-b04.txt。
$ProgressPreference='SilentlyContinue'
$out='C:\build\tmw-uitest\probe-b04.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
  public struct POINT { public int X; public int Y; }
}
'@
[void][W32]::SetProcessDPIAware()
$sw=[W32]::GetSystemMetrics(0); $sh=[W32]::GetSystemMetrics(1)
$rt=[System.Windows.Automation.AutomationElement]::RootElement
$p=(Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1)
if($null -eq $p){ L "NO APP RUNNING"; L "PROBE-DONE"; exit }
$pid0=$p.Id
$mw=$null
for($i=0;$i -lt 10 -and $null -eq $mw;$i++){
  Start-Sleep -Seconds 2
  $ws=@($rt.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
  $mw=$ws | Where-Object { try { $_.Current.ProcessId -eq $pid0 } catch { $false } } | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
}
if($null -eq $mw){ L "MAIN NOT FOUND"; L "PROBE-DONE"; exit }
L ("main rect=" + $mw.Current.BoundingRectangle + " pid=" + $pid0)
Start-Sleep -Seconds 2
[void][W32]::SetForegroundWindow([IntPtr]$mw.Current.NativeWindowHandle)
Start-Sleep -Seconds 1
function Get-El($aid){ $mw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid))) }
function Get-CountText(){
  $ct=Get-El 'CountText'
  if($null -ne $ct){ try { return $ct.Current.Name } catch { return '' } }
  return ''
}
function Click-Center($el){
  $r=$el.Current.BoundingRectangle
  $cx=[int]($r.X + $r.Width/2); $cy=[int]($r.Y + $r.Height/2)
  $ax=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cx * 65535.0 / $sw))
  $ay=[uint32][Math]::Min(65535.0, [Math]::Max(0.0, $cy * 65535.0 / $sh))
  [W32]::mouse_event(0x8001, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  [W32]::mouse_event(0x0002, $ax, $ay, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [W32]::mouse_event(0x0004, $ax, $ay, 0, [IntPtr]::Zero)
  return "($cx,$cy)"
}
function Shot($name){
  try {
    $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save("C:\build\tmw-uitest\probe-b04-$name.jpg", [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $g.Dispose(); $bmp.Dispose()
  } catch { L ("shot $name failed: " + $_.Exception.Message) }
}
function Dump-Items($want){
  # 展开后所有叫 $want 的 ListItem：矩形 / IsOffscreen / 类名 / 是否在弹窗里（父链）。
  $all=@($mw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
  $n=0
  foreach($e in $all){
    try {
      if($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ListItem'){ continue }
      $nm=$e.Current.Name
      if($nm -ne $want){ continue }
      $n++
      $r=$e.Current.BoundingRectangle
      $off=$e.Current.IsOffscreen
      $cls=''; try { $cls=$e.Current.ClassName } catch {}
      L ("  item#$n name=$nm rect=$r offscreen=$off class=$cls w=$($r.Width) h=$($r.Height)")
    } catch {}
  }
  L ("  total '$want' items=$n")
  return $n
}
function Click-VisibleItem($want){
  # 只点可见的（矩形非空且 IsOffscreen=False）；没有可见的返回 $false。
  $all=@($mw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
  foreach($e in $all){
    try {
      if($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ListItem'){ continue }
      if($e.Current.Name -ne $want){ continue }
      $r=$e.Current.BoundingRectangle
      if($r.Width -le 0 -or $r.Height -le 0){ continue }
      if($e.Current.IsOffscreen){ continue }
      $c=Click-Center $e
      L ("  clicked visible '$want' at $c rect=$r")
      return $true
    } catch {}
  }
  L ("  NO visible '$want' item to click")
  return $false
}
function Expand-Combo(){
  $cb=Get-El 'GroupCombo'
  if($null -eq $cb){ L "GroupCombo NOT FOUND"; return $false }
  try {
    $ex=$cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ex.Expand()
    Start-Sleep -Seconds 1
    return $true
  } catch { L ("Expand failed: " + $_.Exception.Message); return $false }
}
L ("count at start=" + (Get-CountText))
# 1. 选卫视
if(Expand-Combo){
  L "--- expand 1: dump '卫视频道' ---"
  Dump-Items '卫视频道' | Out-Null
  Click-VisibleItem '卫视频道' | Out-Null
  Start-Sleep -Seconds 2
  L ("count after 卫视=" + (Get-CountText))
}
# 2. 选回全部
if(Expand-Combo){
  L "--- expand 2: dump '全部' ---"
  Dump-Items '全部' | Out-Null
  Shot 'before-all-click'
  Click-VisibleItem '全部' | Out-Null
  Start-Sleep -Seconds 2
  L ("count after 全部=" + (Get-CountText))
  Shot 'after-all-click'
}
L "PROBE-DONE"
