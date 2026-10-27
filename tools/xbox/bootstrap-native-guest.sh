#!/bin/bash
# First-boot platform boundary. Steam installation and launch remain original DroidDeck code.
set -euo pipefail
install -d -m 755 /var/lib/droiddeck
state() {
  printf '%s\n' "$1" > /var/lib/droiddeck/platform-state.tmp
  mv /var/lib/droiddeck/platform-state.tmp /var/lib/droiddeck/platform-state
}
trap 'state setup_failed' ERR
state platform_installing
platform=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
bash "$platform/xbox/prepare-native-guest.sh" "$platform/overlay"
state client_installing
runuser -u droiddeck -- env HOME=/home/droiddeck BL_X64_NATIVE=1 \
  /usr/local/bin/droiddeck-steam-x64 --prepare > /var/lib/droiddeck/public-client-install.log 2>&1
state client_installed
systemctl enable --now droiddeck-steam.service
state session_started
# This state does not assert authentication, a game download, or successful gameplay.
