$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=[System.Windows.Automation.AutomationElement]::RootElement
$wins=@($root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))
Write-Output ("top-level windows={0}" -f $wins.Count)
foreach($w in $wins){
  try { Write-Output ("  name='{0}' rect={1} offscreen={2}" -f $w.Current.Name, $w.Current.BoundingRectangle, $w.Current.IsOffscreen) } catch { Write-Output ("  (err reading window)") }
}
$p=Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
if($null -ne $p){ Write-Output ("TiviMateWin proc id={0} responding={1}" -f $p.Id, $p.Responding) } else { Write-Output 'TiviMateWin proc NOT FOUND' }
