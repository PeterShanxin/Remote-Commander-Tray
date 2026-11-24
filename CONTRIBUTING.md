# Contributing

Small fixes, reproducible bug reports and translations are welcome. Fork the repository, branch from current `main`, and keep each PR focused on one change. If the change would alter the app's behavior or architecture, discuss the scope in an issue first.

For Windows or Core source changes, use the .NET 8 SDK. For Linux Beta, use Python and the Ubuntu checks in [Verification](docs/verification.md). Use a disposable settings folder and synthetic agent where possible. Do not needlessly restart your installed tray. Share the commands and observed results in the PR, and note when you could not test an architecture or package.

Follow the [coding standards](docs/CODING_STANDARDS.md) and [writing guide](docs/WRITING_GUIDE.md). For a bug report, include the app version, CLI version, OS version and architecture, expected behavior and what happened. Do not post credentials, authorization codes or URLs, tool results or raw logs. [Privacy](docs/PRIVACY.md) describes what the app stores.
