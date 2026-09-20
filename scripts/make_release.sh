#!/bin/bash
# Assemble release zips: the player plus everything the GUI needs at run time (Python package,
# scripts, contract schema, tutorial). The player finds the Python package by walking up from its
# own folder, so the zip keeps the repository layout: <name>/player/ next to <name>/python/.
#
#   scripts/make_release.sh [--linux-only|--windows-only] [--version X.Y.Z]
# Inputs: generated/player/ (scripts/build_player.sh ... linux) and generated/player_win/ (... windows).
# Output: generated/release/UniRoboLab-<version>-<os>.zip
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=$(sed -n 's/^version = "\(.*\)"/\1/p' "$ROOT/python/pyproject.toml")
TARGETS="linux windows"
while [ $# -gt 0 ]; do
  case "$1" in
    --linux-only) TARGETS="linux"; shift ;;
    --windows-only) TARGETS="windows"; shift ;;
    --version) VERSION=$2; shift 2 ;;
    *) echo "usage: $0 [--linux-only|--windows-only] [--version X.Y.Z]" >&2; exit 1 ;;
  esac
done
OUT="$ROOT/generated/release"; mkdir -p "$OUT"
for os in $TARGETS; do
  case $os in
    linux)   SRC="$ROOT/generated/player"; NAME="UniRoboLab-$VERSION-linux-x86_64" ;;
    windows) SRC="$ROOT/generated/player_win"; NAME="UniRoboLab-$VERSION-windows-x86_64" ;;
  esac
  [ -d "$SRC" ] || { echo "no player for $os at $SRC (scripts/build_player.sh <out> $os)"; exit 1; }
  STAGE="$OUT/$NAME"; rm -rf "$STAGE"; mkdir -p "$STAGE/player" "$STAGE/docs/images" "$STAGE/scripts"
  cp -r "$SRC"/. "$STAGE/player/"
  rm -rf "$STAGE"/player/*_BurstDebugInformation_DoNotShip
  cp -r "$ROOT/python" "$STAGE/python"; rm -rf "$STAGE/python/tests" "$STAGE/python/.venv"; find "$STAGE/python" -name __pycache__ -type d -exec rm -rf {} + 2>/dev/null || true
  cp -r "$ROOT/contract" "$STAGE/contract"
  cp -r "$ROOT/docker" "$STAGE/docker"
  for f in unirobolab_gui.sh setup_python.sh setup_python.ps1 sim2sim_container.sh check_runner.sh gui_resources.json; do cp "$ROOT/scripts/$f" "$STAGE/scripts/"; done
  cp "$ROOT/docs/tutorial.md" "$STAGE/docs/"; cp -r "$ROOT/docs/images/tutorial2" "$STAGE/docs/images/"
  cp "$ROOT/LICENSE" "$STAGE/"
  cp "$ROOT/release/README.md" "$STAGE/README.md"; cp "$ROOT/release/README-ja.md" "$STAGE/README-ja.md"
  if [ $os = linux ]; then cp "$ROOT/scripts/UniRoboLab.sh" "$STAGE/UniRoboLab.sh"; chmod +x "$STAGE/UniRoboLab.sh" "$STAGE"/scripts/*.sh "$STAGE/player/UniRoboLab.x86_64"; fi
  if [ $os = windows ]; then cp "$ROOT/scripts/UniRoboLab.cmd" "$STAGE/UniRoboLab.cmd"; fi
  echo "$VERSION" > "$STAGE/VERSION"
  (cd "$OUT" && rm -f "$NAME.zip" && python3 -c "import shutil; shutil.make_archive('$NAME', 'zip', '.', '$NAME')")
  echo "wrote $OUT/$NAME.zip ($(du -sh "$OUT/$NAME.zip" | cut -f1))"
done
