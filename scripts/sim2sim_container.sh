#!/bin/bash
# Start (or reuse) the ROS 2 container for the deploy check (step 5), detached, with this repository
# mounted at the same path as on the host. The image is self-contained (docker/Dockerfile: ros-base +
# the ROS 2 packages the check needs, built from pinned sources); it is built on first start.
#
#   scripts/sim2sim_container.sh [start|stop|shell|build|logs] [jazzy|humble]
set -e
here=$(cd "$(dirname "$0")/.." && pwd)
action=${1:-start}
distro=${2:-jazzy}
name="unirobolab-sim2sim-${distro}"
image="$(id -un)/unirobolab-${distro}"
build_image() {
  echo "building $image (first time: about 10 minutes, needs network access)"
  docker build --network=host -t "$image" --build-arg ROS_DISTRO_ARG="$distro" --build-arg UID="$(id -u)" --build-arg GID="$(id -g)" "$here/docker"
}
case "$action" in
  start)
    if docker ps --format '{{.Names}}' | grep -qx "$name"; then echo "$name already running"; exit 0; fi
    docker image inspect "$image" >/dev/null 2>&1 || build_image
    docker run -d --rm --net=host --ipc=host --name "$name" \
      -v "$here:/home/unity/unirobolab" \
      "$image" -c "sleep infinity"
    # The GUI's check passes host paths (contract, URDF, output folder): make them valid inside the
    # container too (the simulator on the host opens the spawn URDF by that path).
    docker exec -u root "$name" sh -c "mkdir -p '$(dirname "$here")' && ln -sfn /home/unity/unirobolab '$here'"
    echo "started $name"
    ;;
  stop)  docker stop "$name" ;;
  shell) docker exec -it "$name" bash ;;
  build) build_image ;;
  logs)  docker logs "$name" ;;
  *) echo "usage: $0 [start|stop|shell|build|logs] [jazzy|humble]" >&2; exit 1 ;;
esac
