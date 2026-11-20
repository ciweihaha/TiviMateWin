Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
  public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
  public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
}
'@
[void][Win32]::SetProcessDPIAware()
$out='C:\build\tmw-uitest\probe-c04.txt'
function L($s){ Add-Content -Path $out -Value $s -Encoding UTF8 }
if (Test-Path $out) { Remove-Item $out -Force }
function Get-ElText($el){ try { return $el.Current.Name } catch { return '' } }
function Find-All($root,$ct){
  if ($null -eq $root) { return @() }
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
  try { return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) } catch { return @() }
}
function Find-Id($root,$id){
  if ($null -eq $root) { return $null }
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  try { return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) } catch { return $null }
}
function Click($x,$y){
  $sx = [int]($x * 65535 / 1366); $sy = [int]($y * 65535 / 768)
  [Win32]::mouse_event(0x8001, $sx, $sy, 0, [System.UIntPtr]::Zero)
  [Win32]::mouse_event(0x0002, $sx, $sy, 0, [System.UIntPtr]::Zero)
  [Win32]::mouse_event(0x0004, $sx, $sy, 0, [System.UIntPtr]::Zero)
}
function RightClick($x,$y){
  $sx = [int]($x * 65535 / 1366); $sy = [int]($y * 65535 / 768)
  [Win32]::mouse_event(0x8001, $sx, $sy, 0, [System.UIntPtr]::Zero)
  [Win32]::mouse_event(0x0008, $sx, $sy, 0, [System.UIntPtr]::Zero)
  [Win32]::mouse_event(0x0010, $sx, $sy, 0, [System.UIntPtr]::Zero)
}
function Get-MenuItems{
  $rt=[System.Windows.Automation.AutomationElement]::RootElement
  $menus = Find-All $rt ([System.Windows.Automation.ControlType]::Menu)
  $items=@()
  foreach($m in $menus){ foreach($mi in (Find-All $m ([System.Windows.Automation.ControlType]::MenuItem))){ $items += (Get-ElText $mi) } }
  return $items
}
$proc = Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
L ('APP=' + $proc.Id + ' Responding=' + $proc.Responding)
$win = $null
try { $win = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle) } catch { L 'MAIN-FROMHANDLE-FAIL'; exit 0 }
[void][Win32]::SetForegroundWindow($proc.MainWindowHandle)
Start-Sleep -Seconds 1
$cfgPath = Join-Path $env:APPDATA 'TiviMateWin\config.json'
function Get-Hid { try { @((Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json).HiddenChannels) } catch { @() } }
L ('HID-BEFORE=' + ((Get-Hid) -join '|'))
# turn ON show-hidden first (session setting, lost on restart): right-click row 2, click the show-hidden menu item
$list0 = Find-Id $win 'ChannelList'
$items0 = Find-All $list0 ([System.Windows.Automation.ControlType]::ListItem)
if ($items0.Count -gt 1) {
  $r0 = $items0[1].Current.BoundingRectangle
  $cx0 = [int]($r0.X + $r0.Width/2); $cy0 = [int]($r0.Y + $r0.Height/2)
  Click $cx0 $cy0; Start-Sleep -Milliseconds 300
  RightClick $cx0 $cy0; Start-Sleep -Milliseconds 800
  $rt0=[System.Windows.Automation.AutomationElement]::RootElement
  $menus0 = Find-All $rt0 ([System.Windows.Automation.ControlType]::Menu)
  $showItem = $null
  foreach($m in $menus0){ foreach($mi in (Find-All $m ([System.Windows.Automation.ControlType]::MenuItem))){ if ((Get-ElText $mi) -eq '显示隐藏频道') { $showItem = $mi; break } } if ($null -ne $showItem) { break } }
  if ($null -ne $showItem) {
    try { $showItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); L 'SHOW-HIDDEN-CLICKED' } catch { L 'SHOW-HIDDEN-INVOKE-FAIL' }
  } else { L 'SHOW-HIDDEN-NOT-IN-MENU' }
  Start-Sleep -Seconds 2
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Milliseconds 500
}
# list rows + their text names (find the hidden marker)
$list = Find-Id $win 'ChannelList'
$items = Find-All $list ([System.Windows.Automation.ControlType]::ListItem)
L ('ROWS=' + $items.Count)
for ($i=0; $i -lt [Math]::Min(5,$items.Count); $i++) {
  $texts = Find-All $items[$i] ([System.Windows.Automation.ControlType]::Text)
  $names = @($texts | ForEach-Object { Get-ElText $_ })
  L ("ROW[$i] texts=" + ($names -join '/'))
}
# find row with hidden marker
$hiddenRow = $null
foreach ($it in $items) {
  $texts = Find-All $it ([System.Windows.Automation.ControlType]::Text)
  $names = @($texts | ForEach-Object { Get-ElText $_ })
  if ($names -contains '已隐藏') { $hiddenRow = $it; break }
}
L ('HIDDEN-ROW-FOUND=' + ($null -ne $hiddenRow))
if ($null -ne $hiddenRow) {
  $r = $hiddenRow.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width/2); $cy = [int]($r.Y + $r.Height/2)
  L ('HIDDEN-ROW-RECT=' + $r.ToString())
  Click $cx $cy; Start-Sleep -Milliseconds 300
  RightClick $cx $cy; Start-Sleep -Milliseconds 800
  $menu = Get-MenuItems
  L ('MENU-ON-HIDDEN-ROW=' + ($menu -join '/'))
  # dismiss menu
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Milliseconds 500
}
L ('HID-AFTER=' + ((Get-Hid) -join '|'))
L 'PROBE-C04-DONE'
