# Remote Commander Tray

A very small Windows system-tray companion that runs and supervises the official
[Desktop Commander](https://github.com/wonderwhy-er/DesktopCommanderMCP) Remote Device.

It does not reimplement Desktop Commander or read its credential store. Raw CLI
output is sensitive; ordinary logs and copied diagnostics deliberately omit it.
It owns exactly two things: the Windows user experience, and the lifecycle of one
`desktop-commander remote` process.

[中文说明](README.zh-CN.md)

## What it does

- Starts the Remote Device when you sign in to Windows, with no console window.
- Shows the latest CLI-reported connection status in the notification area, at a glance.
- Restarts the agent automatically if it crashes, with a `5s → 15s → 30s → 60s` backoff.
- Leaves the agent alone during ordinary network blips, because the official device
  already handles heartbeats, stale connections and channel recreation itself.
- Tells you clearly when sign-in has expired, and re-authenticates in one click.
- Owns one agent generation at a time and drains its process group before replacement.
  Existing scheduled tasks or manually started devices must be stopped separately.

## Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows 10 1809 or later, x64 or ARM64 |
| Runtime | None - the published `.exe` is self-contained |
| Agent | Node.js plus `@wonderwhy-er/desktop-commander` (see below) |
| Privileges | Ordinary user; no admin rights, no scheduled task |

Install the agent once:

```powershell
npm install -g @wonderwhy-er/desktop-commander
```

If it is not installed globally, the tray falls back to `npx`, which downloads the
package on first run.

## Install

1. Download `RemoteCommanderTray-win-x64.exe` or `RemoteCommanderTray-win-arm64.exe`
   from the [releases page](https://github.com/PeterShanxin/Remote-Commander-Tray/releases).
2. Put it anywhere you like - `%LOCALAPPDATA%\Programs\RemoteCommanderTray\` is a good spot.
3. Run it. It appears in the notification area and starts the agent.
4. Right-click the icon and tick **Launch at sign-in**.

There is no installer and no auto-updater in v0.1. To update, replace the `.exe`.

### If you already run the Remote Device another way

The tray's single-instance guard only coordinates other copies of the tray. It does not
know about a scheduled task, a shortcut, or a terminal window you already use to run
`desktop-commander remote`. Stop and disable that launcher before enabling the tray,
otherwise two supervisors will run the same device. The tray will not go looking for
other Node processes to kill.

If **Launch at sign-in** shows "(turned off in Windows)", Windows has disabled the entry
from its own Startup Apps page. Use that Windows page to re-enable it; clicking the item offers to open the page.
An unreadable or unfamiliar approval record is shown as unknown, never falsely enabled.

## Tray states

Each state has its own shape as well as its own colour, so it stays readable without
relying on colour alone.

| Icon | State | Meaning |
| --- | --- | --- |
| Check | Online | Device registered and marked online |
| Dots | Connecting / Reconnecting | Starting up, or re-establishing a dropped channel |
| Key | Authentication required | Sign-in is pending or the session needs re-authentication |
| Cross | Offline / Error | The agent failed, exited, or has been stuck for too long |
| Pause | Stopped | You stopped the agent |

Tooltip: `Remote Commander - Online - YOURMACHINE`.

## Menu

```
✓ Online - YOURMACHINE
Last connected: 13:52

  (sign-in section, only while authentication is required)
  Open sign-in page
  Copy code: WDJB-MJHT

Restart connection
Re-authenticate...
Stop agent            (becomes "Start agent" when stopped)

Open Remote MCP
Open logs
Copy diagnostics

✓ Launch at sign-in
About
Exit
```

Left-clicking the icon shows the current status as a balloon.

## Notifications

You are only interrupted when you actually have to do something:

- sign-in is required;
- the agent has failed to stay running several times in a row;
- the agent has been running but not connected for several minutes;
- you asked to re-authenticate and the browser flow is now waiting for you.

Ordinary reconnects are silent.

## Re-authenticate

**Re-authenticate...** does exactly four things:

1. stops the entire owned process generation;
2. runs the official `desktop-commander remote --logout`;
3. starts `desktop-commander remote` again;
4. shows the sign-in page and code that the official CLI prints.

The tray never reads, writes, copies or parses `device.json`, and never implements any
part of the OAuth flow. A failed or timed-out logout leaves the agent stopped and
reports failure instead of pretending to sign in successfully. Terminal session loss
and expired sign-in prompts pause for user action with one notification: the tray does
not repeatedly open browsers or delete credentials automatically.

## Files

Everything the tray writes lives in `%LOCALAPPDATA%\RemoteCommanderTray\`:

```
settings.json
logs\agent.log            (rotated at 1 MB, 3 files kept)
logs\agent-verbose.log    (only when verboseAgentLog is on)
```

`agent.log` contains tray lifecycle events and canonical status categories only.
It never stores raw CLI status suffixes, unknown text, verification codes/URLs, or tool
arguments/results. Tool activity and omitted output use fixed descriptions or lengths.
Regex redaction is additional protection, not a guarantee over arbitrary data.

Setting `verboseAgentLog` explicitly opts into a separate sensitive debug file. It can
contain private tool output even after best-effort redaction. Do not share it without
review. Neither current nor historical logs are included by **Copy diagnostics**.

### settings.json

| Key | Default | Meaning |
| --- | --- | --- |
| `startAgentOnLaunch` | `true` | Start the agent when the tray starts |
| `agentExecutable` | `null` | Override the auto-detected CLI path |
| `agentArguments` | `null` | Arguments inserted before `remote` |
| `remoteMcpUrl` | `https://mcp.desktopcommander.app` | Opened by "Open Remote MCP" |
| `stalledConnectionMinutes` | `5` | How long a stuck reconnect runs before you are told |
| `startFailureAlertThreshold` | `3` | Consecutive failed starts before you are told |
| `healthyRunSeconds` | `60` | A run this long resets the restart backoff |
| `logMaxBytes` | `1048576` | Rotate `agent.log` past this size |
| `logRetainedFiles` | `3` | How many rotated logs to keep |
| `notificationsEnabled` | `true` | Desktop notifications on or off |
| `verboseAgentLog` | `false` | Also write raw agent output to `logs/agent-verbose.log` |

"Launch at sign-in" is deliberately not copied into settings.json. The tray reads the
current user's `Run` registration together with Windows' `StartupApproved` decision.
It never writes `StartupApproved`; a system disable is preserved when moving/updating
the executable. This is an observed registration state, not a promise about OS policy
or the next sign-in.

### Copy diagnostics

Produces something like:

```
Tray:           0.1.0
OS:             Microsoft Windows NT 10.0.26100.0 (Arm64)
State:          Online
Agent:          running
Last connected: 2026-01-01 13:52:04
Restart count:  0 (total 0)
...
```

The report contains structural state, timestamps, restart counts and startup status.
It excludes log tails (including logs written by older versions), account/device IDs,
command arguments, raw error strings, verification URLs and codes. Use **Open logs**
locally for operational detail; verbose output is a separate explicit opt-in.

## Security boundaries

The tray:

- does not read or manage OAuth credentials;
- does not read or write `device.json`;
- does not implement OAuth;
- does not implement the Remote MCP protocol;
- does not execute commands received from the network.

The official Desktop Commander keeps authentication, device identity, the Remote MCP
connection, MCP command execution and all credentials. The tray only reads the CLI's
own stdout and stderr to work out what to draw.

CLI status parsing is version-specific, best-effort observation rather than an
independent server health check. Tool/result frames and JSON payloads are excluded
before status normalization. The tray is not a security sandbox for the commands
Desktop Commander executes.

Each agent is created atomically inside its own Windows Job Object. Creation or cleanup
failure stops recovery instead of falling back to an uncontained process. Normal
process descendants are covered; processes deliberately launched outside the job
through another Windows service are not a sandbox guarantee.

## Building from source

```powershell
dotnet test RemoteCommanderTray.sln
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-x64   -o publish/win-x64
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-arm64 -o publish/win-arm64
```

Each publish produces a single self-contained `RemoteCommanderTray.exe` (~60 MB).
Trimming stays off: WinForms is not trim-safe.

The full solution tests require Windows and run on native x64 and ARM64 CI with .NET 8.
The portable core remains testable on Linux/macOS:

```sh
dotnet test tests/RemoteCommanderTray.Core.Tests/RemoteCommanderTray.Core.Tests.csproj
```

See [verification coverage](docs/verification.md) for automated cases, safeguards, and
manual release acceptance that is not implied by a green build.

### Layout

| Project | Target | Contents |
| --- | --- | --- |
| `src/RemoteCommanderTray.Core` | `net8.0` | Log parsing, state machine, supervisor, settings, logging, diagnostics |
| `src/RemoteCommanderTray` | `net8.0-windows` | WinForms tray UI, real process launching, job object, registry startup |
| `tests/RemoteCommanderTray.Core.Tests` | `net8.0` | Deterministic lifecycle, parsing, privacy and settings tests |
| `tests/RemoteCommanderTray.Windows.Tests` | `net8.0-windows` | Actual Job Objects, registry adapters and STA tray controls |
| `tests/RemoteCommanderTray.ProcessProbe` | `net8.0` | Test-only synthetic children, no network or credentials |

The split is the point: status parsing and state rules are portable and unit-tested,
and the Windows-only code stays thin.

`src/RemoteCommanderTray/app.ico` is the repository's only binary file, and it is
generated by `tools/generate-icon.py`; CI fails if the committed icon stops matching the
script. Tray status icons are drawn at runtime instead, so they follow the current DPI.

## Not in v0.1

No Desktop Commander GUI, file manager, terminal, MCP configuration editor, chat UI,
account management, auto-updater or multi-device dashboard. macOS and Linux are not
supported yet.

## License

MIT. See [LICENSE](LICENSE).
