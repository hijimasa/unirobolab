#!/bin/bash
# Launch the UniRoboLab GUI with sensible defaults (no hand-written settings file needed).
#   scripts/unirobolab_gui.sh [project dir] [extra player args...]
# Env: UNIROBOLAB_SETTINGS (settings json, default scripts/gui_resources.json), SIM_LEARNING_PORT (default 10100)
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PLAYER=${UNIROBOLAB_PLAYER:-$ROOT/generated/player/UniRoboLab.x86_64}
[ -x "$PLAYER" ] || { echo "player not found: $PLAYER (run scripts/build_player.sh)"; exit 1; }
PROJECT=${1:-}; [ $# -gt 0 ] && shift
export SIMULATION_RESOURCES_CONFIG=${UNIROBOLAB_SETTINGS:-$ROOT/scripts/gui_resources.json}
export SIM_LEARNING_PORT=${SIM_LEARNING_PORT:-10100}
[ -n "$PROJECT" ] && export SIM_WIZARD_PROJECT=$(cd "$(dirname "$PROJECT")" 2>/dev/null && pwd)/$(basename "$PROJECT")
exec "$PLAYER" -screen-width 1280 -screen-height 800 -screen-fullscreen 0 "$@"
