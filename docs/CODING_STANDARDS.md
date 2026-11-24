# Coding standards

Use the conventions in [.editorconfig](../.editorconfig) and [Directory.Build.props](../Directory.Build.props). Keep changes small enough to review and keep production dependencies unchanged unless a change has a clear need.

## Architecture and contracts

- For Windows, keep parsing, lifecycle and state in Core; keep Windows process, registry and startup APIs in the Windows layer. The UI renders state and issues commands rather than owning the supervisor lifecycle. Linux Beta delegates process ownership and restart to the per-user systemd service; its desktop and command-line controls use that same service.
- Keep one owner for a process generation, timer or state transition. Preserve cancellation and ownership across asynchronous callbacks. Explain why when a lock, retry or timeout is necessary.
- Normalize and validate persisted settings at the boundary. Missing or older values must still load as intended. Do not change user settings, process ownership, startup registration or package identity as incidental cleanup.
- Keep the official Desktop Commander CLI responsible for authentication, device identity and the Remote MCP connection. Do not parse its credential store or include arbitrary CLI output, tool arguments, results, authorization codes, or URLs in ordinary logs or copyable diagnostics. Use the existing [design notes](design-notes.md) for the trust and logging boundaries.

## Maintainability

- Prefer small, typed boundaries over string signaling, mutable global state or one-off conventions. Introduce reusable code when a second use or a clear test seam justifies it.
- Keep UI text separate from raw CLI output and diagnostic labels. If adding localization, key authored text by stable identifiers, not English sentences. Define fallback, and verify the effective Windows UI language in the shipped configuration. Do not change the runtime globalization mode or package layout without separately verifying the effect.
- Handle expected failures at the boundary that can explain or recover from them. Give users a clear next action when one exists. Do not add warning suppressions to make a check pass.
- Compare touched hotspots against the [measured baseline](MAINTAINABILITY_BASELINE.md). Separate unrelated refactors and do not apply a line-count quota mechanically.

## Verification

Write regression tests for the observed behavior, not just private implementation details. Use synthetic CLI events and isolated settings by default. Cover meaningful failure modes and changes across boundaries, such as retry versus stop or saving versus reloading a setting. Run and report the applicable layers in [Verification](verification.md). For Windows UI changes, confirm the tray, status and dialogs on Windows when the change needs a real interaction check. Verify standalone, MSIX and both architectures only to the extent affected, and state coverage explicitly.
