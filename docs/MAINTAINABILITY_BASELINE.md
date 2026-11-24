# Maintainability baseline

Measured on [aeef498](https://github.com/PeterShanxin/Remote-Commander-Tray/commit/aeef498f75bdc02b3fb90b35189aff6784154f14) on 2026-11-24: **28 production C# files, 3,883 physical lines**. The scope is tracked files ending in `.cs` under `src/`. Counts include blanks and comments and exclude tests, manifests and generated build output.

| Production file | Physical lines |
| --- | ---: |
| [TrayApplicationContext.cs](../src/RemoteCommanderTray/UI/TrayApplicationContext.cs) | 573 |
| [AgentSupervisor.cs](../src/RemoteCommanderTray.Core/AgentSupervisor.cs) | 501 |
| [AgentOutputParser.cs](../src/RemoteCommanderTray.Core/AgentOutputParser.cs) | 366 |
| [AgentStateMachine.cs](../src/RemoteCommanderTray.Core/AgentStateMachine.cs) | 289 |
| [WindowsAgentProcess.cs](../src/RemoteCommanderTray/Windows/WindowsAgentProcess.cs) | 230 |

To reproduce from the repository root, count physical lines in the pinned revision. This PowerShell example reads Git objects without modifying the worktree:

```powershell
$ref = 'aeef498f75bdc02b3fb90b35189aff6784154f14'
$rows = git ls-tree -r --name-only $ref -- src |
    Where-Object { $_ -like '*.cs' } |
    ForEach-Object {
        $path = $_
        [pscustomobject]@{ Path = $path; Lines = @(git show "${ref}:$path").Count }
    }
$rows | Sort-Object Lines -Descending | Select-Object -First 5
$rows | Measure-Object -Property Lines -Sum
```

Treat these figures as review signals, not limits or a claim that the repository is debt-free. If changes make a hotspot harder to understand, explain the added responsibility or move it to the natural owner. Record a new baseline only with a dated, reproducible measurement and a reason. Do not restructure a file just to lower its count.
