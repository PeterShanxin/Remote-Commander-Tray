# Agent Guide

Remote Commander Tray is a Windows tray and process supervisor for the official Desktop Commander Remote Device. It uses C# 12, .NET 8 and WinForms. Maintain the Windows x64 and ARM64 paths.

## Source of truth

Use the nearest applicable repository instructions for workflow. For implementation rules, see [Coding standards](CODING_STANDARDS.md). For current behavior and trust boundaries, see [Design notes](design-notes.md) and confirm against source and tests. For commands and gates, use the [verification guide](verification.md) and [CI workflow](../.github/workflows/ci.yml). The [Store guide](STORE.md) and [Store workflow](../.github/workflows/store.yml) cover Store packaging. If sources disagree, report the mismatch instead of silently choosing one.

| Path | Owns |
| --- | --- |
| [Core](../src/RemoteCommanderTray.Core) | CLI parsing, state, supervision, settings and diagnostic policy |
| [Windows](../src/RemoteCommanderTray/Windows) | Native process containment and startup registration |
| [UI](../src/RemoteCommanderTray/UI) | Tray menu, status, notifications and dialogs |
| [Tests](../tests) | Core, Windows controls and native process verification with synthetic agents |
| [Packaging](../packaging) and [tools](../tools) | MSIX manifest, packaging, verification and generated assets |

## Work and verify

1. Check the branch and working tree. Use a fresh clone or a separate worktree for concurrent changes. Preserve edits that did not come from your task.
2. Define the user-visible behavior and the layer that owns it. For a bug fix, first identify a case that fails without the fix.
3. Use the relevant commands in [Verification](verification.md). Distinguish Windows x64 and ARM64 CI from manual acceptance. Core tests and compilation do not verify real OAuth, startup or the installed Store package.
4. Write a focused change. Follow the [writing guide](WRITING_GUIDE.md) and describe the checks you ran and the checks you could not run.
5. Leave the installed tray, accounts and live processes alone unless the owner has authorized an isolated live test. Use synthetic agents and disposable settings instead.

A change to the portable version, Store identity or release workflow is a separate release decision. Check the applicable workflow before proposing that change. Do not merge, publish or change distribution settings without authorization.

Update this guide when the workflow or source of truth changes. Keep incident history and task notes in issues or PRs, not here.
