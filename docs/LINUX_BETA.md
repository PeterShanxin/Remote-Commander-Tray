# Remote Commander Tray · Linux Beta

Runs the official Desktop Commander Remote agent in the background under your
user account. Open the Linux desktop control window to start, stop, restart and
see whether the service is running. It is a window, not a system tray icon.
The window does not indicate whether Desktop Commander itself is connected.

## Install

You need Linux with a running systemd user session, Python 3.9 or later at
`/usr/bin/python3`, and Node.js 22.12 or later. Install your distribution's Tk
package (Ubuntu: `python3-tk`) for the desktop window; headless use does not need
Tk. No admin access is needed to install the app. Automated tests cover Ubuntu
24.04 x64. Other distributions and architectures are untested.

1. Install the official CLI with `npm install -g @wonderwhy-er/desktop-commander`.
2. Pair this machine in a terminal with `desktop-commander remote` using the
   [official instructions](https://desktopcommander.app/help/devices/connect-computer).
   Stop the terminal agent after pairing. If you have another launcher (shell
   script, service, or scheduled job), stop and disable it first.
3. [Download the v0.3.0 Linux Beta archive](https://github.com/PeterShanxin/Remote-Commander-Tray/releases/download/v0.3.0/RemoteCommanderTray-linux-beta.tar.gz).
   Extract it and run `./remote-commander-tray install` from its folder as your regular user. This remembers where
   the CLI and Node are installed, including user-specific npm installs. Rerun
   install if you move or replace Node or the CLI.
4. Open **Remote Commander Tray (Linux Beta)** from the application menu and
   click **Start**. Choose **Enable startup** if you want it to start with your
   user systemd session. Closing the window does not stop the service.

The Linux service retries failures with a delay, up to four starts in five
minutes. If systemd stops retrying, check the official CLI in a terminal and
then start the service again. Stop the service first before launching the CLI
manually, including to pair again. The companion does not read or delete
Desktop Commander credentials. It drops the agent's output rather than putting
pairing codes or command results in the journal. Standard systemd service
metadata remains available. There is no new sandbox for remote commands.

## Headless controls

Run the same controls without opening the desktop window:

```sh
app="$HOME/.local/share/remote-commander-tray/remote-commander-tray"
"$app" status
"$app" start       # or stop, restart
"$app" enable      # or disable
"$app" uninstall
```

Normal user session lifetime applies. This app does not change systemd linger
settings. Uninstall stops and removes the companion without signing you out of
Desktop Commander.

## Build and test

From the repository root, `bash tools/package-linux-beta.sh` writes
`publish/RemoteCommanderTray-linux-beta.tar.gz`. The [CI workflow](../.github/workflows/ci.yml)
builds the archive and uses a synthetic CLI in a disposable Ubuntu runner to
exercise the actual systemd service and desktop entry. It does not exercise an
account, OAuth, or a real connection. The separate portable .NET Core suite
validates shared library behavior, not any Linux or Windows desktop UI.
