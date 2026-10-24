# TiviMateWin（星辰直播）

Windows 桌面 IPTV 直播播放器：WPF + libVLC + .NET 8，绿色免安装，解压即用。

[English](README.en.md) | 简体中文

![Build & Release](https://github.com/ciweihaha/TiviMateWin/actions/workflows/build.yml/badge.svg)
![License](https://img.shields.io/badge/License-MIT-green)
![.NET](https://img.shields.io/badge/.NET-8.0-blueviolet)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-blue)

![界面布局示意](screenshots/ui-layout.svg)

## 功能特性

- **多播放列表管理**：支持同时添加多个 M3U / M3U8 播放列表与 Xtream Codes 账号，点击即切换，侧栏可展开/收起
- **分组与频道管理**：自动解析 `group-title` 分组，支持分组管理（重命名/合并）、隐藏频道、按号码/名称排序
- **EPG 节目单**：XMLTV EPG 流式解析（XmlReader 逐条读取，大文件内存占用低），当前节目高亮，面板可开合
- **收藏**：频道行尾星标一键收藏，收藏视图快速过滤
- **小窗播放**：画中画置顶小窗，另支持多窗口同时播放不同频道
- **深色 / 浅色主题**：基于资源字典切换，运行时即时生效
- **坏源跳台**：连续换台时自动跳过不可用频道
- **gzip 加速**：自动处理 gzip 压缩的播放列表 / EPG 响应，网络请求支持取消
- **实时搜索**：`Ctrl+F` 搜索频道/分组，`ICollectionView.Filter` 实时过滤
- **全屏播放**：全屏控制浮层鼠标静止自动隐藏

## 下载安装

从 [Releases](https://github.com/ciweihaha/TiviMateWin/releases) 下载最新 `TiviMateWin-win-x64.zip`：

1. 解压到任意目录
2. 运行 `TiviMateWin.exe`

> self-contained 自包含发布，**无需安装 .NET 运行时**，支持 Windows 10/11 x64。

## 快速上手

1. 启动后点击 **添加源**，粘贴 M3U/M3U8 播放列表地址（或选择本地文件），也支持 Xtream API（服务器地址 / 账号 / 密码）
2. 频道加载后**双击**播放；点击频道行尾的 **★** 收藏；右键频道可小窗播放、隐藏、复制地址等
3. 播放中通过右侧 **节目单** 面板查看 EPG；底部状态栏控制播放/暂停、音量与进度

账号密码等敏感信息使用 Windows DPAPI 加密后保存在本地 `config.json`，不上传任何服务器。

## 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Tab` | 收起 / 展开侧栏 |
| `Ctrl+F` | 聚焦搜索框 |
| `Esc` | 清空搜索 / 退出全屏 |
| `F` | 切换全屏 |
| `↑` / `↓` | 频道列表上/下移动（换台） |
| `Ctrl+↑` / `Ctrl+↓` | 音量增 / 减 |
| `Enter` | 播放选中频道；搜索状态下播放第一个结果 |
| `Space` | 播放 / 暂停 |

## 从源码构建

```powershell
dotnet publish TiviMateWin.csproj -c Release -r win-x64 --self-contained true -o publish
```

推送 `v*` 格式的 tag（如 `v1.3.3`）时，GitHub Actions 会自动构建并发布 Release。

## 隐私说明

- 所有配置、播放列表、收藏均保存在本地，无任何遥测
- Xtream 账号密码经 DPAPI 加密落盘

## License

[MIT](LICENSE)

> AI生成
