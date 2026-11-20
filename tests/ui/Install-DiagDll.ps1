# Install-DiagDll：把诊断版 TiviMateWin.dll 换上（H01 定根因用）。
# 跑法：run8 跑完后，定时任务 session 1 跑本文件；结果写 C:\build\tmw-uitest\diag-install.txt。
# 诊断 dll 在仓库 tests/diag/TiviMateWin.dll（commit 6bbbe54），经 ghfast.top 下，
# 473,600 字节，MD5 f63ecc28c859ccca73f507a8ddee10e6。不要碰 C:\TiviMateWin-v1.4.34-old。
$ProgressPreference='SilentlyContinue'
$log='C:\build\tmw-uitest\diag-install.txt'
function L($s){ Add-Content -Path $log -Value $s -Encoding UTF8; Write-Output $s }
if (Test-Path $log) { Remove-Item $log -Force }
$url='https://ghfast.top/https://raw.githubusercontent.com/ciweihaha/TiviMateWin/main/tests/diag/TiviMateWin.dll'
$expectMd5='f63ecc28c859ccca73f507a8ddee10e6'
$dst='C:\build\tmw-diag.dll'
# 1. 下载
try {
  Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing -TimeoutSec 120
  L ("downloaded bytes=" + (Get-Item $dst).Length)
} catch {
  L ("DOWNLOAD FAIL: " + $_.Exception.Message)
  L 'INSTALL-FAILED'; exit 1
}
# 2. 验 MD5
$md5=(Get-FileHash -Path $dst -Algorithm MD5).Hash.ToLower()
L ("md5=" + $md5)
if ($md5 -ne $expectMd5) { L 'MD5 MISMATCH, abort'; L 'INSTALL-FAILED'; exit 1 }
# 3. 停 app、备份原 dll、替换
try { Get-Process TiviMateWin -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Seconds 3
$target='C:\TiviMateWin\TiviMateWin.dll'
$bak='C:\TiviMateWin\TiviMateWin.dll.1.4.36-bak'
if (-not (Test-Path $bak)) { Copy-Item $target $bak -Force; L ("backed up to " + $bak) } else { L "backup already exists, keep it" }
Copy-Item $dst $target -Force
$md5b=(Get-FileHash -Path $target -Algorithm MD5).Hash.ToLower()
L ("installed md5=" + $md5b)
if ($md5b -ne $expectMd5) { L 'INSTALLED MD5 MISMATCH'; L 'INSTALL-FAILED'; exit 1 }
# 4. 启动 app，等主窗口
Start-Process -FilePath 'C:\TiviMateWin\TiviMateWin.exe' -WorkingDirectory 'C:\TiviMateWin'
Start-Sleep -Seconds 8
$p=Get-Process TiviMateWin -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $p) { L 'APP DID NOT START'; L 'INSTALL-FAILED'; exit 1 }
L ("app started pid=" + $p.Id)
L 'INSTALL-OK'
