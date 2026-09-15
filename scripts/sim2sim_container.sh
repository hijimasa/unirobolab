#!/bin/bash
# Start (or reuse) the ROS 2 + simulator container for sim2sim, detached, with this
# repository mounted next to the sample colcon workspace. Mirrors
# Unity_ROS2_sample/docker/run-docker-container.bash but runs in the background so
# commands can be sent with `docker exec`.
#
#   scripts/sim2sim_container.sh [start|stop|shell] [jazzy|humble]
set -e
here=$(cd "$(dirname "$0")/.." && pwd)
sample=${UNITY_ROS2_SAMPLE:-$(cd "$here/../Unity_ROS2_sample" && pwd)}
action=${1:-start}
distro=${2:-jazzy}
case "$distro" in humble) codename=jammy ;; jazzy) codename=noble ;; *) echo "distro?" >&2; exit 1 ;; esac
name="unirobolab-sim2sim-${distro}"
image="$(id -un)/ros-${distro}-${codename}-unity-sample"

case "$action" in
  start)
    if docker ps --format '{{.Names}}' | grep -qx "$name"; then echo "$name already running"; exit 0; fi
    xhost +local:root >/dev/null 2>&1 || true
    GPU_OPT=""; type nvidia-container-runtime >/dev/null 2>&1 && GPU_OPT="--gpus all"
    docker run -d --rm --net=host --ipc=host --privileged $GPU_OPT \
      -v /tmp/.X11-unix:/tmp/.X11-unix:rw \
      -v "$HOME/.Xauthority:/.Xauthority" \
      -v "$sample/colcon_ws:/home/unity/colcon_ws" \
      -v "$here:/home/unity/unirobolab" \
      -e XAUTHORITY=/.Xauthority -e DISPLAY="${DISPLAY:-:1}" -e QT_X11_NO_MITSHM=1 \
      -v /run/dbus/system_bus_socket:/run/dbus/system_bus_socket \
      --name "$name" "$image" -c "sleep infinity"
    echo "started $name"
    ;;
  stop)  docker stop "$name" ;;
  shell) docker exec -it "$name" bash ;;
  *) echo "usage: $0 [start|stop|shell] [jazzy|humble]" >&2; exit 1 ;;
esac
