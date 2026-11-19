# WinRM 那头一键跑 UI 回归测试（tests/ui/Test-TiviMateWin.ps1）。
# 用法（沙箱）：WINRM_PASS=123 .../dt.py cloud -f Invoke-UiTests.ps1
# 它只负责：把脚本拉到机器上、用 session 1 定时任务跑、等结果、打印 results.txt。
# 真正的检查逻辑全在 Test-TiviMateWin.ps1 里。任务时限 2 小时（soak 要 30 分钟以上）。
param([string]$UserId = 'Administrator', [string]$RunDir = 'C:\build\tmw-uitest\run1', [string]$Branch = 'main')
$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$script = Join-Path $RunDir 'Test-TiviMateWin.ps1'
$url = "https://ghfast.top/https://raw.githubusercontent.com/ciweihaha/TiviMateWin/$Branch/tests/ui/Test-TiviMateWin.ps1"
try { Invoke-WebRequest -Uri $url -OutFile $script -UseBasicParsing }
catch { Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ciweihaha/TiviMateWin/$Branch/tests/ui/Test-TiviMateWin.ps1" -OutFile $script -UseBasicParsing }
Write-Output "script: $((Get-Item $script).Length) bytes"
$task = 'TMW-UITEST'
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-ExecutionPolicy Bypass -File `"$script`" -OutDir `"$RunDir`""
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $task
$res = Join-Path $RunDir 'results.txt'
for ($i = 0; $i -lt 100; $i++) {
  Start-Sleep -Seconds 60
  $st = (Get-ScheduledTask -TaskName $task).State
  if ((Test-Path $res) -and ($st -eq 'Ready')) { break }
  Write-Output "waiting... $($i + 1) min, task=$st"
}
Get-Content $res -Raw -ErrorAction SilentlyContinue
