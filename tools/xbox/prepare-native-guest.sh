#!/bin/bash
# Platform setup for the original DroidDeck Linux runtime on a native x86-64 guest.
# Run inside the newly provisioned guest; the argument is an ORIGINAL overlay checkout.
set -euo pipefail
test "$(id -u)" = 0
test "$(uname -s):$(uname -m)" = Linux:x86_64
original=${1:?Path to original tools/linuxfs/overlay is required}
test -f "$original/usr/local/bin/droiddeck-session"
test -f "$original/usr/local/bin/droiddeck-steam-x64"
id droiddeck >/dev/null

export DEBIAN_FRONTEND=noninteractive
dpkg --add-architecture i386
apt-get update -qq
apt-get install -y --no-install-recommends \
  xserver-xorg-core xserver-xorg-video-fbdev xserver-xorg-video-vesa \
  xserver-xorg-input-libinput x11-utils openbox dbus-x11 curl ca-certificates \
  file unzip xz-utils fonts-dejavu-core fonts-liberation python3 procps util-linux libgtk-3-0 libnss3 libxss1 libasound2 \
  pulseaudio libpulse0 libvulkan1 mesa-vulkan-drivers libgl1-mesa-dri \
  libc6:i386 libstdc++6:i386 libx11-6:i386 libxrandr2:i386 libglib2.0-0:i386 \
  libgl1-mesa-dri:i386 libgl1:i386 libvulkan1:i386 mesa-vulkan-drivers:i386
usermod -aG input,video,audio droiddeck
if getent group render >/dev/null; then usermod -aG render droiddeck; fi
install -d /usr/local/bin /usr/local/libexec/droiddeck-xbox
install -m 755 "$(dirname "${BASH_SOURCE[0]}")/guest-gamepad.py" /usr/local/libexec/droiddeck-xbox/gamepad
find "$original/usr/local/bin" -maxdepth 1 -type f \
  -exec install -m 755 '{}' /usr/local/bin/ \;

cat > /etc/systemd/system/droiddeck-display.service <<'UNIT'
[Unit]
Description=DroidDeck Xbox guest display
After=systemd-user-sessions.service
Conflicts=getty@tty1.service
[Service]
ExecStart=/usr/lib/xorg/Xorg :0 -ac -nolisten tcp -noreset vt1
[Install]
WantedBy=multi-user.target
UNIT

cat > /usr/local/libexec/droiddeck-xbox/x11-session <<'SESSION'
#!/bin/bash
set -e
for attempt in $(seq 1 120); do
  xdpyinfo >/dev/null 2>&1 && break
  sleep 1
done
xdpyinfo >/dev/null 2>&1
pulseaudio --start --exit-idle-time=-1
openbox >/dev/null 2>&1 &
wm=$!
trap 'kill "$wm" 2>/dev/null || true' EXIT
# BL_INSIDE selects the existing inner session, replacing Android's compositor boundary.
/usr/local/bin/droiddeck-session steam
SESSION
chmod 755 /usr/local/libexec/droiddeck-xbox/x11-session
cat > /etc/systemd/system/droiddeck-steam.service <<'UNIT'
[Unit]
Description=Original DroidDeck native Steam session
After=network-online.target droiddeck-display.service dbus.service
Requires=droiddeck-display.service
Wants=network-online.target dbus.service
[Service]
User=droiddeck
WorkingDirectory=/home/droiddeck
RuntimeDirectory=droiddeck
RuntimeDirectoryMode=0700
Environment=HOME=/home/droiddeck DISPLAY=:0 XDG_RUNTIME_DIR=/run/droiddeck
Environment=BL_LAUNCH_DIR=/run/droiddeck
Environment=XDG_DATA_HOME=/home/droiddeck/.local/share
Environment=BL_X64_NATIVE=1 BL_STEAM_CLIENT_ARCH=x86_64 BL_INSIDE=1
Environment=BL_DESKTOP=1 BL_STEAM_UI=desktop BL_X64_DECK=0
ExecStart=/usr/bin/dbus-run-session -- /usr/local/libexec/droiddeck-xbox/x11-session
KillMode=control-group
# Client output stays private inside the guest. No host log export.
StandardOutput=null
StandardError=null
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
modprobe uinput
printf '%s\n' uinput > /etc/modules-load.d/droiddeck-gamepad.conf
cat > /etc/udev/rules.d/60-droiddeck-uinput.rules <<'RULES'
KERNEL=="uinput", SUBSYSTEM=="misc", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"
RULES
udevadm control --reload-rules
udevadm trigger --subsystem-match=misc --sysname-match=uinput
udevadm settle
cat > /etc/systemd/system/droiddeck-gamepad.service <<'UNIT'
[Unit]
Description=DroidDeck Xbox standard controller adapter
After=systemd-modules-load.service
[Service]
ExecStart=/usr/local/libexec/droiddeck-xbox/gamepad
StandardOutput=null
StandardError=null
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now droiddeck-gamepad.service
systemctl enable --now droiddeck-display.service
printf '%s\n' 'Platform installed. Run original droiddeck-steam-x64 --prepare as droiddeck before enabling droiddeck-steam.service.'
