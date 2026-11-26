#!/usr/bin/env bash
# Only on a disposable CI login: this exercises the actual user service and app launcher.
set -euo pipefail
package="$(realpath "$1")"
fixture="$(mktemp -d)"
export PATH="$fixture:$PATH"
supported_home="$HOME"
installed="$supported_home/.local/share/remote-commander-tray/remote-commander-tray"
unit=remote-commander-tray.service
if systemctl --user cat "$unit" >/dev/null 2>&1 || [[ -e "$installed" ]]; then
  echo "An existing installation is present; refusing to modify it." >&2
  exit 1
fi
cleanup() {
  if [[ -x "$installed" ]]; then
    "$installed" uninstall
  fi
}
trap cleanup EXIT

cat > "$fixture/node" <<'FAKE'
#!/bin/sh
if [ "$1" = --version ]; then echo v22.12.0; else exit 2; fi
FAKE
cat > "$fixture/desktop-commander" <<'FAKE'
#!/bin/sh
set -eu
test "$#" = 1
test "$1" = remote
root="$(dirname "$0")"
node --version >/dev/null
printf '%s\n' "$$" >> "$root/agent-pids"
sleep 300 &
printf '%s\n' "$!" >> "$root/child-pids"
echo 'synthetic-stdout-marker-never-log'
echo 'synthetic-stderr-marker-never-log' >&2
wait
FAKE
chmod +x "$fixture"/{desktop-commander,node}
"$package/remote-commander-tray" install
[[ "$("$installed" status)" == 'Stopped | Start with user session: off' ]]
"$installed" enable
[[ "$("$installed" status)" == 'Stopped | Start with user session: on' ]]
"$installed" start

wait_for_children() {
  local target="$1"
  for _ in {1..60}; do
    if [[ -f "$fixture/child-pids" ]] && (( $(wc -l < "$fixture/child-pids") >= target )); then
      return
    fi
    sleep 0.5
  done
  echo "Synthetic child count never reached $target" >&2
  exit 1
}
wait_for_children 1
[[ "$("$installed" status)" == 'Running (connection not verified) | Start with user session: on' ]]
old_agent="$(tail -n 1 "$fixture/agent-pids")"
old_child="$(tail -n 1 "$fixture/child-pids")"
kill -KILL "$old_agent"
wait_for_children 2
# A supervised restart cleans up the old cgroup before the next generation starts.
if kill -0 "$old_child" 2>/dev/null; then
  echo "The previous generation's child survived crash recovery." >&2
  exit 1
fi
"$installed" restart
wait_for_children 3
child="$(tail -n 1 "$fixture/child-pids")"
"$installed" stop
if kill -0 "$child" 2>/dev/null; then
  echo "The managed child survived Stop." >&2
  exit 1
fi
[[ "$("$installed" status)" == 'Stopped | Start with user session: on' ]]
"$installed" disable
[[ "$("$installed" status)" == 'Stopped | Start with user session: off' ]]
journal="$(journalctl --user -u "$unit" --no-pager)"
if grep -Eq 'synthetic-(stdout|stderr)-marker-never-log' <<<"$journal"; then
  echo "Raw synthetic agent output reached the service journal." >&2
  exit 1
fi

# Launch via the generated desktop entry and check the live X11 window, not a mock API.
xvfb-run -a bash -c '
  set -euo pipefail
  gio launch "$HOME/.local/share/applications/remote-commander-tray.desktop"
  for _ in {1..30}; do
    windows="$(xwininfo -root -tree)"
    if grep -Fq "Remote Commander Tray" <<<"$windows"; then exit 0; fi
    sleep 0.2
  done
  exit 1
'
"$installed" uninstall
[[ ! -e "$installed" && ! -e "$HOME/.config/systemd/user/$unit" ]]
echo "Linux Beta service, restart, shutdown, desktop entry and uninstall passed."
