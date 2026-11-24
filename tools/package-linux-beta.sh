#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-"$root/publish"}"
bundle="$(mktemp -d)"
trap 'rm -rf -- "$bundle"' EXIT
name=RemoteCommanderTray-linux-beta
mkdir -p -- "$out" "$bundle/$name"
install -m 755 "$root/linux/remote-commander-tray" "$bundle/$name/"
install -m 644 "$root/linux/remote-commander-tray.service" "$root/docs/LINUX_BETA.md" "$root/LICENSE" "$bundle/$name/"
# Keep this archive byte-for-byte reproducible for an identical checkout.
tar --sort=name --mtime='UTC 2020-01-01' --owner=0 --group=0 --numeric-owner -cf - -C "$bundle" "$name" |
  gzip -n > "$out/$name.tar.gz"
