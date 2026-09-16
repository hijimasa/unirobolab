#!/bin/bash
# Deploy check (sim2sim) against an already running UniRoboLab simulator, driven by the GUI (phase 5).
# Runs where ROS 2 is available: on the host, or inside the unirobolab-sim2sim container (docker exec).
# Env (paths as seen from where this runs):
#   CONTRACT  contract.json     ONNX  policy.onnx     URDF  robot.urdf     OUT  output dir
#   TASK      task.json (optional; goals and tolerance come from it)      NS   robot namespace (= entity name)
#   ROS_DISTRO (default jazzy)
# Prints "##STEP n/5 <text>" lines for the GUI and leaves OUT/report.json.
# (no set -u: the ROS setup scripts reference unset variables)
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export PYTHONPATH="$ROOT/python/src:${PYTHONPATH:-}"
NS=${NS:-robot}; for v in OUT CONTRACT ONNX URDF; do [ -n "${!v:-}" ] || { echo "##FAIL $v is not set"; exit 1; }; done
mkdir -p "$OUT"
LOG="$OUT/check_runner.log"; : > "$LOG"
say() { echo "##STEP $1"; echo "##STEP $1" >> "$LOG"; }
fail() { echo "##FAIL $1"; echo "##FAIL $1" >> "$LOG"; cleanup; exit 1; }
STARTED_ENDPOINT=0
cleanup() {
  pkill -f 'policy_nod[e]' 2>/dev/null
  [ "$STARTED_ENDPOINT" = 1 ] && pkill -f 'default_server_endpoin[t]' 2>/dev/null
  true
}
trap cleanup EXIT

source "/opt/ros/${ROS_DISTRO:-jazzy}/setup.bash" 2>/dev/null || fail "ROS 2 (${ROS_DISTRO:-jazzy}) not found"
[ -f /home/unity/colcon_ws/install/setup.sh ] && source /home/unity/colcon_ws/install/setup.sh
[ -f /home/unity/colcon_ws/scripts/fastdds_udp_only.xml ] && export FASTRTPS_DEFAULT_PROFILES_FILE=/home/unity/colcon_ws/scripts/fastdds_udp_only.xml
ros2 pkg prefix simulation_ros2_utils >/dev/null 2>&1 || fail "simulation_ros2_utils (spawn_entity / set_sim_state) is not in this ROS 2 environment"
ros2 pkg prefix ros_tcp_endpoint >/dev/null 2>&1 || fail "ros_tcp_endpoint is not in this ROS 2 environment"

say "1/5 generating the ROS 2 package"
rm -rf "$OUT/pkg" "$OUT/ws"
python3 -m unirobolab gen "$CONTRACT" --out "$OUT/pkg" --onnx "$ONNX" --overwrite >> "$LOG" 2>&1 || fail "package generation failed (see $LOG)"
PKG=$(ls "$OUT/pkg" | head -1)

say "2/5 building the package ($PKG)"
( cd "$OUT" && colcon build --base-paths pkg --build-base ws/build --install-base ws/install --symlink-install >> "$LOG" 2>&1 ) || fail "colcon build failed (see $LOG)"
source "$OUT/ws/install/setup.sh"

say "3/5 connecting to the simulator and spawning $NS"
if ! pgrep -f 'default_server_endpoin[t]' >/dev/null; then
  nohup ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0 >> "$LOG" 2>&1 < /dev/null &
  STARTED_ENDPOINT=1; sleep 4
fi
timeout 30 ros2 run simulation_ros2_utils set_sim_state --ros-args -p set_state:=start >> "$LOG" 2>&1 || fail "the simulator did not answer set_simulation_state (is it running with ROS enabled?)"
timeout 60 ros2 run simulation_ros2_utils spawn_entity --ros-args -r spawn_entity:=/spawn_entity \
  -p urdf_path:="$URDF" -p robot_name:="$NS" -p x:=${SPAWN_X:-0.0} -p y:=${SPAWN_Y:-0.0} -p z:=0.0 -p R:=0.0 -p P:=0.0 -p Y:=${SPAWN_YAW:-0.0} >> "$LOG" 2>&1 || fail "spawn_entity failed (see $LOG)"
sleep 2
timeout 10 ros2 topic echo "/$NS/joint_states" --once --field name >> "$LOG" 2>&1 || fail "no /$NS/joint_states from the simulator"

say "4/5 running the policy through ROS 2 (goals + e-stop test, about 30 s)"
python3 -m unirobolab scenario-default "$CONTRACT" ${TASK:+--task "$TASK"} --out "$OUT/scenario.json" >> "$LOG" 2>&1 || fail "scenario generation failed"
python3 -m unirobolab sim2sim "$CONTRACT" --pkg "$PKG" --scenario "$OUT/scenario.json" --out "$OUT" >> "$LOG" 2>&1
RC=$?
[ -f "$OUT/report.json" ] || fail "sim2sim produced no report (see $LOG)"

say "5/5 done (sim2sim exit $RC)"
exit 0
