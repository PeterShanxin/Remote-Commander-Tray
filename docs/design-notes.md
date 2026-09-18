# Design notes

Background for reviewers: why the pieces are shaped the way they are, how the v0.1
acceptance criteria are met, and what is not yet proven.

## Shape of the code

```
AgentOutputParser   one CLI line  ->  AgentSignal          (no state, no UI)
AgentStateMachine   AgentSignal   ->  AgentSnapshot        (no parsing, no UI)
AgentSupervisor     snapshots + process lifecycle          (no UI)
TrayApplicationContext              renders snapshots      (no parsing, no rules)
```

The spec asked for status logic in one simple state machine, with UI and log parsing
kept apart. That boundary is also what makes the core project target plain `net8.0`,
so the whole status pipeline is unit-tested on any host, including the Linux CI leg.

## Status detection

Signal strings were taken from the source of
`@wonderwhy-er/desktop-commander@0.2.51` (`dist/remote-device/*.js`), not guessed:

| CLI line | Signal | Resulting state |
| --- | --- | --- |
| `🚀 Starting MCP Device...` | `DeviceStarting` | Starting |
| `⏳ Connecting to Remote MCP ...` | `ConnectingToRemote` | Connecting |
| `- ✅ Session restored` | `SessionRestored` | Connecting |
| `🔐 Authenticating with Remote MCP server...` | `AuthenticationStarted` | Authentication required |
| `✅ Device ready:` | `DeviceReady` | Online |
| `🔌 Device marked as online` | `DeviceOnline` | Online |
| `❌ Channel error: ...` | `ChannelDisrupted` | Connecting (no restart) |
| `- ❌ Device startup failed: ...` | `StartupFailed` | Error |

Two details that matter:

- The device authorization flow prints its URL and its user code on the line *after*
  their labels, so the parser carries a two-flag state for exactly that case.
- Matching happens on a normalized line (ANSI codes, emoji, bullets and `1.` markers
  stripped) using `StartsWith`, never `Contains`. Tool-call logging puts
  attacker-influenced text into the same stream; a prefix match on a normalized line
  means that text lands inside `Received tool call ...` and cannot forge a transition.
  There is a test for exactly that.

## Restart policy

| Event | Action |
| --- | --- |
| Process exits and the agent is wanted | Restart after `5s → 15s → 30s → 60s` |
| Process exits after a user Stop | Nothing |
| Channel error / closed / timed out / recreating | Icon changes, process untouched |
| Device marked offline | Icon changes, process untouched |
| Run lasted longer than `healthyRunSeconds` | Backoff resets |
| Reached Online | Backoff resets, failure streak cleared |

Leaving channel problems alone is the whole reason the supervisor stays this simple:
the official device already owns heartbeat, stale-connection detection and channel
recreation, and restarting the process underneath it would only throw away its own
recovery.

`Remote session expired and could not be renewed` is the one connection-level message
that *is* treated as an error, because the CLI itself says the process has to be
restarted to recover. Nothing exits on that path - the CLI stops its heartbeat and stays
alive - so the supervisor acts on the message directly: one deduplicated notification per
generation, then a stop and a restart routed through the normal backoff, so a session that
keeps dropping cannot become a restart loop.

## Exactly one agent

Four independent guards:

1. A named mutex (`Local\RemoteCommanderTray.SingleInstance.<user>`) means one tray per
   signed-in user.
2. Every supervisor entry point serializes on one semaphore, and `StartCore` returns
   early if a live process already exists - so a restart timer and a menu click cannot
   race into two agents.
3. Each generation carries its identity through its own callbacks. An exit callback that
   was queued before a Restart re-checks, under the semaphore, that the process it came
   from is still the current one; output from a replaced generation is dropped the same
   way. Without that check, a queued exit would detach the replacement and start a third
   agent beside it.
4. Every generation gets **its own** `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` job object with
   breakaway disallowed, and that job is terminated when the generation ends - on Stop, on
   Restart, and on natural exit. Killing the tree from the root is not enough: once the
   root has exited there is no common parent left to walk, so a surviving local-MCP
   `node.exe` can only be reached through the job. Kill-on-close remains the backstop for a
   tray that is killed outright.

The mutex coordinates trays, and nothing else. A pre-existing scheduled task or terminal
running `desktop-commander remote` is invisible to it, and the tray deliberately does not
hunt for unrelated Node processes to kill. Handing over from a previous launcher is a
documented manual step.

## What reaches the log

The official CLI logs completed tool calls with `JSON.stringify(result)`, and the
serialized result lands on its own line with no prefix to recognize it by. Scrubbing that
with regexes cannot be sound over arbitrary tool output - a nested
`\"refresh_token\":\"...\"` inside an escaped string defeats a pattern written for plain
JSON, and opaque credentials are not JWT-shaped at all.

So `agent.log` is an allowlist, not a filter:

| Line | What is logged |
| --- | --- |
| Recognized CLI status line | verbatim |
| `Received tool call ...` / `Tool call X completed:` | `tool call X` (name only, and only if it is a plain identifier) |
| The line after `completed:` | `<tool result omitted, N chars>` |
| Anything else | verbatim if short and free of braces, brackets and quotes; otherwise `<agent output omitted, N chars>` |

`SecretRedactor` still runs over everything that survives, including the escaped-JSON key
forms, but as defence in depth rather than as the guarantee. Raw output goes to
`agent-verbose.log` only when the user sets `verboseAgentLog`, and diagnostics never reads
that file.

## Launching the CLI

Preference order:

1. `agentExecutable` from `settings.json`;
2. `node.exe` plus the globally installed `dist/index.js` - no shell, no `.cmd` shim, so
   stdio redirection and tree teardown behave predictably;
3. `desktop-commander.cmd` through `cmd.exe /d /s /c`;
4. `npx -y @wonderwhy-er/desktop-commander@latest`.

Windows path conventions (`\` and `;`) are hard-coded rather than taken from
`Path.Combine`, because the paths being resolved are always Windows paths regardless of
which host runs the tests.

## Acceptance criteria

| # | Criterion | Where |
| --- | --- | --- |
| 1 | Starts at Windows sign-in | `StartupRegistration`, per-user `Run` key |
| 2 | No console window | `CreateNoWindow` + `UseShellExecute = false`, `WinExe` output |
| 3 | Tray always shows the real state | `AgentStateMachine` -> `TrayApplicationContext` |
| 4 | At most one agent | Mutex, supervisor semaphore, generation identity, job object |
| 5 | Crash recovery | `RestartBackoff` + `AgentSupervisor.ScheduleRestart` |
| 6 | Brief network loss does not cause restarts | `ChannelDisrupted` never touches the process |
| 7 | Expired sign-in is obvious | Key icon, balloon, sign-in menu section, session-loss recovery |
| 8 | One-click re-authenticate | `AgentSupervisor.ReauthenticateAsync` |
| 9 | Start / Stop / Restart | Tray menu |
| 10 | Logs and diagnostics | `RollingFileLog`, `DiagnosticsReport` |
| 11 | No leftover agent after Exit | Per-generation job terminated on every end; kill-on-close backstop |
| 12 | Runs on ARM64 Windows | `win-arm64` publish leg in CI |

## Startup state is not one registry value

Turning a startup app off in Windows' Startup Apps page does not delete its `Run` value;
Windows records the decision separately under `Explorer\StartupApproved\Run`, in a binary
value whose first byte has bit 0 set when disabled. Treating the presence of the `Run`
command as "enabled" shows a ticked box for something that will not launch, and rewriting
the `Run` value does not clear the separate decision.

`StartupApproval` resolves the two reads into `NotRegistered` / `Enabled` /
`DisabledByWindows`. The menu shows the effective state, and when Windows has disabled the
entry the tray offers `ms-settings:startupapps` rather than pretending it can re-enable
it. The user's external choice is preserved, including by the path-refresh at launch.

## Known gaps

- **Not yet run on Windows.** Everything compiles for `win-x64` and `win-arm64`, and the
  102 core tests pass, but the tray UI, job object, registry entry and real process
  launching have only been exercised by cross-compilation. Criteria 1, 2, 4, 11 and 12
  need one manual pass on a real machine before v0.1 is called done. In particular the
  job-per-generation teardown and the `StartupApproved` round trip (enable in the tray,
  disable in Windows, reopen the menu, re-enable) want checking on real hardware.
- **Stopping is a kill, not a graceful shutdown.** Without a console there is no way to
  deliver Ctrl+C, so `StopAsync` kills the process tree. The device is marked offline
  server-side by its own heartbeat timeout rather than by its shutdown path. Sending
  `CTRL_BREAK_EVENT` to a process group would be tidier and needs creation flags that
  `System.Diagnostics.Process` does not expose.
- **The restart countdown in the menu does not tick.** It is computed when the menu
  opens, which is the only moment it is visible.
- **Self-contained publish is ~60 MB.** Acceptable for v0.1 per the spec. A
  framework-dependent build would be a few hundred KB if the .NET 8 desktop runtime is
  already present.
