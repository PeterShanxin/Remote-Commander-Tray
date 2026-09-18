# Design and verification notes

## Scope

The tray stays a Windows-only WinForms companion, not a replacement for Desktop
Commander. The platform-neutral Core owns parsing, state and lifecycle policy. The
Windows adapter owns native process containment and startup registration. The UI only
renders snapshots and routes deliberate user actions.

No new runtime packages, browser engine, Windows service, background helper daemon or
OAuth implementation are introduced by the review fixes.

## Process ownership

A supervisor semaphore serializes Start, Stop, Restart, Re-authenticate, and every exit
or output callback. Each callback carries its originating process and revalidates that
identity after acquiring the semaphore. Publishing the generation and Starting state
before `Start()` means synchronous or very fast callbacks cannot be discarded or have
their state overwritten. stdout and stderr have separate parsers so a partial sign-in
prompt on one stream cannot consume unrelated output from the other.

Each remote command and each one-shot logout gets a separate Job Object. The Windows
adapter uses `PROC_THREAD_ATTRIBUTE_JOB_LIST` to put the process in its job atomically
at creation, with breakaway disabled. `CREATE_SUSPENDED` allows streams and observation
to be wired before executing user code. `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` restricts
inheritance to the three standard streams; the job handle is never inherited.

This avoids both the fast-child escape and tray-crash gap in a create-then-assign
sequence. Failure to create the containment is fatal to that start. There is no
`requireJobObject=false` escape hatch.

On root exit, Stop, Restart, logout completion, timeout, cancellation or disposal, the
adapter terminates the owned job and waits for its active-process count to reach zero.
Only then may the supervisor replace the generation. Checking `Process.HasExited` is
not sufficient: descendants may still be alive. A failed drain blocks replacement and
requires an explicit Stop retry. Closing the owned handle is the final kill-on-close
backstop when the tray exits or crashes. No process-name-wide termination is used.

Output lines are capped at 16 KiB; oversized lines are omitted and parsing resumes at
the next newline. One-shot captured output is bounded as well.

References for the native boundary:
- [Create a process directly in a job](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812)
- [Process/thread attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [TerminateJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-terminatejobobject)

## Recovery and sign-in

| Event | Behavior |
| --- | --- |
| Unexpected process exit | Drain generation, then retry at 5/15/30/60 seconds |
| User Stop | Drain and stay stopped |
| Temporary channel/network failure | Update status; leave reconnect to the official CLI |
| Terminal `Remote session expired and could not be renewed` | Latch Authentication required; notify once; wait for user sign-in |
| Late Online after terminal session loss | Ignore it until a new generation starts |
| Re-authenticate | Drain old agent, run official logout, then start a new agent only if logout succeeded |
| Failed/timed-out logout | Show an error; do not silently restart with old credentials |
| Process cleanup not confirmed | Block replacement rather than risk duplicate agents |

Terminal session loss does not itself start an automatic browser/restart loop. The
notification and `Sign in again...` menu action lead to the existing confirmation and
official CLI flow. The tray never opens or edits `device.json`. Local logout is not the
same as server-side revocation; revocation remains in the official device dashboard.

Status parsing is based on official CLI 0.2.51 output. Online is the latest status
reported by that CLI, not an independent end-to-end proof that a ChatGPT request will
succeed. Unknown future output cannot be treated as confirmed connectivity.

## Ordinary logging and clipboard diagnostics

Arbitrary CLI output may contain tool results, including opaque credentials, short
plain text or nested JSON. A recognized status prefix can also have sensitive text
appended. Regexes alone cannot safely classify such input.

`AgentLogPolicy` therefore emits only generated event names and omission markers. It
never stores a raw CLI line, tool name, URI, code, error payload or `AgentSignal.Value`
in the ordinary operational log. `SecretRedactor` is secondary protection, including
for escaped keyed secrets, not the confidentiality guarantee.

`Copy diagnostics` includes structural state, version/OS, process status, counters and
timestamps. It excludes command arguments, CLI-derived account/device fields, free-form
errors and **all log tails**, including legacy logs from older builds.

`verboseAgentLog` is a separate, explicit sensitive-data opt-in. Its file may contain
raw tool results and credentials; never include it automatically in a bug report. Both
logs rotate. Existing files from older builds are not silently deleted or claimed to
have been retroactively sanitized.

## Startup and handover

The named mutex coordinates tray instances only. It cannot prevent an independent
terminal, scheduled task or another user/session from launching the official CLI.
Disable and stop the old launcher before adopting this tray. Do not run both supervisors
against the same device. The tray never searches for arbitrary Node processes to kill.

Startup state combines the Run command with a **read-only** observation of Windows'
StartupApproved record. Only known complete 12-byte states are interpreted. Unexpected
formats, access errors or flags produce Unknown rather than an enabled checkmark. This
registry format is undocumented, so this is an observation, not a promise about every
Windows startup policy.

A Windows-disabled entry stays disabled when its executable path is refreshed. The UI
opens `ms-settings:startupapps` for Windows-disabled or unknown states; it never writes
StartupApproved to override an external user choice. No startup state is duplicated in
settings.json.

## Automated verification

Core regressions cover immediate output/exit, exit and auth callbacks already queued
behind a restart, terminal-session notification/latching, failed logout, failed cleanup,
creation retry, disposal and log/clipboard privacy. These are stateful boundary tests,
not just string matching tests.

`tests/RemoteCommanderTray.Windows.Integration` compiles the actual production Windows
adapter and runs isolated synthetic executables. It checks live UTF-8 output, no console,
environment inheritance, root-exit cleanup, Stop/Dispose, job isolation, owner crash,
timeout/cancellation, fast exits, output bounds and disposable test registry records.
It does not start Desktop Commander, read credentials or alter real startup settings.

Run on Windows:

```powershell
dotnet build RemoteCommanderTray.sln -c Release
dotnet test tests/RemoteCommanderTray.Core.Tests -c Release --no-build
dotnet run --project tests/RemoteCommanderTray.Windows.Integration -c Release --no-build
```

CI runs .NET 8 tests and native integration on hosted x64 and ARM64 Windows, then builds
both self-contained distributions. Local testing on the maintainer's ARM64 machine used
.NET 9 with explicit `DOTNET_ROLL_FORWARD=Major`; the .NET 8 CI result remains a separate
required verification, not something inferred from the local run.

## Remaining manual acceptance before a public release

Automated tests do not replace a real sign-in and desktop acceptance pass. Verify tray
menu interactions, actual browser authorization, sleep/resume and a Windows sign-in
cycle after a deliberate handover from the previous launcher. No such handover or real
authorization was performed as part of this review patch.

Stop is a bounded job termination, not a graceful Ctrl+C. Remote presence can take time
to expire after stopping even though local access is already cut off. The single-file
self-contained distribution includes .NET; package size is not the tray's idle memory
usage. No memory or long-duration soak benchmark is claimed here.
