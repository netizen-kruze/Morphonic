#!/usr/bin/env bash
# Brings up a headless desktop for the Linux tests: an Xvfb display, a D-Bus
# session, a PipeWire graph (pipewire + wireplumber + pipewire-pulse) with a
# null sink "TestSpeakers" (whose monitor is the default microphone) so the
# app has devices to open. Writes the environment to env.sh for later shells.
set -u
ENVDIR="$(cd "$(dirname "$0")" && pwd)"
export XDG_RUNTIME_DIR=/tmp/xdg-morphonic
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"
export DISPLAY=:99
export HOME="${HOME:-/root}"

if ! pgrep -x Xvfb >/dev/null; then
  Xvfb :99 -screen 0 1280x800x24 -nolisten tcp >"$ENVDIR/xvfb.log" 2>&1 &
  sleep 1
fi
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ] || ! dbus-send --session --dest=org.freedesktop.DBus --print-reply /org/freedesktop/DBus org.freedesktop.DBus.ListNames >/dev/null 2>&1; then
  eval "$(dbus-launch --sh-syntax)"
fi
if ! pgrep -x pipewire >/dev/null; then
  pipewire >"$ENVDIR/pipewire.log" 2>&1 &
  sleep 1
  wireplumber >"$ENVDIR/wireplumber.log" 2>&1 &
  sleep 1
  pipewire-pulse >"$ENVDIR/pipewire-pulse.log" 2>&1 &
  sleep 2
fi
for _ in 1 2 3 4 5 6 7 8 9 10; do pactl info >/dev/null 2>&1 && break; sleep 1; done
pactl info | head -3
if ! pactl list short sinks | grep -q TestSpeakers; then
  pactl load-module module-null-sink sink_name=TestSpeakers sink_properties=device.description=Test-Speakers >/dev/null
fi
if ! pactl list short sources | grep -q TestMic; then
  pactl load-module module-remap-source master=TestSpeakers.monitor source_name=TestMic source_properties=device.description=Test-Microphone >/dev/null
fi
pactl set-default-sink TestSpeakers >/dev/null 2>&1
pactl set-default-source TestMic >/dev/null 2>&1
cat > "$ENVDIR/env.sh" <<EOF
export XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR
export DISPLAY=$DISPLAY
export DBUS_SESSION_BUS_ADDRESS='$DBUS_SESSION_BUS_ADDRESS'
export DBUS_SESSION_BUS_PID=${DBUS_SESSION_BUS_PID:-}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
EOF
echo "--- sinks";   pactl list short sinks
echo "--- sources"; pactl list short sources
echo "--- pw-dump nodes"; pw-dump 2>/dev/null | python3 -c "import json,sys; [print(o['info']['props'].get('media.class'), o['info']['props'].get('node.name')) for o in json.load(sys.stdin) if o.get('type')=='PipeWire:Interface:Node' and 'Audio' in str(o['info']['props'].get('media.class'))]"
echo "env written to $ENVDIR/env.sh"
