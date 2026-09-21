#!/bin/bash
# ウィザードの動線が最後まで押せることを確かめる (プレイヤーをヘッドレスで動かす)。
#
# 2026-09-22 の不具合: 「次へ」が *次の* フェーズに入れるかどうかで押せる/押せないを決めていたため、
# URDF を読み込んだだけの状態 (task.json はまだ無い) で ① の「次へ」が灰色のまま押せず、
# 一連の作業が始められなかった。task.json を書くのは ① の「次へ」自身なので、先に見てはいけない。
# ここでは URDF だけのプロジェクトから ① → ② → ③ が通ることを確かめる。
#
#   scripts/tests/test_wizard_flow.sh
# 事前に scripts/build_player.sh と scripts/setup_python.sh が済んでいること。
set -u
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
PLAYER=${UNIROBOLAB_PLAYER:-$ROOT/generated/player/UniRoboLab.x86_64}
PORT=${SIM_LEARNING_PORT:-10109}
fails=0
note() { echo "  $*"; }
fail() { echo "FAIL: $*"; fails=$((fails + 1)); }

[ -x "$PLAYER" ] || { echo "SKIP: no player at $PLAYER (scripts/build_player.sh)"; exit 0; }
[ -x "$ROOT/.venv/bin/python" ] || { echo "SKIP: no .venv (scripts/setup_python.sh)"; exit 0; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
cp "$ROOT/python/tests/fixtures/servo_demo.urdf" "$tmp/"
cat > "$tmp/unirobolab.project.json" <<JSON
{"urdf": "servo_demo.urdf", "name": "ServoDemo", "ns": "ServoDemo", "command_mode": "joint_state_topic",
 "controller": "", "estop_topic": "", "task": "task.json", "contract": "", "train": "", "run_dir": "",
 "report": "", "deploy_dir": "", "phase_reached": 1, "stamp_keys": [], "stamp_values": []}
JSON

# <phase> <action> <seconds> -> ログを $tmp/log に残す
run_phase() {
  local phase=$1 action=$2 limit=$3
  rm -f "$tmp/log"
  env SIMULATION_RESOURCES_CONFIG="$ROOT/scripts/gui_resources.json" SIM_LEARNING_PORT="$PORT" \
      SIM_WIZARD_PROJECT="$tmp" SIM_WIZARD_PHASE="$phase" SIM_WIZARD_ACTION="$action" \
      timeout "$limit" "$PLAYER" -batchmode -nographics -logFile "$tmp/log" >/dev/null 2>&1
  [ -s "$tmp/log" ] || { fail "phase $phase: the player wrote no log"; return 1; }
  return 0
}

echo "1) URDF だけのプロジェクト: ① で「次へ」が押せる"
run_phase 1 "" 45
if grep -q "^\[Wizard\] next=1 phase=1" "$tmp/log"; then
  note "next is enabled at step 1"
else
  fail "step 1 cannot proceed with a URDF loaded: $(grep -m1 '^\[Wizard\] next=' "$tmp/log" || echo 'no next= line')"
fi

echo "2) ① の「次へ」で task.json ができ、② へ進む"
run_phase 1 next 60
grep -q "^\[Wizard\] phase 2" "$tmp/log" || fail "step 1 -> 2 did not happen"
[ -f "$tmp/task.json" ] || fail "step 1 did not write task.json (the Next button creates it)"

echo "3) ② の「次へ」で契約と学習設定ができ、③ へ進む"
run_phase 2 next 120
grep -q "^\[Wizard\] phase 3" "$tmp/log" || fail "step 2 -> 3 did not happen (task-gen failed?)"
ls "$tmp"/generated/*.json >/dev/null 2>&1 || fail "step 2 did not generate the contract and the training config"

echo "4) ③ は学習前なので「次へ」は押せず、理由が出る"
run_phase 3 "" 60
if grep -q "^\[Wizard\] next=0 phase=3" "$tmp/log"; then
  note "next is disabled at step 3 with a reason: $(grep -m1 '^\[Wizard\] next=0 phase=3' "$tmp/log" | sed 's/.*reason=//')"
else
  fail "step 3 should not be passable before training"
fi

[ "$fails" = 0 ] && { echo "wizard flow tests passed"; exit 0; }
echo "$fails check(s) failed"; exit 1
