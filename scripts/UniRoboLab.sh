#!/bin/bash
# Launch the UniRoboLab GUI (release layout: this file next to player/UniRoboLab.x86_64).
#   ./UniRoboLab.sh [project dir]
exec "$(dirname "$0")/scripts/unirobolab_gui.sh" "$@"
