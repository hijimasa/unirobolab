#!/bin/bash
# K simulator instances for training, without ROS 2. Run INSIDE the container.
#
#   sim_pool.sh start K [sim_dir]     learning servers on ports POOL_PORT_BASE..+K-1 (default 10100)
#   sim_pool.sh stop
#   sim_pool.sh urdf <xacro> [args]   process a xacro with use_sim:=true into $POOL_URDF (strips <gazebo>)
#
# The trainer spawns the robots itself through the learning server (unirobolab train
# --spawn-urdf ...), so no endpoint, no spawn service and no ROS graph are involved.
# Environment: POOL_SETTINGS (simulation_resources.json for every instance, default
# scripts/train_resources.json), POOL_SIM_ARGS (default "-batchmode -nographics
# -job-worker-count <cores/K>"), POOL_URDF (/tmp/robot.urdf).
SIM_DIR_DEFAULT=/home/unity/unirobolab/generated/player   # UniRoboLab player (scripts/build_player.sh); SIM_BIN overrides the executable name
source /home/unity/colcon_ws/scripts/simulator_version.sh
ACTION=${1:-status}
POOL_DIR=${POOL_DIR:-/tmp/sim_pool}
PORT_BASE=${POOL_PORT_BASE:-10100}
POOL_URDF=${POOL_URDF:-/tmp/robot.urdf}

case "$ACTION" in
  stop)
    pkill -f "${SIM_BIN:-UniRoboLab}.x8[6]_64" 2>/dev/null; sleep 2; echo "pool stopped"; exit 0 ;;
  urdf)
    source "/opt/ros/${ROS_DISTRO:-jazzy}/setup.bash"; source /home/unity/colcon_ws/install/setup.sh
    shift; xacro "$1" use_sim:=true "${@:2}" > "$POOL_URDF"
    python3 - "$POOL_URDF" <<'PY'
import sys, xml.etree.ElementTree as ET
p = sys.argv[1]; t = ET.parse(p); r = t.getroot()
for g in list(r.findall("gazebo")): r.remove(g)
t.write(p)
PY
    echo "wrote $POOL_URDF"; exit 0 ;;
  start) ;;
  status)
    echo "simulators: $(pgrep -fc "${SIM_BIN:-UniRoboLab}.x8[6]_64")"; ls "$POOL_DIR" 2>/dev/null; exit 0 ;;
  *) echo "usage: $0 start K [sim_dir] | stop | urdf <xacro> [args] | status" >&2; exit 1 ;;
esac

K=${2:-1}
SIM_DIR=${3:-${SIM_DIR:-$SIM_DIR_DEFAULT}}
SETTINGS=${POOL_SETTINGS:-/home/unity/unirobolab/scripts/train_resources.json}
# Each instance starts one job worker per core by default and the workers spin-wait, so K
# instances oversubscribe the machine. Share the cores: workers = cores / K (at least 2).
if [ -z "${POOL_SIM_ARGS+x}" ]; then
  W=$(( $(nproc) / K )); [ "$W" -lt 2 ] && W=2
  POOL_SIM_ARGS="-batchmode -nographics -job-worker-count $W"
fi
pkill -f "${SIM_BIN:-UniRoboLab}.x8[6]_64" 2>/dev/null; sleep 2
mkdir -p "$POOL_DIR"
export DISPLAY=${DISPLAY:-:1} XAUTHORITY=/.Xauthority
for ((i=0; i<K; i++)); do
  PORT=$((PORT_BASE + i))
  ( cd "$SIM_DIR" && SIMULATION_RESOURCES_CONFIG="$SETTINGS" SIM_LEARNING_PORT=$PORT \
      exec "./${SIM_BIN:-UniRoboLab}.x86_64" ${POOL_SIM_ARGS-"-batchmode -nographics"} ) \
      > "$POOL_DIR/sim_$i.log" 2>&1 < /dev/null &
done
# wait until every learning server answers
for ((i=0; i<K; i++)); do
  PORT=$((PORT_BASE + i))
  for _ in $(seq 1 60); do
    python3 - $PORT <<'PY' && break
import socket, sys
s = socket.socket(); s.settimeout(0.5)
try:
    s.connect(("127.0.0.1", int(sys.argv[1]))); s.close()
except OSError:
    sys.exit(1)
PY
    sleep 1
  done
done
echo "pool of $K started (ports $PORT_BASE..$((PORT_BASE + K - 1)), logs in $POOL_DIR)"
