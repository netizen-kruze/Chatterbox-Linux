#!/usr/bin/env bash
# Release smoke test — run on ANY Linux desktop, including one without
# VRChat or any model installed.
#
# Boots the published Chatterbox binary against a throwaway data folder and
# a fake VRChat log in which a watched player is already present, then
# checks that the process survives, the page connected, the settings were
# read, the fake log was read (a world and one player), and the boot-time
# auto-start check ran (the launch path that once crashed the Windows
# build). Nothing touches the real
# ~/.local/share/Chatterbox or the real game log.
#
#   tools/smoke.sh                          # after build.sh (uses publish/Chatterbox)
#   tools/smoke.sh ~/Apps/Chatterbox/Chatterbox
#   tools/smoke.sh <binary> 20              # wait 20 s instead of 15
#
# Needs a display (a desktop session, or run it under xvfb-run).
set -u

EXE="${1:-$(dirname "$0")/../publish/Chatterbox}"
WAIT="${2:-15}"

if [ ! -f "$EXE" ]; then echo "binary not found: $EXE"; exit 2; fi
if [ ! -x "$EXE" ]; then echo "not executable: $EXE  (fix: chmod +x \"$EXE\")"; exit 2; fi
EXE="$(readlink -f "$EXE")"
if pgrep -x Chatterbox >/dev/null 2>&1; then
  echo "Chatterbox is already running - close it first (single instance)."; exit 2
fi
if [ -z "${WAYLAND_DISPLAY:-}${DISPLAY:-}" ]; then
  echo "no display (WAYLAND_DISPLAY/DISPLAY unset) - run from a desktop session, or: xvfb-run -a $0 $EXE"; exit 2
fi

ROOT="$(mktemp -d "${TMPDIR:-/tmp}/chatterbox-smoke-XXXXXX")"
DATA="$ROOT/data"; LOGS="$ROOT/vrchat"
mkdir -p "$DATA" "$LOGS"

# A returning user with one watched player and auto-start on.
cat > "$DATA/stt_settings.json" <<'JSON'
{
  "AutoStartEnabled": true,
  "AutoStartFriends": [ { "Id": "usr_smoke-0000-0000-0000-000000000001", "Name": "SmokeTestPlayer" } ]
}
JSON

# A VRChat log in which that player is already in the instance.
STAMP="$(date '+%Y.%m.%d %H:%M:%S')"
cat > "$LOGS/output_log_2026-01-01_00-00-00.txt" <<LOG
$STAMP Log        -  [Behaviour] Joining wrld_smoke-0000-0000-0000-000000000001:1~private(usr_me)~region(us)
$STAMP Log        -  [Behaviour] Entering Room: Smoke Test World
$STAMP Log        -  [Behaviour] OnPlayerJoined SmokeTestPlayer (usr_smoke-0000-0000-0000-000000000001)
LOG

T0="$(date '+%Y-%m-%d %H:%M:%S')"
"$EXE" --data-dir "$DATA" --vrchat-log-dir "$LOGS" --assume-vrchat-running \
  >"$ROOT/stdout.txt" 2>"$ROOT/stderr.txt" &
PID=$!
sleep "$WAIT"
ALIVE=0; kill -0 "$PID" 2>/dev/null && ALIVE=1

LOG=""; [ -f "$DATA/last_boot.log" ] && LOG="$(cat "$DATA/last_boot.log")"
ERR=""; [ -f "$DATA/error.log" ] && ERR="$(cat "$DATA/error.log")"
CRASHES=0
if command -v coredumpctl >/dev/null 2>&1; then
  # the kernel keeps 15 bytes of the process name (Chatterbox-1.5.1-linux-x64 -> Chatterbox-1.5.)
  CRASHES="$(coredumpctl list --no-pager --no-legend --since="$T0" 2>/dev/null | grep -c "$(basename "$EXE" | cut -c1-15)" || true)"
fi
if [ "$ALIVE" = 1 ]; then kill "$PID" 2>/dev/null; sleep 1; kill -9 "$PID" 2>/dev/null; fi

fail=0
check() { if [ "$2" = 1 ]; then echo "  [PASS] $1"; else echo "  [FAIL] $1"; fail=$((fail + 1)); fi; }
has() { if printf '%s' "$LOG" | grep -q -E -- "$1"; then echo 1; else echo 0; fi; }

check "process alive after wait"        "$ALIVE"
check "boot log written"                "$([ -n "$LOG" ] && echo 1 || echo 0)"
check "settings file read"              "$(has 'settings from: +settings file')"
check "page connected"                  "$(has 'page connected')"
check "boot auto-start check ran"       "$(has 'boot auto-start check')"
check "VRChat log read"                 "$(has 'vrchat log: +output_log_2026-01-01_00-00-00\.txt .*in a world, 1 player')"
check "cuda line in boot log"           "$(has 'cuda: ')"
check "no core dumps"                   "$([ "$CRASHES" = 0 ] && echo 1 || echo 0)"
check "no unhandled exceptions logged"  "$(printf '%s' "$ERR" | grep -q -E 'Unhandled|SessionWork|UiDispatcher|OnUiMessage' && echo 0 || echo 1)"

echo
echo '--- last_boot.log ---'
echo "$LOG"
if [ -n "$ERR" ]; then echo '--- error.log ---'; echo "$ERR"; fi
if [ -s "$ROOT/stderr.txt" ]; then echo '--- stderr (last 20 lines) ---'; tail -n 20 "$ROOT/stderr.txt"; fi
rm -rf "$ROOT"

if [ "$fail" -gt 0 ]; then echo "SMOKE TEST FAILED ($fail check(s))"; exit 1; fi
echo "SMOKE TEST PASSED"
exit 0
