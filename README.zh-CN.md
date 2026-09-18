# Remote Commander Tray

一个极轻量的 Windows 系统托盘小工具，负责运行和管理官方
[Desktop Commander](https://github.com/wonderwhy-er/DesktopCommanderMCP) Remote Device。

它不重新实现 Desktop Commander，也不读取其凭据文件。官方 CLI 的原始输出可能含敏感数据，
普通日志和诊断报告刻意不保留这些内容。它只负责两件事：
Windows 侧的使用体验，以及一个 `desktop-commander remote` 进程的生命周期。

[English](README.md)

## 功能

- 登录 Windows 后自动启动 Remote Device，不弹出 console 窗口。
- 托盘图标显示 CLI 最近报告的连接状态。
- Agent crash 后自动重启，退避间隔 `5s → 15s → 30s → 60s`。
- 网络短暂抖动时不重启 agent：官方 device 自己处理 heartbeat、stale connection
  和 channel recreation。
- 登录失效时明确提醒，一次点击即可重新认证。
- 同时管理一个 agent 进程组，替换前先回收整组进程。已有的计划任务或手动 CLI 必须先停用。

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
页面里关掉了它。请在 Windows 中重新启用，点击该菜单项会提示打开该页面。
无法读取或识别系统的启动许可记录时会显示未知，不会误报已启用。

## 托盘状态

图标不只靠颜色区分，每个状态有各自的图形：

| 图形 | 状态 | 含义 |
| --- | --- | --- |
| 对勾 | Online | 设备已注册并标记为 online |
| 三点 | Connecting / Reconnecting | 正在启动，或正在重建断开的 channel |
| 钥匙 | Authentication required | 登录尚未完成，或会话已失效需要重新认证 |
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

1. 停止并回收当前整组进程；
2. 调用官方 `desktop-commander remote --logout`；
3. 重新启动 `desktop-commander remote`；
4. 显示官方 CLI 打印出来的登录地址和验证码。

托盘从不读取、写入、复制或解析 `device.json`，也不实现 OAuth 流程的任何部分。
注销失败或超时会明确报错并保持停止，不会假装登录成功。终止性会话失效、登录等待超时
只提醒一次并等待用户操作，不会自动反复弹浏览器，也不会自动删除凭据。

## 文件

所有本地文件都在 `%LOCALAPPDATA%\RemoteCommanderTray\`：

```
settings.json
logs\agent.log            （1 MB 自动 rotation，保留 3 份）
logs\agent-verbose.log    （仅在 verboseAgentLog 打开时）
```

`agent.log` 仅保存托盘生命周期和固定的状态分类，不保存 CLI 原始状态后缀、未知文本、
验证码/登录地址、工具参数或返回内容。未知输出仅记录省略标记和长度。
正则脱敏只是额外防护，不作为任意文本绝不含敏感数据的保证。

`Copy diagnostics` 仅复制结构化状态、时间、重启次数和启动状态，不复制任何日志尾部
（包括旧版本写过的日志）、账户/设备标识、命令参数、原始错误或验证码。

`verboseAgentLog` 是显式开启的独立调试日志，可能包含工具读取的私人内容。
普通诊断不会读取它；分享前必须自行检查。

`settings.json` 的各项含义见 [英文 README](README.md#settingsjson)。

"Launch at sign-in" 不在 settings.json 中保存副本。托盘同时读取用户的 Run 注册项
和 Windows 的 StartupApproved 状态，但绝不改写 StartupApproved。
移动程序时更新启动路径也会保留系统禁用选择；显示的是观察到的状态，不保证系统策略
一定允许下次登录启动。

## 安全边界

托盘：

- 不读取或管理 OAuth 凭据；
- 不读写 `device.json`；
- 不实现 OAuth；
- 不实现 Remote MCP protocol；
- 不执行来自网络的 commands。

官方 Desktop Commander 继续拥有 authentication、device identity、Remote MCP 连接、
MCP 命令执行和全部凭据。托盘只读取 CLI 自己的 stdout / stderr 来判断该画什么。

状态解析基于已验证的 CLI 版本，是尽力观察，不是独立的服务器在线探测。
工具帧和 JSON 返回内容在状态归一化前排除。托盘不为官方 CLI 执行的命令提供安全沙箱。

每组进程在创建时就原子地加入独立的 Windows Job Object。创建或清理失败时停止恢复，
不退回无约束进程。正常子进程均在回收范围内；通过其他 Windows 服务刻意启动的进程
不属于此保证。

## 从源码构建

```powershell
dotnet test RemoteCommanderTray.sln
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-x64   -o publish/win-x64
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-arm64 -o publish/win-arm64
```

每次 publish 产出单个 self-contained 的 `RemoteCommanderTray.exe`（约 60 MB）。
不开启 trimming：WinForms 不是 trim-safe 的。

完整测试在 Windows x64/ARM64 的 .NET 8 CI 上执行；Linux/macOS 可单独运行核心测试项目。
自动化覆盖、真实环境测试范围和仍需人工验收的项目见 [验证说明](docs/verification.md)。

## v0.1 不做

完整 Desktop Commander GUI、文件管理、terminal、MCP configuration editor、聊天 UI、
account management、auto-updater、多设备 dashboard。暂不支持 macOS 和 Linux。

## 许可

MIT，见 [LICENSE](LICENSE)。
