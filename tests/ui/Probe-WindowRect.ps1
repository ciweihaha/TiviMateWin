$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=[System.Windows.Automation.AutomationElement]::RootElement
$win=$root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
Write-Output ("Window name={0}" -f $win.Current.Name)
Write-Output ("Window rect={0}" -f $win.Current.BoundingRectangle)
Write-Output ("IsOffscreen={0} IsEnabled={1}" -f $win.Current.IsOffscreen, $win.Current.IsEnabled)
$p=Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
Write-Output ("proc id={0} mainWindowTitle={1}" -f $p.Id, $p.MainWindowTitle)
Add-Type -AssemblyName System.Windows.Forms
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output ("Screen Bounds={0}x{1} at ({2},{3})" -f $b.Width,$b.Height,$b.X,$b.Y)
Write-Output ("Screen WorkingArea={0}" -f [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea)
