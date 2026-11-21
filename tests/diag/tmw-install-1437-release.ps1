# TiviMateWin v1.4.37 云电脑安装脚本（官方 Release 包，全量安装）
# 来源：https://github.com/ciweihaha/TiviMateWin/releases/tag/v1.4.37
# 安装包 TiviMateWin-win-x64.zip = 151774609 字节
$ErrorActionPreference = 'Stop'
$log = 'C:\build\tmw-v1.4.37-install.log'
$zip = 'C:\build\TiviMateWin-1.4.37.zip'
$expectedSize = 151774609
function Log($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" | Out-File -FilePath $log -Append -Encoding utf8; Write-Host $m }
New-Item -ItemType Directory -Force -Path 'C:\build' | Out-Null
'' | Out-File -FilePath $log -Encoding utf8
Log 'install start: v1.4.37'

# 1. 停残留 app（run21 浸泡进程也在其中）
Get-Process -Name 'TiviMateWin' -ErrorAction SilentlyContinue | ForEach-Object {
  Log "stop pid $($_.Id)"
  try { $_.Kill() } catch { Log "kill failed: $_" }
}
Start-Sleep -Seconds 3
if (Get-Process -Name 'TiviMateWin' -ErrorAction SilentlyContinue) { Log 'WARN: TiviMateWin still running'; Start-Sleep -Seconds 5 }

# 2. 下载官方 Release 包（ghfast.top 优先，GitHub 直链兜底，各最多 2 次）
$urls = @(
  'https://ghfast.top/https://github.com/ciweihaha/TiviMateWin/releases/download/v1.4.37/TiviMateWin-win-x64.zip',
  'https://github.com/ciweihaha/TiviMateWin/releases/download/v1.4.37/TiviMateWin-win-x64.zip'
)
$ok = $false
foreach ($u in $urls) {
  for ($try = 1; $try -le 2 -and -not $ok; $try++) {
    Log "download try $try : $u"
    try {
      if (Test-Path $zip) { Remove-Item $zip -Force }
      $sw = [System.Diagnostics.Stopwatch]::StartNew()
      Invoke-WebRequest -Uri $u -OutFile $zip -TimeoutSec 1800
      $sw.Stop()
      $size = (Get-Item $zip).Length
      Log "downloaded $size bytes in $([int]$sw.Elapsed.TotalSeconds)s"
      if ($size -eq $expectedSize) { $ok = $true } else { Log "size mismatch: want $expectedSize got $size" }
    } catch { Log "download error: $($_.Exception.Message)" }
  }
  if ($ok) { break }
}
if (-not $ok) { Log 'INSTALL FAILED: download'; exit 1 }

# 3. 备份 1.4.36（已有备份则跳过）
if (-not (Test-Path 'C:\TiviMateWin-v1.4.36-old')) {
  Log 'backup C:\TiviMateWin -> C:\TiviMateWin-v1.4.36-old'
  robocopy 'C:\TiviMateWin' 'C:\TiviMateWin-v1.4.36-old' /MIR /NFL /NDL /NJH /NJS | Out-Null
} else { Log 'backup already exists, skip' }

# 4. 解压覆盖
Log 'extract over C:\TiviMateWin'
Expand-Archive -Path $zip -DestinationPath 'C:\TiviMateWin' -Force

# 5. 验版本
$vi = (Get-Item 'C:\TiviMateWin\TiviMateWin.exe').VersionInfo
Log "exe FileVersion=$($vi.FileVersion) ProductVersion=$($vi.ProductVersion)"
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
Log "zip SHA256=$sha"
if ($vi.ProductVersion -notlike '1.4.37*') { Log 'INSTALL FAILED: version mismatch'; exit 1 }
Log 'INSTALL OK: v1.4.37 in place'
