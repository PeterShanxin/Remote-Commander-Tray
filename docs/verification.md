# Verification

## Automated suite

Run on Windows with the .NET 8 SDK:

```powershell
dotnet build RemoteCommanderTray.sln -c Release
dotnet test RemoteCommanderTray.sln -c Release --no-build
dotnet run --project tests/RemoteCommanderTray.Windows.Integration -c Release --no-build
dotnet build RemoteCommanderTray.sln -c Debug
dotnet test RemoteCommanderTray.sln -c Debug --no-build
dotnet run --project tests/RemoteCommanderTray.Windows.Integration -c Debug --no-build
```

The core suite has 194 cases. The separate Windows.Integration harness retains 10
native scenarios, including owner-crash cleanup and environment inheritance. Windows
tests add 13 cases covering actual process/job lifetime, isolated registry adapters
and STA WinForms tray controls. Build the solution before running the standalone
integration harness; `dotnet test` alone does not build that executable.
CI runs the full suite on native x64 and ARM64 Windows with .NET 8, then publishes
both architectures. Publishing alone is not counted as native runtime verification.

Coverage includes synchronous Start output/exit, queued stale exit/session callbacks,
late output from replaced generations, stop/retry/dispose races, failed logout,
terminal authentication loss, bounded output, raw/escaped payload omission and
historical log exclusion from diagnostics. Windows cases check containment from the
first instruction, rapid root exit, surviving children, unrelated group isolation,
one-shot timeout/cancellation, concurrent UTF-8 streams and synthetic tray commands.
Startup tests write only HKCU\Software\RemoteCommanderTray.Tests\<random-id> and remove
that private test key. They never modify the real Run or StartupApproved entries.

Timestamped CLI 0.3.1 fixtures cover status and authentication on both output streams,
reconnect/recovery without process replacement, ANSI decoration, and a ready device
remaining Online after the five-minute stall deadline. Negative cases retain JSON,
quoted strings, arrays, unknown or malformed bracket prefixes, and multiline tool
results as payload. Authentication details and tool content remain absent from logs.

## Local evidence

The timestamp-prefix fix was exercised on 2026-11-07 on Windows ARM64 with SDK
8.0.427 / runtime 8.0.33, using a task-local SDK without major-version roll-forward.
The same 27 focused cases first produced 26 failures and one pass against the old
parser, including the ready device becoming Error after the simulated stall deadline;
all 27 passed after the fix. Release and Debug each passed all 194 core cases, 13
Windows tests and 10 native integration scenarios. The generated icon matched the
committed file. Native x64 execution remains a separate CI check for this revision;
cross-publishing an x64 executable does not prove native x64 runtime behavior.

No real Remote Device, OAuth flow or production startup entry was launched/changed
by the tests. Test children are synthetic and job-scoped. Real installed-tray UI,
authentication and sign-in acceptance still require the manual checks below.

## Manual release acceptance

These are not implied by unit tests or synthetic control tests:

- Stop/disable the previous scheduled task or manual CLI before handing over to tray.
  Verify only one official device is online, and retain a way to restore the old launcher.
- Enable sign-in launch, disable it in Windows Startup Apps, inspect the tray's observed
  state, explicitly re-enable it in Windows, then verify an actual Windows sign-in.
- Test a real browser device authorization, canceled/expired prompts, reconnect after
  sleep/network loss, tray icon/DPI/keyboard interaction and notification click behavior.
- Exit and confirm the owned CLI group has no surviving children. Do not blanket-kill
  Node or processes belonging to other work.

Ready for review means the recorded automated checks and code review passed; it is
not a claim that real reboot, OAuth or every hardware configuration was tested.
