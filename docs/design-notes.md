# Design notes

## Boundaries

C# / WinForms remains a thin tray and process supervisor. Core owns parsing and state;
Windows owns process groups, registry access and UI. No new production dependencies.
The official Desktop Commander CLI owns OAuth, device identity and remote commands.
The tray does not read device.json. Raw CLI output may contain private tool data.

## Generation lifecycle

Commands serialize on a semaphore. Output identity checks, parsing and state updates
share a short callback lock. Delayed exits carry their source generation and recheck
identity after acquiring the semaphore. No process teardown runs under the callback
lock. Register Starting and the process identity before Start can emit callbacks.
Queued callbacks are observed, canceled or awaited during shutdown.

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
Terminal session loss or an expired authorization prompt pauses for explicit user
re-authentication with one actionable notice, rather than looping browser launches.
A failed logout cannot be presented as successful sign-in or start a replacement.
Notification clicks resolve the current prompt, not an old cached authorization URL.

## Logs and diagnostics

Ordinary agent.log stores canonical status categories, not raw recognized suffixes.
Unknown output, tool arguments/results, codes and authorization URLs are omitted.
The parser rejects JSON/result frames before normalization and keeps stdout/stderr
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
