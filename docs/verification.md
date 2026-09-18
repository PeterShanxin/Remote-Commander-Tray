# Verification

## Automated suite

Run on Windows with the .NET 8 SDK:

```powershell
dotnet test RemoteCommanderTray.sln -c Release
dotnet test RemoteCommanderTray.sln -c Debug
```

The core suite has 122 cases. Windows integration adds 12 cases covering actual
process/job lifetime, isolated registry adapters and STA WinForms tray controls.
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

## Local evidence

The fix revision was exercised on Windows ARM64 with SDK 9.0.318 / runtime 9.0.20,
using DOTNET_ROLL_FORWARD=Major for the net8 test assemblies. Release and Debug both passed all
122 core and 12 Windows cases. Exact .NET 8 verification belongs to the CI result,
not this local roll-forward run. No real device, OAuth flow or production startup
entry was launched/changed by the tests. Test children are synthetic and job-scoped.

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
