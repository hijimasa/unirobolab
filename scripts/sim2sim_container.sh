#!/bin/bash
# Start (or reuse) the ROS 2 container for the deploy check (step 5), detached, with this repository
# and, when given, one project directory mounted. The project is mounted at the same absolute path
# as on the host because the host-side simulator must be able to open the URDF path sent over ROS.
# The image is self-contained (docker/Dockerfile: ros-base + the ROS 2 packages the check needs,
# built from pinned sources); it is built on first start.
#
#   scripts/sim2sim_container.sh [start|check|stop|shell|build|logs] [jazzy|humble] [project-dir]
set -e
here=$(cd "$(dirname "$0")/.." && pwd)
action=${1:-start}
distro=${2:-jazzy}
project_arg=${3:-}
name="unirobolab-sim2sim-${distro}"
image="$(id -un)/unirobolab-${distro}"
project_label="io.unirobolab.project"
build_image() {
  echo "building $image (first time: about 10 minutes, needs network access)"
  docker build --network=host -t "$image" --build-arg ROS_DISTRO_ARG="$distro" --build-arg UID="$(id -u)" --build-arg GID="$(id -g)" "$here/docker"
}

project_path() {
  [ -n "$project_arg" ] || { echo "project directory is required" >&2; return 1; }
  [ -d "$project_arg" ] || { echo "project directory does not exist: $project_arg" >&2; return 1; }
  local project
  project=$(cd "$project_arg" && pwd -P)
  [ "$project" != / ] || { echo "refusing to mount / as a project" >&2; return 1; }
  [ -f "$project/unirobolab.project.json" ] || { echo "not a UniRoboLab project: $project" >&2; return 1; }
  printf '%s\n' "$project"
}

start_container() {
  local project=${1:-} current=""
  if docker ps --format '{{.Names}}' | grep -qx "$name"; then
    # A container cannot gain another bind mount after creation. Recreate it only when the GUI
    # switches projects; a plain `start` continues to reuse any running container.
    [ -z "$project" ] && { echo "$name already running"; return 0; }
    current=$(docker inspect --format "{{ index .Config.Labels \"$project_label\" }}" "$name" 2>/dev/null || true)
    [ "$current" = "$project" ] && { echo "$name already running for $project"; return 0; }
    echo "switching $name project mount to $project"
    docker stop "$name" >/dev/null
  fi
  docker image inspect "$image" >/dev/null 2>&1 || build_image
  local mount_args=()
  if [ -n "$project" ]; then
    mount_args=(-v "$project:$project" --label "$project_label=$project")
  fi
  docker run -d --rm --net=host --ipc=host --name "$name" \
    -v "$here:/home/unity/unirobolab" "${mount_args[@]}" \
    "$image" -c "sleep infinity"
  # check_runner is addressed through /home/unity/unirobolab. This compatibility link also keeps
  # paths used by older GUI builds valid without exposing anything beyond the repository itself.
  docker exec -u root "$name" sh -c "mkdir -p '$(dirname "$here")' && ln -sfn /home/unity/unirobolab '$here'"
  echo "started $name${project:+ for $project}"
}

inside_project() {
  local path=$1 project=$2 resolved parent
  if [ -e "$path" ]; then
    resolved=$(realpath "$path")
  else
    parent=$(realpath "$(dirname "$path")") || return 1
    resolved="$parent/$(basename "$path")"
  fi
  [ "$resolved" = "$project" ] || [ "${resolved#"$project"/}" != "$resolved" ]
}

run_check() {
  local project value resolved
  project=$(project_path)
  for var in CONTRACT ONNX URDF; do
    value=${!var:-}
    [ -f "$value" ] || { echo "##FAIL $var is not a file: $value"; return 1; }
    inside_project "$value" "$project" || { echo "##FAIL $var must be inside the project: $value"; return 1; }
    resolved=$(realpath "$value")
    printf -v "$var" '%s' "$resolved"
  done
  if [ -n "${TASK:-}" ]; then
    [ -f "$TASK" ] || { echo "##FAIL TASK is not a file: $TASK"; return 1; }
    inside_project "$TASK" "$project" || { echo "##FAIL TASK must be inside the project: $TASK"; return 1; }
    TASK=$(realpath "$TASK")
  fi
  [ -n "${OUT:-}" ] || { echo "##FAIL OUT is not set"; return 1; }
  inside_project "$OUT" "$project" || { echo "##FAIL OUT must be inside the project: $OUT"; return 1; }
  mkdir -p "$OUT"
  OUT=$(realpath "$OUT")
  start_container "$project"
  docker exec "$name" env \
    CONTRACT="$CONTRACT" ONNX="$ONNX" URDF="$URDF" TASK="${TASK:-}" OUT="$OUT" \
    NS="${NS:-robot}" SPAWN_YAW="${SPAWN_YAW:-0}" SPAWN_X="${SPAWN_X:-0}" SPAWN_Y="${SPAWN_Y:-0}" \
    bash /home/unity/unirobolab/scripts/check_runner.sh
}

case "$action" in
  start)
    project=""
    [ -z "$project_arg" ] || project=$(project_path)
    start_container "$project"
    ;;
  check) run_check ;;
  stop)  docker stop "$name" ;;
  shell) docker exec -it "$name" bash ;;
  build) build_image ;;
  logs)  docker logs "$name" ;;
  *) echo "usage: $0 [start|check|stop|shell|build|logs] [jazzy|humble] [project-dir]" >&2; exit 1 ;;
esac
