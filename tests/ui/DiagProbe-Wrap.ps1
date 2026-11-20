# TMW-DiagProbe wrapper: install diag dll -> Probe-Settings6 -> read diag log -> Probe-B04.
# Run as a Scheduled Task in Session 1; summary -> C:\build\tmw-uitest\diag-probe-summary.txt.
$ProgressPreference='SilentlyContinue'
$sum='C:\build\tmw-uitest\diag-probe-summary.txt'
function L($s){ Add-Content -Path $sum -Value $s -Encoding UTF8; Write-Output $s }
if (Test-Path $sum) { Remove-Item $sum -Force }
L ("start " + (Get-Date -Format 'HH:mm:ss'))
# 0. sync latest probe scripts (commit b641f77; ghfast.top caches raw content, use ?v= to bust)
foreach ($f in @('Probe-Settings6.ps1','Probe-B04.ps1','Install-DiagDll.ps1')) {
  try {
    Invoke-WebRequest -Uri ("https://ghfast.top/https://raw.githubusercontent.com/ciweihaha/TiviMateWin/main/tests/ui/$f?v=b641f77") -OutFile "C:\build\tmw-uitest\$f" -UseBasicParsing -TimeoutSec 90
    L ("synced $f bytes=" + (Get-Item "C:\build\tmw-uitest\$f").Length)
  } catch { L ("sync FAIL $f : " + $_.Exception.Message) }
}
# 1. install diag dll
L '--- step1 Install-DiagDll ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\build\tmw-uitest\Install-DiagDll.ps1' 2>&1 | ForEach-Object { L $_ }
if (Test-Path 'C:\build\tmw-uitest\diag-install.txt') { Get-Content 'C:\build\tmw-uitest\diag-install.txt' | ForEach-Object { L ("install: " + $_) } }
# 2. Probe-Settings6 (restarts app itself, clicks settings)
L '--- step2 Probe-Settings6 ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\build\tmw-uitest\Probe-Settings6.ps1' 2>&1 | ForEach-Object { L $_ }
if (Test-Path 'C:\build\tmw-uitest\probe-out6.txt') { Get-Content 'C:\build\tmw-uitest\probe-out6.txt' | ForEach-Object { L ("probe6: " + $_) } }
# 3. read the 4 diag log lines
L '--- step3 diag log lines ---'
$log = Join-Path $env:APPDATA 'TiviMateWin\log.txt'
if (Test-Path $log) {
  $lines = Get-Content $log -Tail 400 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'settings button clicked|settings window constructed|SettingsWindow ctor done|settings dialog returned' }
  if ($lines) { $lines | ForEach-Object { L ("diaglog: " + $_) } } else { L 'diaglog: NO diag lines found in last 400 lines (diag dll may not be active)' }
} else { L 'diaglog: log file not found' }
# 4. Probe-B04
L '--- step4 Probe-B04 ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\build\tmw-uitest\Probe-B04.ps1' 2>&1 | ForEach-Object { L $_ }
if (Test-Path 'C:\build\tmw-uitest\probe-b04.txt') { Get-Content 'C:\build\tmw-uitest\probe-b04.txt' | ForEach-Object { L ("probeB04: " + $_) } }
L ("done " + (Get-Date -Format 'HH:mm:ss'))
L 'DIAG-PROBE-DONE'
