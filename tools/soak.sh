#!/usr/bin/env bash
# Soak test — the checklist's "Stability" section without a microphone or
# VRChat: the bundled speech clip is fed in a loop as the microphone
# (--capture-command, tools/feed.py), a fake VRChat log with a watched
# player present makes captions auto-start, and the run is left alone for
# N minutes. Afterwards the boot log's "memory after N passes" lines, the
# session summary and error.log are printed: memory should stay flat and
# error.log empty.
#
# Models and engine packs are taken from the REAL data folder (symlinked
# into a throwaway one), so install an engine first; settings, players and
# logs stay in the throwaway folder and nothing real is modified. The
# chatbox text goes to 127.0.0.1:9000 like always (harmless without VRChat).
#
#   tools/soak.sh                              # publish/Chatterbox, 20 minutes
#   tools/soak.sh ~/Apps/Chatterbox/Chatterbox 5
#   tools/soak.sh <binary> <minutes> parakeet  # force an engine (parakeet|whisper)
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
EXE="${1:-$HERE/../publish/Chatterbox}"
MINUTES="${2:-20}"
ENGINE="${3:-}"
REAL="${XDG_DATA_HOME:-$HOME/.local/share}/Chatterbox"

if [ ! -x "$EXE" ]; then echo "binary not found or not executable: $EXE"; exit 2; fi
EXE="$(readlink -f "$EXE")"
if [ -z "${WAYLAND_DISPLAY:-}${DISPLAY:-}" ]; then echo "no display - run from a desktop session, or: xvfb-run -a $0 $EXE $MINUTES"; exit 2; fi
if [ ! -d "$REAL/models" ]; then echo "no models in $REAL/models - install an engine in Chatterbox first"; exit 2; fi
command -v python3 >/dev/null || { echo "python3 is needed for tools/feed.py"; exit 2; }
WAV="$HERE/../src/Chatterbox/Bench/fixture.wav"
[ -f "$WAV" ] || { echo "speech clip not found: $WAV"; exit 2; }

ROOT="$(mktemp -d "${TMPDIR:-/tmp}/chatterbox-soak-XXXXXX")"
DATA="$ROOT/data"; LOGS="$ROOT/vrchat"
mkdir -p "$DATA" "$LOGS"
ln -s "$REAL/models" "$DATA/models"
[ -d "$REAL/runtimes" ] && ln -s "$REAL/runtimes" "$DATA/runtimes"

# The real settings (engine, model, mic) plus a watched player and auto-start.
python3 - "$REAL/stt_settings.json" "$DATA/stt_settings.json" "$ENGINE" <<'PY'
import json, sys
src, dst, engine = sys.argv[1:4]
try:
    s = json.load(open(src))
except Exception:
    s = {}
s["AutoStartEnabled"] = True
s["AutoStartFriends"] = [{"Id": "usr_soak-0000-0000-0000-000000000001", "Name": "SoakTestPlayer"}]
if engine:
    s["Engine"] = engine
json.dump(s, open(dst, "w"), indent=2)
PY

STAMP="$(date '+%Y.%m.%d %H:%M:%S')"
cat > "$LOGS/output_log_2026-01-01_00-00-00.txt" <<LOG
$STAMP Log        -  [Behaviour] Joining wrld_soak-0000-0000-0000-000000000001:1~private(usr_me)~region(us)
$STAMP Log        -  [Behaviour] Entering Room: Soak Test World
$STAMP Log        -  [Behaviour] OnPlayerJoined SoakTestPlayer (usr_soak-0000-0000-0000-000000000001)
LOG

echo "==> Soaking $EXE for $MINUTES min (data: $DATA)"
"$EXE" --data-dir "$DATA" --vrchat-log-dir "$LOGS" --assume-vrchat-running \
  --capture-command "python3 '$HERE/feed.py' '$WAV'" \
  >"$ROOT/stdout.txt" 2>"$ROOT/stderr.txt" &
PID=$!
END=$(( $(date +%s) + MINUTES * 60 ))
while [ "$(date +%s)" -lt "$END" ] && kill -0 "$PID" 2>/dev/null; do
  sleep 30
  printf '%s  passes: %s  rss: %s kB\n' "$(date '+%H:%M:%S')" \
    "$(grep -c 'memory after' "$DATA/last_boot.log" 2>/dev/null)" \
    "$(awk '/VmRSS/ {print $2}' /proc/$PID/status 2>/dev/null)"
done
ALIVE=0; kill -0 "$PID" 2>/dev/null && ALIVE=1
if [ "$ALIVE" = 1 ]; then kill -TERM "$PID"; for i in $(seq 1 100); do kill -0 "$PID" 2>/dev/null || break; sleep 0.1; done; kill -9 "$PID" 2>/dev/null; fi
pkill -f "$HERE/feed.py" 2>/dev/null

echo
echo '--- last_boot.log: session, memory and warnings ---'
grep -E 'captions session|memory after|falling behind|skipped .* to catch up|recognition failed|capture failed|exit requested|unhandled|engine disposal' "$DATA/last_boot.log" 2>/dev/null
echo '--- error.log ---'; [ -s "$DATA/error.log" ] && cat "$DATA/error.log" || echo "(empty)"
echo
fail=0
[ "$ALIVE" = 1 ] || { echo "[FAIL] the app died before the end of the soak"; fail=1; }
grep -q 'captions session ended' "$DATA/last_boot.log" 2>/dev/null || { echo "[FAIL] no session summary (did captions start? is an engine installed?)"; fail=1; }
[ -s "$DATA/error.log" ] && { echo "[FAIL] error.log is not empty"; fail=1; }
if [ "$fail" = 0 ]; then echo "SOAK PASSED — compare the first and last 'memory after' lines above: RSS should be within a few tens of MB"; fi
echo "(run folder kept for inspection: $ROOT)"
exit $fail
