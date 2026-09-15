#!/bin/bash
# Bring the simulator up with the servo demo robot spawned and NO controller attached,
# so the policy node drives /ServoDemo/joint_command directly. Run INSIDE the container.
#
#   servo_demo_bringup.sh [sim_dir]
#
# Derived from Unity_ROS2_sample/colcon_ws/scripts/bringup_test.sh (same ordering:
# endpoint first, then the simulator, then start, then spawn).
source /home/unity/colcon_ws/scripts/simulator_version.sh
SIM_DIR=${1:-${SIM_DIR:-$SIM_DIR_DEFAULT}}   # arg or SIM_DIR env, e.g. a player built from a branch
NS=${NS:-ServoDemo}

rm -f /dev/shm/fastrtps_* /dev/shm/sem.fastrtps_* 2>/dev/null
export FASTRTPS_DEFAULT_PROFILES_FILE=/home/unity/colcon_ws/scripts/fastdds_udp_only.xml

pkill -f 'Simulator.x8[6]_64' 2>/dev/null
pkill -f 'default_server_endpoin[t]' 2>/dev/null
pkill -f 'spawn_entit[y]' 2>/dev/null
pkill -f 'policy_nod[e]' 2>/dev/null
sleep 2
rm -f /dev/shm/fastrtps_* /dev/shm/sem.fastrtps_* 2>/dev/null

source "/opt/ros/${ROS_DISTRO:-jazzy}/setup.bash"
source /home/unity/colcon_ws/install/setup.sh

nohup ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0 > /tmp/endpoint.log 2>&1 < /dev/null &
sleep 5

export DISPLAY=${DISPLAY:-:1} XAUTHORITY=/.Xauthority
# Pacing. The player defaults to 10 FPS (FrameRateController), which makes Unity run
# five 50 Hz physics steps back-to-back per frame: joint_states then leave in bursts of
# three every 100 ms and commands are consumed once per frame. target_fps lifts that.
export SIMULATION_RESOURCES_CONFIG=${SIM_SETTINGS:-/home/unity/unirobolab/scripts/sim2sim_resources.json}
# exec inside the subshell so no bash lingers holding this script's stdout open:
# a caller that pipes this script (e.g. `| tail`) would otherwise never see EOF.
# SIM_ARGS: e.g. "-batchmode" for training (no window, so no vsync cap on the frame rate;
# rendering still works for camera sensors). Default: windowed.
( cd "$SIM_DIR" && exec ./Unity_ROS2_Robot_Simulator.x86_64 ${SIM_ARGS:-} ) > /tmp/sim_bringup.log 2>&1 < /dev/null &
sleep 14

timeout 30 ros2 run simulation_ros2_utils set_sim_state --ros-args -p set_state:=start 2>&1 | tail -1
sleep 2

URDF=$(ros2 pkg prefix servo_demo_description)/share/servo_demo_description/robots/servo_demo.urdf
# N_ENTITIES > 1: spawn copies named ${NS}_0.. in a row (for the direct learning channel,
# which addresses entities by name; their ROS topics all share /${NS}/... and are unused).
N=${N_ENTITIES:-1}
for ((i=0; i<N; i++)); do
  if [ "$N" -eq 1 ]; then NAME=$NS; else NAME="${NS}_$i"; fi
  X=$(python3 -c "print($i * ${ENTITY_SPACING:-0.6})")
  timeout 60 ros2 run simulation_ros2_utils spawn_entity --ros-args -r spawn_entity:=/spawn_entity \
    -p urdf_path:="$URDF" -p robot_name:="$NAME" -p x:=$X -p y:=0.0 -p z:=0.0 -p R:=0.0 -p P:=0.0 -p Y:=-1.57 2>&1 | tail -1
done
sleep 3
timeout 8 ros2 topic echo /$NS/joint_states --once --field name 2>/dev/null | tr '\n' ' '; echo
[ -n "${SIM_LEARNING_PORT:-}" ] && echo "learning server port: $SIM_LEARNING_PORT (SIM_LEARNING_PORT is inherited by the player)"
echo "bringup done (sim log: /tmp/sim_bringup.log)"
