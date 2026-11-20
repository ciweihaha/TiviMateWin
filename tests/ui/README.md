# TiviMateWin UI 回归测试脚本（用户 2026-11-19 立的规矩：先测后发）
#
# 用途：发版前（打 tag 之前）或已发版后，在真机上把用户提过的所有功能逐项跑一遍，
# 再加探查隐藏 bug 的用例（边界、异常路径、长时间播），脚本全过才算完。
# 每轮发版都复用这份脚本；新功能进来时，先往这里加用例、再写代码。
#
# 跑法（必须在交互会话 session 1 里跑，WinRM session 0 看不到窗口）：
#   1. 把本文件拷到测试机（云电脑/台式机），例如 C:\build\tmw-uitest\Test-TiviMateWin.ps1
#      （从仓库拉：https://ghfast.top/https://raw.githubusercontent.com/ciweihaha/TiviMateWin/main/tests/ui/Test-TiviMateWin.ps1）
#   2. 建一个 session 1 定时任务跑它（见 Invoke-UiTests.ps1，WinRM 那头一键跑）：
#        powershell.exe -ExecutionPolicy Bypass -File C:\build\tmw-uitest\Test-TiviMateWin.ps1 -OutDir C:\build\tmw-uitest\run1
#      任务的 ExecutionTimeLimit 至少 2 小时（soak 阶段要 30 分钟以上）。
#   3. 跑完读 OutDir\results.txt（逐项 PASS/FAIL/NEEDS-EYE）+ results.jsonl + 截图。
#
#  verdict 说明：
#   PASS      脚本自己能判定的检查过了（UIA 读数 / config 值 / log / 像素对比）。
#   FAIL      没过——修代码、重测，直到全过才能发版。
#   NEEDS-EYE 脚本只能留证据、判不了（OSD 文字、暂停徽章这类视频叠加层不在 UIA 树里），
#             须由人/ agent 看截图定 PASS/FAIL；有 NEEDS-EYE 未复核时也不算“全过”。
#
# 已知坑（前面几轮实测攒下的，别再踩）：
# - OSD（切台信息条）在 UIA 树里根本不存在，只能靠截图。
# - 频道列表项在 UIA 里是 0 个（虚拟化），点台用数字键，别点列表。
# - SetCursorPos 会被 DPI 虚拟化 ×1.25，点按用 mouse_event 绝对坐标（脚本里的 Invoke-MouseClick）。
# - 脚本启动先调 SetProcessDPIAware，再用 GetSystemMetrics(0/1) 算鼠标坐标；
#   Screen.Bounds 是缓存的逻辑值（125% 下 1536x864，物理 1920x1080），永不用（run8）。
# - 截图是物理像素 1:1；UIA 矩形也是物理像素，直接对上，不用换算。
# - app 会无崩溃自己退出（“悄悄退出”），soak 阶段专门盯它；中途退了就是 FAIL。
# - 云电脑上流经常 stall，别把卡顿当失败；soak 只盯“进程还在不在 + 时间在不在走”。
# - 翻译 Key 都是空的（Azure + Cloudflare 没配），这是预期的；提示自动消失那项靠它触发。
# - 切台用数字键：按 "1" 要等 1.2 秒才提交（10–19 都以 1 开头），按 "51" 立即提交。

# 用例清单（id 对应脚本里的 Test-XXX 函数；每轮按这个表核对）：
#   A01 标题栏版本号            A02 节目单加载 N 台          A03 启动耗时（只记录）
#   B01 分组下拉框字看得见（像素对比 + 截图）  B02 下拉框 4 个选项
#   B03 选“卫视频道”过滤        B04 选回“全部”恢复
#   B05 设置页下拉框（主题）字看得见（在 H01 打开设置页时顺带测）
#   C01 右键菜单没有“复制频道地址”  C02 右键“隐藏频道”   C03 “显示隐藏频道”找回
#   C04 右键“显示频道”取消隐藏  C05 config 里 HiddenChannels 为空
#   D01 新窗口播放时主窗口停掉  D02 关新窗口后主窗口恢复
#   E01 切台 OSD 四行（截图复核） E02 无节目单台显示“暂无节目信息”（截图复核）
#   E03 没有单独的“节目单”按钮  E04 E 键打开完整节目单（截图复核）
#   F01 “免费接口未配置”提示 5 秒自动消失、不反复弹（截图复核）
#   G01 点“刷新”显示“已刷新：N 个频道 HH:mm”
#   H01 设置页显示“当前版本”    H02 “检查更新”显示“已是最新版本”
#   I01 空格键暂停（时间停走 + 截图复核徽章）  I02 再按空格继续
#   J01 翻译“强制开”联动主开关（config 变成 true）  J02 恢复基线
#   K01 快速连切 10 台不崩      K02 断流台不退出（凤凰中文 60 秒）
#   K03 新窗口开/关 3 次不漏窗口 K04 深色→浅色→深色切换不崩
#   K05 搜不到的词不崩、清空恢复 K06 config 损坏后仍能启动（备份+恢复）
#   L01 连续播 30 分钟不退出（soak；卡顿/断流次数只记录，不判失败）

# run8 加固（v1.4.37）：E04 只凭截图不能定罪——面板矩形须完全落在主窗口矩形内才 PASS（run7 疑似 DPI 取图裁切）；
# H01 打不开时枚举全部顶层窗口，区分“点击没触发”与“已有模态窗挡住 UI 线程”；F01 证据里写明设置窗门禁（H01 未过则 toast 无法验证）。
