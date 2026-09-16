#!/bin/bash
# Set up the Python side of UniRoboLab with uv, without touching the system Python.
#
#   scripts/setup_python.sh [--training] [--dir <venv dir>]
#
# Creates a virtual environment (default: <repo>/.venv) with the runtime extras
# (numpy, onnx, onnxruntime, pyyaml) so that `unirobolab gen / import-isaaclab /
# make-test-policy / live` work. --training adds stable-baselines3 and CPU torch for
# `unirobolab train` (the simulator container already has them). Installs uv into
# ~/.local/bin if it is missing (https://astral.sh/uv). Prints the interpreter path to
# put into the unirobolab.policy_runner_command entry of simulation_resources.json.
set -e
here=$(cd "$(dirname "$0")/.." && pwd)
venv="$here/.venv"
extras="runtime"
while [ $# -gt 0 ]; do
  case "$1" in
    --training) extras="runtime,training"; shift ;;
    --dir) venv="$2"; shift 2 ;;
    *) echo "usage: $0 [--training] [--dir <venv dir>]" >&2; exit 1 ;;
  esac
done
if ! command -v uv >/dev/null 2>&1; then
  echo "installing uv into ~/.local/bin"
  curl -LsSf https://astral.sh/uv/install.sh | sh
  export PATH="$HOME/.local/bin:$PATH"
fi
uv venv -q "$venv"
uv pip install -q --python "$venv/bin/python" -e "$here/python[$extras]"
echo "ready: $venv"
echo "  unirobolab: $venv/bin/unirobolab"
echo "  simulator settings: \"policy_runner_command\": \"$venv/bin/python -m unirobolab live\""
