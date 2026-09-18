# Remote Commander Tray

一个极轻量的 Windows 系统托盘小工具，负责运行和管理官方
[Desktop Commander](https://github.com/wonderwhy-er/DesktopCommanderMCP) Remote Device。

它不重新实现 Desktop Commander，也绝不接触任何 OAuth token。它只负责两件事：
Windows 侧的使用体验，以及一个 `desktop-commander remote` 进程的生命周期。

[English](README.md)

## 功能

- 登录 Windows 后自动启动 Remote Device，不弹出 console 窗口。
- 托盘图标一眼可见真实连接状态。
- Agent crash 后自动重启，退避间隔 `5s → 15s → 30s → 60s`。
- 网络短暂抖动时不重启 agent：官方 device 自己处理 heartbeat、stale connection
  和 channel recreation。
- 登录失效时明确提醒，一次点击即可重新认证。
- 永远最多一个 agent；Exit 后没有残留进程。

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

1. 停止当前 child process；
2. 调用官方 `desktop-commander remote --logout`；
3. 重新启动 `desktop-commander remote`；
4. 显示官方 CLI 打印出来的登录地址和验证码。

托盘从不读取、写入、复制或解析 `device.json`，也不实现 OAuth 流程的任何部分。

## 文件

所有本地文件都在 `%LOCALAPPDATA%\RemoteCommanderTray\`：

```
settings.json
logs\agent.log        （1 MB 自动 rotation，保留 3 份）
```

`settings.json` 的各项含义见 [英文 README](README.md#settingsjson)。

"Launch at sign-in" 刻意不放在这里：它的唯一真实来源是用户级注册表项
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RemoteCommanderTray`，
这样从 Windows 自带的"启动应用"页面改动也不会和文件里的副本产生分歧。

## 安全边界

托盘：

- 不解析、不存储、不复制 token；
- 不读写 `device.json`；
- 不实现 OAuth；
- 不实现 Remote MCP protocol；
- 不执行来自网络的 commands。

官方 Desktop Commander 继续拥有 authentication、device identity、Remote MCP 连接、
MCP 命令执行和全部凭据。托盘只读取 CLI 自己的 stdout / stderr 来判断该画什么。

Agent 输出被当作不可信输入：状态行在归一化之后按前缀匹配，因此日志里的 tool call
即使带有任意文本，也无法伪造状态变化。

## 从源码构建

```powershell
dotnet test RemoteCommanderTray.sln
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-x64   -o publish/win-x64
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-arm64 -o publish/win-arm64
```

每次 publish 产出单个 self-contained 的 `RemoteCommanderTray.exe`（约 60 MB）。
不开启 trimming：WinForms 不是 trim-safe 的。

## v0.1 不做

完整 Desktop Commander GUI、文件管理、terminal、MCP configuration editor、聊天 UI、
account management、auto-updater、多设备 dashboard。暂不支持 macOS 和 Linux。

## 许可

MIT，见 [LICENSE](LICENSE)。
