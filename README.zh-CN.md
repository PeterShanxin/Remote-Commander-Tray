# Remote Commander Tray

一个极轻量的 Windows 系统托盘小工具，负责运行和管理官方
[Desktop Commander](https://github.com/wonderwhy-er/DesktopCommanderMCP) Remote Device。

它不重新实现 Desktop Commander，也不管理官方的凭据存储。它只负责两件事：
Windows 侧的使用体验，以及一个 `desktop-commander remote` 进程的生命周期。

[English](README.md)

## 功能

- 登录 Windows 后自动启动 Remote Device，不弹出 console 窗口。
- 托盘图标显示官方 CLI 最近报告的连接状态。
- Agent crash 后自动重启，退避间隔 `5s → 15s → 30s → 60s`。
- 网络短暂抖动时不重启 agent：官方 device 自己处理 heartbeat、stale connection
  和 channel recreation。
- 登录失效时明确提醒，一次点击即可重新认证。
- 同一 supervisor 最多管理一代 agent；Stop / Exit 清理其完整进程树。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809 及以上，x64 或 ARM64 |
| 运行时 | 无需安装 - 发布的 `.exe` 是 self-contained |
| Agent | Node.js + `@wonderwhy-er/desktop-commander` |
| 权限 | 普通用户即可，不需要管理员，不使用 Scheduled Task |

先安装官方 CLI：

```powershell
npm install -g @wonderwhy-er/desktop-commander
```

如果没有全局安装，托盘会回退到 `npx`，首次运行时自动下载。

## 安装

1. 从 [releases 页面](https://github.com/PeterShanxin/Remote-Commander-Tray/releases)
   下载 `RemoteCommanderTray-win-x64.exe` 或 `RemoteCommanderTray-win-arm64.exe`。
2. 放到任意目录，推荐 `%LOCALAPPDATA%\Programs\RemoteCommanderTray\`。
3. 运行后出现在通知区域，并自动启动 agent。
4. 右键菜单勾选 **Launch at sign-in**。

v0.1 没有安装包，也没有自动更新。升级时直接替换 `.exe`。

### 如果你已经用别的方式在跑 Remote Device

托盘的单实例保护只管其他托盘进程，它不知道你已有的 scheduled task、快捷方式或终端窗口。
启用托盘前请先停用那个启动方式，否则会有两个 supervisor 管同一个设备。托盘不会去杀
其他 Node 进程。

如果 **Launch at sign-in** 显示 "(turned off in Windows)"，说明你在 Windows 的"启动应用"
页面里关掉了它。只有 Windows 能重新打开；点击该菜单项会直接打开那个页面。

## 托盘状态

图标不只靠颜色区分，每个状态有各自的图形：

| 图形 | 状态 | 含义 |
| --- | --- | --- |
| 对勾 | Online | 设备已注册并标记为 online |
| 三点 | Connecting / Reconnecting | 正在启动，或正在重建断开的 channel |
| 钥匙 | Authentication required | 官方 CLI 正在等待你登录 |
| 叉 | Offline / Error | Agent 启动失败、退出，或长时间卡住 |
| 暂停 | Stopped | 你手动停止了 agent |

Tooltip 形如：`Remote Commander - Online - YOURMACHINE`。

## 右键菜单

```
✓ Online - YOURMACHINE
Last connected: 13:52

  （登录区块，仅在需要认证时出现）
  Open sign-in page
  Copy code: WDJB-MJHT

Restart connection
Re-authenticate...
Stop agent            （停止后变成 "Start agent"）

Open Remote MCP
Open logs
Copy diagnostics

✓ Launch at sign-in
About
Exit
```

左键单击图标会用气泡显示当前状态。

## 通知

只在真正需要你处理时打扰你：

- 需要登录；
- Agent 连续多次启动失败；
- Agent 在运行但长时间连不上；
- 你手动执行了 re-authentication，需要去浏览器完成。

普通的自动重连不弹通知。

## 重新认证

**Re-authenticate...** 只做四件事：

1. 停止当前 agent 的完整进程树；
2. 调用官方 `desktop-commander remote --logout`；
3. 只有官方 logout 成功后，才重新启动 `desktop-commander remote`；
4. 显示官方 CLI 打印出来的登录地址和验证码。

运行中的会话彻底失效时，只提醒一次，并显示 **Sign in again...**。由用户触发重新登录，
不会循环弹浏览器；退出登录失败时也不会用旧凭据静默启动新 agent。

托盘从不读取、写入、复制或解析 `device.json`，也不实现 OAuth 流程的任何部分。

## 文件

所有本地文件都在 `%LOCALAPPDATA%\RemoteCommanderTray\`：

```
settings.json
logs\agent.log            （1 MB 自动 rotation，保留 3 份）
logs\agent-verbose.log    （仅在 verboseAgentLog 打开时）
```

`agent.log` 只记录程序生成的运行事件名称和省略标记，不保存 CLI 原始行、工具参数、
返回值、工具名称或未知文本。短字符串和嵌套转义 JSON 也不会原样保存。正则脱敏只是
额外防护，不作为处理任意工具输出的安全保证。

**Copy diagnostics 只复制结构化运行摘要**：版本、系统、状态、进程是否运行、计数和时间。
不会附带日志尾部、命令参数、账号/设备自由文本或原始错误。旧版本已有日志也不例外。

`verboseAgentLog` 是独立的敏感调试日志开关，默认关闭。开启后可能保存工具读到的凭据，
不要未经检查直接分享；诊断复制永远不包含它。升级不会静默删除或自动清洗旧日志。

各设置见 [英文 README](README.md#settingsjson)。进程必须成功进入 Job Object 才能运行，
没有绕过此保护的开关。

自启动状态同时读取 Run 注册与 Windows 自己的 StartupApproved 状态。Windows 已禁用时
保留其选择，未知格式或读取失败时显示“未知”，并引导到系统“启动应用”设置，不擅自改写。

## 安全边界

托盘：

- 不读取官方凭据存储，也不实现 token 持久化；
- 不读写 `device.json`；
- 不实现 OAuth；
- 不实现 Remote MCP protocol；
- 不执行来自网络的 commands。

官方 Desktop Commander 继续拥有 authentication、device identity、Remote MCP 连接、
MCP 命令执行和全部凭据。托盘只读取 CLI 自己的 stdout / stderr 来判断该画什么。

Agent 输出被当作不可信输入：先识别已知工具日志格式，再匹配状态前缀。它是兼容性解析器，
不是结构化状态协议。普通日志和诊断排除原始工具内容；主动开启的 verbose 日志仍可能
保留敏感输出，参见上方说明。

## 从源码构建

```powershell
dotnet test RemoteCommanderTray.sln
# 仅 Windows：真实进程树与隔离注册表测试
dotnet run --project tests/RemoteCommanderTray.Windows.Integration -c Release
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-x64   -o publish/win-x64
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-arm64 -o publish/win-arm64
```

每次 publish 产出单个 self-contained 的 `RemoteCommanderTray.exe`（约 60 MB）。
不开启 trimming：WinForms 不是 trim-safe 的。

## 验证范围

自动化测试覆盖核心状态、回调竞态、隐私、真实父子进程退出、超时取消和隔离的启动注册表。
真实浏览器认证、菜单使用、睡眠唤醒和 Windows 登录自启仍需要在正式发布前验收。
切换前先停用旧启动器，本次修复不会替你切换正在使用的后台 agent。

## v0.1 不做

完整 Desktop Commander GUI、文件管理、terminal、MCP configuration editor、聊天 UI、
account management、auto-updater、多设备 dashboard。暂不支持 macOS 和 Linux。

## 许可

MIT，见 [LICENSE](LICENSE)。
