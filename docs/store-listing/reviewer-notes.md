# Certification notes

This is an independent open-source tray companion, not a repackaged remote service.
It requires `runFullTrust` because it is a WinForms desktop tray app that creates a
Windows Job Object and launches/manages the user's installed official CLI. It does
not install a service, driver, scheduled task, browser extension, or new credential.
It runs at the signed-in user's privilege level, without elevation.

Prerequisites are disclosed at the start of both listing descriptions. The app
does not bundle or automatically download Node.js or the official Desktop Commander
CLI. The .NET runtime is included in the MSIX payload.

Test with Node.js 22.12+ and `npm install -g @wonderwhy-er/desktop-commander` installed
before starting the companion. Use your own authorized Desktop Commander Remote
test account; no shared login or developer credentials are provided.

1. Launch the app. Its icon appears in the notification area or hidden-icons overflow.
2. If the CLI is absent, an agent-unavailable notification explains the prerequisite;
   the app remains available in the tray and does not download it automatically.
3. With the CLI installed, follow the official sign-in flow from the tray menu.
4. Confirm Online, then exercise Stop agent, Start agent and Restart connection.
5. Enable Launch at sign-in. Disable it in Windows Startup Apps and confirm the app
   reports the external disable instead of overriding it.
6. Open logs and Copy diagnostics. Raw tool results and credentials are not included
   in ordinary diagnostics. Verbose raw logging is off by default.
7. Exit stops the managed process group. Uninstall through Windows Settings.

Account state and all remote requests belong to the official Desktop Commander
client/service. This tray never reads its device credential file and does not
implement its authentication or remote command protocol. Existing standalone
launchers must be stopped before the tray is used, to avoid duplicate supervisors.

Updates for this packaged edition are delivered through Microsoft Store.
