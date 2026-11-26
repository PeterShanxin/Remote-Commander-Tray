# Design notes

## Platform boundaries

The official CLI owns OAuth, device identity and remote commands on both platforms.
The Windows tray uses C# and WinForms. Core owns its parsing and state; Windows owns
its process groups, registry access and UI.

Linux Beta is a separate Python control window and command line, not a WinForms port.
Both control the same per-user systemd service. The service owns startup, bounded retry
and cgroup cleanup. It does not parse CLI output or claim that a running service is
connected. It does not keep agent output. See the [Linux Beta guide](LINUX_BETA.md).

## Windows implementation
The Windows tray does not read device.json. Raw CLI output may contain private tool data.

## Generation lifecycle

Commands serialize on a semaphore. Output identity checks, parsing and state updates
share a short callback lock. Delayed exits carry their source generation and recheck
identity after acquiring the semaphore. No process teardown runs under the callback
lock. Register Starting and the process identity before Start can emit callbacks.
Queued callbacks are observed, canceled or awaited during shutdown.
The tray shows Stopping during process-group cleanup. The UI reads the current
status when it handles a queued update so an older Online event cannot replace
a newer stop.

Each real process is created with PROC_THREAD_ATTRIBUTE_JOB_LIST and HANDLE_LIST:
it is contained from its first instruction and inherits only its stdio handles, not
the job's owning handle. One kill-on-close Job Object belongs to each generation.
Natural root exit, Stop, Dispose and the logout one-shot terminate and drain that
job before another generation can start. Root exit alone is insufficient. Creation
or cleanup failures fail closed; there is no uncontained fallback setting.

The containment boundary is ordinary descendants, not a security sandbox for arbitrary
commands. The single-instance mutex coordinates tray copies in the same user session;
pre-existing scheduled tasks and manual CLI launchers require the documented handover.

## Recovery and authentication

Unexpected exits retry at 5s, 15s, 30s and 60s. Stop cancels retry and countdown.
Ordinary network errors only change state; the official CLI owns channel recovery.

CLI 0.3.1 checks the channel every 10 seconds and refreshes the server's last-seen
time every five minutes when reachable. It prints “Device marked as online” only
when the status changes. Successful repeated writes and socket heartbeat replies
produce no regular terminal signal in normal mode. A silent process may be healthy.

The Windows tray's hidden top-level window records whether the device was Online
when Windows announced suspend. On automatic resume from sleep or hibernation,
that same online report becomes **Status unconfirmed**. A fresh online signal
clears the pending sleep marker under the state lock, even if the process changed
or the clock reads the same. A late or duplicate resume cannot undo fresh status.
Screen unlock alone has no effect. A recognized CLI status updates the icon
immediately. Tool output and device metadata cannot confirm the connection.

For a quiet, still-running agent, the supervisor's existing 15-second health check
restores Online after `wakeGraceSeconds` (default 180, minimum 90). CLI 0.3.1
attempts to recreate a joined channel when its last confirmed heartbeat is over
75 seconds old. Its 10-second check interval gives a nominal detection window
of 85 seconds. The default allows a further 95 seconds for scheduling delays.
A second sleep during this window starts a new grace period on the next wake.

Grace expiry changes the displayed state without recording a new connection,
restarting the agent or notifying the user. The healthy-run backoff reset still
uses the original online interval. A reported disconnect starts the normal
reconnect interval and can raise the existing stalled-connection notice.

Terminal session loss or an expired authorization prompt pauses for explicit user
re-authentication with one actionable notice, rather than looping browser launches.
A failed logout cannot be presented as successful sign-in or start a replacement.
Exit cancels an active logout and waits for its process group to be cleaned up.
If either group fails cleanup, the tray reports the error and blocks further launches.
Notification clicks resolve the current prompt, not an old cached authorization URL.

## Logs and diagnostics

Ordinary agent.log stores canonical status categories, not raw recognized suffixes.
Unknown output, tool arguments/results, codes and authorization URLs are omitted.
The parser strips ANSI escapes and one exact leading ASCII `[HH:mm:ss] ` clock
emitted by CLI 0.3.1 before checking for JSON/result frames. Other bracket prefixes
remain payload; removing a clock never removes a following JSON delimiter. Plain
status lines from older CLI versions remain supported. The parser keeps stdout/stderr
multiline prompt state separate. This is version-specific observation, not independent
server-health verification or an authentication/security boundary.

Copy diagnostics includes only structural state and counters. It never appends log
tails, including older unsafe files, or arbitrary identifiers/commands/error strings.
Optional verbose output is a separate sensitive debug file, off by default.
Regex scrubbing is defense in depth, never the guarantee over arbitrary tool output.
Native line buffers and one-shot output capture are bounded.

## Startup

Read Run registration plus observed StartupApproved state. Known disabled states stay
disabled across path refresh. Unknown/malformed/unreadable states stay Unknown, not
Enabled. Never write StartupApproved; offer Windows Startup Apps for re-enabling.
Only the user's Run command is changed when explicitly toggling registration.

## Verification and limits

See [verification.md](verification.md). Automated Windows tests use actual native
process groups, isolated registry keys and STA tray controls, but synthetic agents.
Real browser OAuth, Windows sign-in/reboot and manual visual acceptance remain separate
release checks. Stop force-terminates the owned group; remote offline status converges
through the official service's heartbeat rather than graceful CLI shutdown.
