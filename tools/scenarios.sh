#!/usr/bin/env bash
# Functional scenarios for the published binary — the checklist items that
# need no microphone, model or VRChat, run on ANY Linux desktop (or under
# xvfb-run) in about five minutes:
#
#   S1  a second launch is refused; SIGTERM exits cleanly, lock released
#   S2  crash recovery: a boot.inprogress left behind is reported, safe boot
#   S3  VRChat "running" but its log folder is missing (named in the log)
#   S4  VRChat's log is present but empty (Logging off)
#   S5  Steam wrapper mode: the game gets its overlay preload and library
#       path back and its own arguments; the app exits when the game does
#   S6  the WebKit web process dies: the heartbeat watchdog reloads the page
#
# Nothing touches the real ~/.local/share/Chatterbox or ~/.config: every
# scenario runs with its own HOME and data folder under a temp directory.
#
#   tools/scenarios.sh                      # after build.sh (publish/Chatterbox)
#   tools/scenarios.sh ~/Apps/Chatterbox/Chatterbox
#   xvfb-run -a tools/scenarios.sh <binary> # without a desktop session
set -u
EXE="${1:-$(dirname "$0")/../publish/Chatterbox}"
if [ ! -x "$EXE" ]; then echo "binary not found or not executable: $EXE"; exit 2; fi
EXE="$(readlink -f "$EXE")"
if [ -z "${WAYLAND_DISPLAY:-}${DISPLAY:-}" ]; then echo "no display - run from a desktop session, or: xvfb-run -a $0 $EXE"; exit 2; fi
if pgrep -x Chatterbox >/dev/null 2>&1; then echo "Chatterbox is already running - close it first (S1 needs the lock)"; exit 2; fi

ROOT="$(mktemp -d "${TMPDIR:-/tmp}/chatterbox-scenarios-XXXXXX")"
fail=0
check() { if [ "$2" = 1 ]; then echo "  [PASS] $1"; else echo "  [FAIL] $1"; fail=$((fail+1)); fi; }
has() { grep -q -E -- "$2" "$1" 2>/dev/null && echo 1 || echo 0; }
alive() { kill -0 "$1" 2>/dev/null && echo 1 || echo 0; }
# 1 when pid $1 exits within $2 seconds
wait_exit() { local i=0; while kill -0 "$1" 2>/dev/null && [ "$i" -lt $(( $2 * 10 )) ]; do sleep 0.1; i=$((i+1)); done; kill -0 "$1" 2>/dev/null && echo 0 || echo 1; }
stop() { kill -TERM "$1" 2>/dev/null; wait_exit "$1" 10 >/dev/null; kill -KILL "$1" 2>/dev/null; wait "$1" 2>/dev/null; }
# A fresh home + data folder for one scenario: $HOME is set, the data
# folder path is echoed; a settings file is written so the run reads as a
# configured user (otherwise a missing file is treated as provisional).
scenario() {
  export HOME="$ROOT/$1/home"; mkdir -p "$HOME"
  local d="$ROOT/$1/data"; mkdir -p "$d"; echo '{ "CheckUpdatesAtStartup": false }' > "$d/stt_settings.json"
  echo "$d"
}
fakelog() { mkdir -p "$1"; local st; st="$(date '+%Y.%m.%d %H:%M:%S')"; printf '%s Log        -  [Behaviour] Joining wrld_x:1~region(us)\n%s Log        -  [Behaviour] Entering Room: X\n' "$st" "$st" > "$1/output_log_2026-01-01_00-00-00.txt"; }

echo "=== S1: a second launch is refused; SIGTERM exits cleanly"
D="$(scenario s1)"; fakelog "$D/vr"
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" >"$D/a.out" 2>"$D/a.err" & A=$!
sleep 6
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" >"$D/b.out" 2>"$D/b.err"; BCODE=$?
check "first instance alive"                         "$(alive $A)"
check "second launch exits 0 (got $BCODE)"           "$([ $BCODE = 0 ] && echo 1 || echo 0)"
check "second launch says already running"           "$(has "$D/b.err" 'already running')"
check "first page connected"                         "$(has "$D/last_boot.log" 'page connected')"
kill -TERM $A
check "SIGTERM: exits within 10 s"                   "$(wait_exit $A 10)"
wait $A; ACODE=$?
check "SIGTERM: exit code 0 (got $ACODE)"            "$([ $ACODE = 0 ] && echo 1 || echo 0)"
check "SIGTERM: logged"                              "$(has "$D/last_boot.log" 'exit requested by SIGTERM')"
check "SIGTERM: no error.log"                        "$([ ! -s "$D/error.log" ] && echo 1 || echo 0)"
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" >"$D/c.out" 2>"$D/c.err" & C=$!
sleep 4
check "lock released: a new instance starts"         "$(alive $C)"
stop $C

echo "=== S2: crash recovery (a boot.inprogress left behind)"
D="$(scenario s2)"; fakelog "$D/vr"; : > "$D/boot.inprogress"
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" >"$D/a.out" 2>"$D/a.err" & A=$!
sleep 8
check "alive"                                        "$(alive $A)"
check "boot log: previous start never reached the window" "$(has "$D/last_boot.log" 'previous start .*never reached the window')"
check "error.log: PreviousStart entry"               "$(has "$D/error.log" 'PreviousStart')"
check "page connected"                               "$(has "$D/last_boot.log" 'page connected')"
stop $A
check "sentinel cleared for the next start"          "$([ ! -e "$D/boot.inprogress" ] && echo 1 || echo 0)"

echo "=== S2b: the previous run died while captions were running (marker in phase 'captions')"
D="$(scenario s2b)"; fakelog "$D/vr"
printf '1.7.2|%s|999999|captions' "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)" > "$D/boot.inprogress"
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" >"$D/a.out" 2>"$D/a.err" & A=$!
sleep 8
check "alive"                                        "$(alive $A)"
check "boot log: ended while captions were running"  "$(has "$D/last_boot.log" 'ended while captions were running')"
check "boot log: SAFE BOOT"                          "$(has "$D/last_boot.log" 'SAFE BOOT')"
check "error.log: PreviousStart entry"               "$(has "$D/error.log" 'PreviousStart')"
check "the marker now guards this run (phase window)" "$(grep -q -E '\|window$' "$D/boot.inprogress" 2>/dev/null && echo 1 || echo 0)"
stop $A
check "a clean exit removes the marker"              "$([ ! -e "$D/boot.inprogress" ] && echo 1 || echo 0)"

echo "=== S3: VRChat 'running' but its log folder is missing"
D="$(scenario s3)"
"$EXE" --data-dir "$D" --vrchat-log-dir /nonexistent/vrchat --assume-vrchat-running >"$D/a.out" 2>"$D/a.err" & A=$!
sleep 40
check "alive after 40 s"                             "$(alive $A)"
check "boot log names the folder"                    "$(has "$D/last_boot.log" 'vrchat log dir: +/nonexistent/vrchat')"
check "boot log: log folder not found"               "$(has "$D/last_boot.log" 'log folder not found')"
check "no error.log"                                 "$([ ! -s "$D/error.log" ] && echo 1 || echo 0)"
stop $A

echo "=== S4: VRChat's log is present but empty (Logging off)"
D="$(scenario s4)"; mkdir -p "$D/vr"; : > "$D/vr/output_log_2026-01-01_00-00-00.txt"
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" --assume-vrchat-running >"$D/a.out" 2>"$D/a.err" & A=$!
sleep 40
check "alive after 40 s"                             "$(alive $A)"
check "boot log: still empty"                        "$(has "$D/last_boot.log" 'still empty')"
check "no error.log"                                 "$([ ! -s "$D/error.log" ] && echo 1 || echo 0)"
stop $A

echo "=== S5: Steam wrapper mode: the game gets its environment back, the app exits with it"
scenario s5 >/dev/null
# Wrapper mode takes every argument as the game's, so the app uses its
# default data folder under this scenario's HOME.
DEF="$HOME/.local/share/Chatterbox"; mkdir -p "$DEF"; echo '{ "CheckUpdatesAtStartup": false }' > "$DEF/stt_settings.json"
G="$ROOT/s5/game"; mkdir -p "$G"
cat > "$G/VRChat.exe" <<'EOF'
#!/bin/sh
printf 'LD_PRELOAD=%s\nLD_LIBRARY_PATH=%s\nargs=%s\n' "$LD_PRELOAD" "$LD_LIBRARY_PATH" "$*" > "$(dirname "$0")/game.env"
sleep 12
EOF
chmod +x "$G/VRChat.exe"
T0=$(date +%s)
LD_PRELOAD=/nonexistent/gameoverlayrenderer.so LD_LIBRARY_PATH=/opt/steam/ubuntu12_64/x:/usr/lib SYSTEM_LD_LIBRARY_PATH=/usr/lib \
  "$EXE" "$G/VRChat.exe" -some-game-arg --data-dir "$G" >"$G/a.out" 2>"$G/a.err" & A=$!
sleep 5
check "the game was started"                         "$([ -f "$G/game.env" ] && echo 1 || echo 0)"
check "the game got the overlay preload back"        "$(has "$G/game.env" '^LD_PRELOAD=/nonexistent/gameoverlayrenderer.so$')"
check "the game got Steam's library path back"       "$(has "$G/game.env" '^LD_LIBRARY_PATH=/opt/steam/ubuntu12_64/x:/usr/lib$')"
check "the game got its own arguments, not ours"     "$(has "$G/game.env" '^args=-some-game-arg --data-dir ')"
check "the app booted in its default data folder"    "$(has "$DEF/last_boot.log" 'page connected')"
check "the app saw the game running"                 "$(has "$DEF/last_boot.log" 'vrchat running: True')"
EXITED="$(wait_exit $A 35)"
check "the app exits after the game ($(( $(date +%s) - T0 )) s)" "$EXITED"
wait $A 2>/dev/null; ACODE=$?
check "exit code 0 (got $ACODE)"                     "$([ $ACODE = 0 ] && echo 1 || echo 0)"
check "no error.log"                                 "$([ ! -s "$DEF/error.log" ] && echo 1 || echo 0)"
[ "$EXITED" = 1 ] || stop $A
pkill -TERM -f "$G/VRChat.exe" 2>/dev/null

echo "=== S6: the WebKit web process dies -> the heartbeat watchdog reloads the page (~80 s)"
D="$(scenario s6)"; fakelog "$D/vr"
"$EXE" --data-dir "$D" --vrchat-log-dir "$D/vr" >"$D/a.out" 2>"$D/a.err" & A=$!
sleep 6
# Only THIS instance's web process (a direct child of the app) — never
# another Chatterbox's or a browser's.
WP="$(pgrep -P "$A" -f WebKitWebProcess | head -1)"
check "the app has a WebKit web process"             "$([ -n "$WP" ] && echo 1 || echo 0)"
[ -n "$WP" ] && kill -KILL "$WP"
sleep 80
check "app alive 80 s after the web process died"    "$(alive $A)"
check "the watchdog reloaded the page"               "$(has "$D/last_boot.log" 'no heartbeat .* reloading')"
stop $A

pkill -KILL -x Chatterbox 2>/dev/null
echo
if [ "$fail" -gt 0 ]; then echo "SCENARIOS: $fail check(s) FAILED (logs kept in $ROOT)"; exit 1; fi
rm -rf "$ROOT"
echo "SCENARIOS: ALL PASSED"
