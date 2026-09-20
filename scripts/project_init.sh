#!/bin/bash
# Create a wizard project folder from a URDF, ready for step ① (what the GUI does when you pick a URDF).
#   scripts/project_init.sh <project dir> <urdf> [name] [namespace]
# Copies the URDF into the folder, writes unirobolab.project.json and a task.json preset
# (joint_target for a fixed base, base_target for a mobile base). Needs .venv (scripts/setup_python.sh).
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PY=${UNIROBOLAB_PYTHON:-$ROOT/.venv/bin/python}
DIR=$1; URDF=$2; NAME=${3:-$(basename "${URDF%.*}")}; NS=${4:-$NAME}
mkdir -p "$DIR"; cp "$URDF" "$DIR/"; U=$(basename "$URDF")
FIXED=$("$PY" -m unirobolab robot-info "$DIR/$U" | "$PY" -c "import json,sys; print('1' if json.load(sys.stdin).get('fixed_base', True) else '0')")
KIND=joint_target; [ "$FIXED" = 0 ] && KIND=base_target
"$PY" -m unirobolab task-preset $KIND --urdf "$DIR/$U" --out "$DIR/task.json" --name "$NAME" --namespace "$NS" >/dev/null
cat > "$DIR/unirobolab.project.json" <<JSON
{"urdf": "$U", "name": "$NAME", "ns": "$NS", "command_mode": "joint_state_topic", "controller": "", "estop_topic": "", "task": "task.json",
 "contract": "", "train": "", "run_dir": "", "report": "", "deploy_dir": "", "phase_reached": 1, "stamp_keys": [], "stamp_values": []}
JSON
echo "project: $DIR ($KIND) -> scripts/unirobolab_gui.sh $DIR"
