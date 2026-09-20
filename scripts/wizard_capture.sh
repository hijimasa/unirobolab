#!/bin/bash
# Drive the wizard from the command line and capture screenshots (for tutorials and UX reviews).
#
#   scripts/wizard_capture.sh <project dir> <phase 1-6> <shots> [action] [timeout s]
#     shots  : comma-separated "<png>@<seconds>" or "<png>@done" (done = after the phase's action finished)
#     action : the phase's main operation, run right after the phase opens:
#              1 load | 2 next | 3 train | 4 run | 5 check | 6 deploy
# Example: scripts/wizard_capture.sh ~/p/servo 2 /tmp/task.png@12
#          scripts/wizard_capture.sh ~/p/servo 3 /tmp/train_start.png@15,/tmp/train_done.png@done train 900
# Needs a display (DISPLAY, default :1); the window is 1280x800. Prints the player log lines that matter.
set -u
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PROJECT=$1; PHASE=$2; SHOTS=$3; ACTION=${4:-}; LIMIT=${5:-90}
mkdir -p "$PROJECT"
abs() { case "$1" in /*) echo "$1" ;; *) echo "$PWD/$1" ;; esac; }
ABS_SHOTS=""; LAST=""
for item in $(echo "$SHOTS" | tr ',' ' '); do f=$(abs "${item%@*}"); rm -f "$f"; ABS_SHOTS="${ABS_SHOTS:+$ABS_SHOTS,}$f@${item#*@}"; LAST=$f; done
LOG=${WIZ_LOG:-$PROJECT/wizard_capture.log}
env DISPLAY="${DISPLAY:-:1}" SIM_WIZARD_PHASE="$PHASE" ${ACTION:+SIM_WIZARD_ACTION="$ACTION"} SIM_GUI_SCREENSHOT="$ABS_SHOTS" \
  timeout "$LIMIT" "$ROOT/scripts/unirobolab_gui.sh" "$PROJECT" -logFile "$LOG" >/dev/null 2>&1 &
PID=$!
for i in $(seq 1 "$LIMIT"); do sleep 1; [ -s "$LAST" ] && break; kill -0 $PID 2>/dev/null || break; done
sleep 2; kill $PID 2>/dev/null; sleep 1
grep -E "\[Wizard\]|\[Env\]|Exception" "$LOG" | grep -v "screenshot ->" | cut -c1-160 | head -12
[ -s "$LAST" ] && echo "captured: $ABS_SHOTS" || { echo "no screenshot within ${LIMIT}s (see $LOG)"; exit 1; }
