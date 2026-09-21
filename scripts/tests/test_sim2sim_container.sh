#!/bin/bash
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
FAKE_BIN="$TMP/bin"
export FAKE_DOCKER_LOG="$TMP/docker.log"
export FAKE_DOCKER_STATE="$TMP/docker.state"
mkdir -p "$FAKE_BIN"

cat > "$FAKE_BIN/docker" <<'SH'
#!/bin/bash
set -euo pipefail
printf 'COMMAND %s\n' "${1:-}" >> "$FAKE_DOCKER_LOG"
printf 'ARG %s\n' "$@" >> "$FAKE_DOCKER_LOG"
case "${1:-}" in
  ps)
    [ -f "$FAKE_DOCKER_STATE" ] && echo unirobolab-sim2sim-jazzy
    ;;
  image)
    exit 0
    ;;
  inspect)
    cat "$FAKE_DOCKER_STATE"
    ;;
  run)
    for ((i=1; i <= $#; i++)); do
      if [ "${!i}" = --label ]; then
        j=$((i + 1)); label=${!j}; printf '%s\n' "${label#*=}" > "$FAKE_DOCKER_STATE"
      fi
    done
    echo fake-container-id
    ;;
  stop)
    rm -f "$FAKE_DOCKER_STATE"
    ;;
  exec)
    if [[ " $* " == *" check_runner.sh "* ]]; then
      echo "##STEP 5/5 fake check complete"
    fi
    ;;
esac
SH
chmod +x "$FAKE_BIN/docker"
export PATH="$FAKE_BIN:$PATH"

make_project() {
  local dir=$1
  mkdir -p "$dir/run"
  printf '{}\n' > "$dir/unirobolab.project.json"
  printf '{}\n' > "$dir/contract.json"
  printf 'onnx\n' > "$dir/run/policy.onnx"
  printf '<robot/>\n' > "$dir/robot.urdf"
  printf '{}\n' > "$dir/task.json"
}

PROJECT="$TMP/home projects/robot one"
make_project "$PROJECT"

"$ROOT/scripts/sim2sim_container.sh" start jazzy "$PROJECT" >/dev/null
grep -Fx -- "ARG $PROJECT:$PROJECT" "$FAKE_DOCKER_LOG" >/dev/null
grep -Fx -- "ARG io.unirobolab.project=$PROJECT" "$FAKE_DOCKER_LOG" >/dev/null

: > "$FAKE_DOCKER_LOG"
PROJECT_LINK="$TMP/project link"
ln -s "$PROJECT" "$PROJECT_LINK"
CONTRACT="$PROJECT_LINK/contract.json" ONNX="$PROJECT_LINK/run/policy.onnx" URDF="$PROJECT_LINK/robot.urdf" \
  TASK="$PROJECT_LINK/task.json" OUT="$PROJECT_LINK/sim2sim_out" NS="robot one" \
  "$ROOT/scripts/sim2sim_container.sh" check jazzy "$PROJECT_LINK" >/dev/null
! grep -q '^COMMAND run$' "$FAKE_DOCKER_LOG"
grep -Fx -- "ARG CONTRACT=$PROJECT/contract.json" "$FAKE_DOCKER_LOG" >/dev/null
grep -Fx -- "ARG /home/unity/unirobolab/scripts/check_runner.sh" "$FAKE_DOCKER_LOG" >/dev/null

OUTSIDE="$TMP/outside.json"
printf '{}\n' > "$OUTSIDE"
if CONTRACT="$OUTSIDE" ONNX="$PROJECT/run/policy.onnx" URDF="$PROJECT/robot.urdf" \
  OUT="$PROJECT/sim2sim_out" "$ROOT/scripts/sim2sim_container.sh" check jazzy "$PROJECT" >/dev/null 2>&1; then
  echo "outside-project input was accepted" >&2
  exit 1
fi

PROJECT2="$TMP/another/project"
make_project "$PROJECT2"
: > "$FAKE_DOCKER_LOG"
"$ROOT/scripts/sim2sim_container.sh" start jazzy "$PROJECT2" >/dev/null
grep -q '^COMMAND stop$' "$FAKE_DOCKER_LOG"
grep -q '^COMMAND run$' "$FAKE_DOCKER_LOG"

echo "sim2sim_container tests passed"
