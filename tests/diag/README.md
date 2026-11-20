# 诊断构建 dll（H01 用）

`TiviMateWin.dll`（473,600 字节，MD5 `f63ecc28c859ccca73f507a8ddee10e6`）是诊断构建
的 app 主 dll，取自 CI artifact 14553913311（main 提交 b408e30）。
与 v1.4.36 正式版的二进制差异只有这一个 dll（`git diff b680e26a..b408e30`
证实 app 代码只改两处：MainWindow +7/-1、SettingsWindow +2），所以单换
这个 dll 就等价于装完整诊断版。

多写 4 行诊断日志到 `%APPDATA%\TiviMateWin\log.txt`：
- `settings button clicked`（Click 处理器触发）
- `settings window constructed, showing dialog`（SettingsWindow 已构造）
- `SettingsWindow ctor done`（构造函数完成）
- `settings dialog returned: <bool>`（ShowDialog 已返回）

判读：无 clicked = Click 没触发；有 clicked 无 constructed = 处理器内失败；
有 constructed 无 ctor done = 构造抛异常；有 constructed 无 returned =
ShowDialog 卡住/窗口已开但不可见。

为什么放这里：151MB 完整诊断包的 CI 预签链接只有约 10 分钟有效，云电脑
到 blob 存储的单连接总在中途断、断点续传又因链接过期失败（2026-11-20
三次失败）。这个 473KB 的 dll 走 ghfast.top 的 raw 链接就能稳定下载。

用法（run8 跑完、界面空出来后）：云电脑上备份 `C:\TiviMateWin\TiviMateWin.dll`，
用这个文件覆盖，重启 app，跑 Probe-Settings6，读日志定 H01 根因。
H01 定罪后删掉本目录（别留二进制在仓库里）。
