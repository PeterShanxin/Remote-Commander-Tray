# Remote Commander Tray

A very small Windows system-tray companion that runs and supervises the official
[Desktop Commander](https://github.com/wonderwhy-er/DesktopCommanderMCP) Remote Device.

It does not reimplement Desktop Commander or manage its credential store.
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
- Owns at most one managed agent generation, and drains its descendants on Stop or Exit.

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
from its own Startup Apps page. Use that page to re-enable it. Unknown startup states
also direct you to Windows settings.

## Tray states

Each state has its own shape as well as its own colour, so it stays readable without
relying on colour alone.

| Icon | State | Meaning |
| --- | --- | --- |
| Check | Online | Device registered and marked online |
| Dots | Connecting / Reconnecting | Starting up, or re-establishing a dropped channel |
| Key | Authentication required | Sign-in is pending, or an expired session needs Sign in again |
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
3. starts `desktop-commander remote` again only if logout succeeded;
4. shows the sign-in page and code that the official CLI prints.

An expired running session produces one notification and a **Sign in again...** action;
it does not open browsers repeatedly. A failed logout leaves the agent stopped.

The tray never reads, writes, copies or parses `device.json`, and never implements any
part of the OAuth flow.

## Files

Everything the tray writes lives in `%LOCALAPPDATA%\RemoteCommanderTray\`:

```
settings.json
logs\agent.log            (rotated at 1 MB, 3 files kept)
logs\agent-verbose.log    (only when verboseAgentLog is on)
```

`agent.log` contains tray lifecycle messages plus generated operational event names,
not raw CLI lines. Tool parameters, results, names and unrecognized text are omitted,
including short plain text and nested escaped JSON. Regex redaction is defense in depth,
not a guarantee over arbitrary tool output.

`verboseAgentLog` opts into a separate sensitive debug log. It can contain credentials
and anything a remote tool read. Never share it without inspecting it. Diagnostics never
includes that file, and older existing logs are not retroactively sanitized.

### settings.json

| Key | Default | Meaning |
| --- | --- | --- |
| `startAgentOnLaunch` | `true` | Start the agent when the tray starts |
| `agentExecutable` | `null` | Override with an absolute CLI executable path |
| `agentArguments` | `null` | Arguments inserted before `remote` |
| `remoteMcpUrl` | `https://mcp.desktopcommander.app` | Opened by "Open Remote MCP" |
| `stalledConnectionMinutes` | `5` | How long a stuck reconnect runs before you are told |
| `startFailureAlertThreshold` | `3` | Consecutive failed starts before you are told |
| `healthyRunSeconds` | `60` | A run this long resets the restart backoff |
| `logMaxBytes` | `1048576` | Rotate `agent.log` past this size |
| `logRetainedFiles` | `3` | How many rotated logs to keep |
| `notificationsEnabled` | `true` | Desktop notifications on or off |
| `verboseAgentLog` | `false` | Also write raw agent output to `logs/agent-verbose.log` |

"Launch at sign-in" is not duplicated in settings.json. It reads the Run registration
and Windows' separate StartupApproved decision. A disabled entry stays disabled; an
unrecognized or unreadable state is shown as unknown. Re-enable through Windows Startup
Apps, not by overwriting its approval record. Process containment is always required.

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

Only a structural operational summary is copied. Command arguments, account/device
fields printed by the CLI, free-form errors and all log tails are excluded, even when
an older build left sensitive text in an existing log.

## Security boundaries

The tray:

- does not read the credential store or implement token persistence;
- does not read or write `device.json`;
- does not implement OAuth;
- does not implement the Remote MCP protocol;
- does not execute commands received from the network.

The official Desktop Commander keeps authentication, device identity, the Remote MCP
connection, MCP command execution and all credentials. The tray only reads the CLI's
own stdout and stderr to work out what to draw.

Agent output is treated as untrusted input. Known tool-log envelopes are recognized
before normalized status prefixes. This is a compatibility parser, not a structured
status protocol. Ordinary logging and diagnostics exclude payloads; explicitly enabled
verbose logging may retain sensitive tool output as described above.

## Building from source

```powershell
dotnet test RemoteCommanderTray.sln
# Windows only: real process-tree and isolated startup-registry checks
dotnet run --project tests/RemoteCommanderTray.Windows.Integration -c Release
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-x64   -o publish/win-x64
dotnet publish src/RemoteCommanderTray/RemoteCommanderTray.csproj -c Release -r win-arm64 -o publish/win-arm64
```

Each publish produces a single self-contained `RemoteCommanderTray.exe` (~60 MB).
Trimming stays off: WinForms is not trim-safe.

`dotnet build` and `dotnet test` also work on Linux and macOS hosts - `EnableWindowsTargeting`
lets the desktop targeting pack restore anywhere, and the test project covers only the
platform-neutral core.

### Layout

| Project | Target | Contents |
| --- | --- | --- |
| `src/RemoteCommanderTray.Core` | `net8.0` | Log parsing, state machine, supervisor, settings, logging, diagnostics |
| `src/RemoteCommanderTray` | `net8.0-windows` | WinForms tray UI, real process launching, job object, registry startup |
| `tests/RemoteCommanderTray.Core.Tests` | `net8.0` | Core and lifecycle/privacy regression tests |

| `tests/RemoteCommanderTray.Windows.Integration` | `net8.0` | Native process-tree and isolated registry integration checks |

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
