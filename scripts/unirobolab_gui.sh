#!/bin/bash
# Launch the UniRoboLab GUI with sensible defaults (no hand-written settings file needed).
#   scripts/unirobolab_gui.sh [project dir] [extra player args...]
# Env: UNIROBOLAB_SETTINGS (settings json, default scripts/gui_resources.json), SIM_LEARNING_PORT (default 10100)
ROOT=$(cd "$(dirname "$0")/.." && pwd)
# player: release layout (<root>/player/) first, then the development build (<root>/generated/player/)
PLAYER=${UNIROBOLAB_PLAYER:-}
if [ -z "$PLAYER" ]; then
  for cand in "$ROOT/player/UniRoboLab.x86_64" "$ROOT/generated/player/UniRoboLab.x86_64"; do [ -x "$cand" ] && PLAYER=$cand && break; done
fi
[ -n "$PLAYER" ] && [ -x "$PLAYER" ] || { echo "player not found under $ROOT/player or $ROOT/generated/player (run scripts/build_player.sh, or download a release)"; exit 1; }
PROJECT=${1:-}; [ $# -gt 0 ] && shift
export SIMULATION_RESOURCES_CONFIG=${UNIROBOLAB_SETTINGS:-$ROOT/scripts/gui_resources.json}
export SIM_LEARNING_PORT=${SIM_LEARNING_PORT:-10100}
[ -n "$PROJECT" ] && export SIM_WIZARD_PROJECT=$(cd "$(dirname "$PROJECT")" 2>/dev/null && pwd)/$(basename "$PROJECT")
exec "$PLAYER" -screen-width 1280 -screen-height 800 -screen-fullscreen 0 "$@"
