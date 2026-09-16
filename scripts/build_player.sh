#!/bin/bash
# Build the UniRoboLab player (Linux by default) from unity/UniRoboLab.
# TextMeshPro essentials (Assets/TextMesh Pro: TMP Settings, default font, shaders) are committed;
# without them TMP text throws NullReferenceException at runtime.
# The simulator core comes from ../Unity_ROS2_Robot_Simulator/Packages/SimulationCore
# (file: dependency in unity/UniRoboLab/Packages/manifest.json).
#   scripts/build_player.sh [output] [linux|windows]
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-$ROOT/generated/player/UniRoboLab.x86_64}
TARGET=${2:-linux}
UNITY=${UNITY:-$(ls -d ~/Unity/Hub/Editor/6000.*/Editor/Unity 2>/dev/null | tail -1)}
LOG=${BUILD_LOG:-$ROOT/generated/log/build_player.log}
mkdir -p "$(dirname "$OUT")" "$(dirname "$LOG")"
rm -f "$ROOT/unity/UniRoboLab/Assets/UniRoboLab/Scenes/Lab.unity"   # always regenerate the scene from LabSceneBuilder
"$UNITY" -batchmode -nographics -quit -projectPath "$ROOT/unity/UniRoboLab" \
  -executeMethod BuildPlayer.Build -buildOutput "$OUT" -playerTarget "$TARGET" -logFile "$LOG" || {
  echo "build failed, see $LOG"; grep -E "error CS|Exception" "$LOG" | sort -u | head -20; exit 1; }
grep -E "\[BuildPlayer\]" "$LOG" | tail -1
