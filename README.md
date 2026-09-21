# UniRoboLab
English | [日本語](README-ja.md)

UniRoboLab is a GUI-first toolchain for training a robot policy in a Unity simulator and
getting it onto a ROS 2 robot. One window walks through six steps:

**① robot** (load a URDF) → **② task** (define start and goal conditions in 3D) → **③ train**
(PPO against the simulator's learning server, no ROS 2 needed) → **④ try** (run the policy live
in 3D) → **⑤ check** (run it through ROS 2 as the real robot would, judge PASS/FAIL, test the
e-stop) → **⑥ deploy** (a generated ROS 2 package plus a plain-language deployment guide).

The interface is English or Japanese, switched from the header and remembered between runs.

It is not another massively-parallel physics simulator; Isaac Lab, mjlab and Genesis already do
that well. UniRoboLab focuses on what those tools leave to the user: turning a policy into a ROS 2
node whose observation/action wiring is guaranteed to match the environment it was trained in,
and proving it in simulation before touching hardware. The single source of truth is the
*policy contract* (`contract/`), from which both the training environment and the generated
node are built.

## Getting it

- **Releases** (Linux x86_64, Windows x64): a zip with the player, the Python package and the
  tutorial. See the README inside the zip ([release/README.md](release/README.md)) for setup.
- **Build it yourself**: below.

## Build from source

Requirements:

- Unity **6000.3.21f1** (Unity Hub) with *Linux Build Support (Mono)* and, for the Windows player,
  *Windows Build Support (Mono)*. Building for Windows from a Linux host works.
- Network access on the first build: the simulator core and the other Unity packages are UPM git
  dependencies pinned by commit in `unity/UniRoboLab/Packages/manifest.json`
  ([Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator)
  `Packages/SimulationCore` and friends, the `hijimasa` forks of ROS-TCP-Connector, URDF-Importer
  and UnitySensors).
- Python 3.10+ is not required to build, only to run (`scripts/setup_python.sh`).

```bash
git clone https://github.com/hijimasa/unirobolab.git && cd unirobolab
scripts/build_player.sh                                        # -> generated/player/UniRoboLab.x86_64
scripts/build_player.sh generated/player_win/UniRoboLab.exe windows   # Windows player (optional)
scripts/setup_python.sh --training                             # .venv with the training backend
scripts/unirobolab_gui.sh ~/unirobolab_projects/first          # run the GUI
```

`build_player.sh` finds the editor under `~/Unity/Hub/Editor/6000.*`; set `UNITY` to override. The
scene is generated at build time (`Assets/UniRoboLab/Editor/LabSceneBuilder.cs`), so there is
nothing to open in the editor for a normal build. The build log is `generated/log/build_player.log`.

Release zips (player + `python/` + `scripts/` + `contract/` + tutorial, in the layout the player
expects at run time): `scripts/make_release.sh` after building both players.

Step ⑤ needs ROS 2. Without it on the host, `scripts/sim2sim_container.sh start` runs a self-contained
ROS 2 Jazzy container (`docker/Dockerfile`: ros-base plus the ROS-TCP endpoint, simulation_interfaces,
simulation_ros2_utils and topic_based_ros2_control built from pinned sources; about 1.3 GB). The image
is built on the first start (about 10 minutes, network needed); the ⑤ screen has a button for it.

Tests:

```bash
PYTHONPATH=python/src pytest python/tests          # the Python package (no simulator needed)
scripts/tests/test_sim2sim_container.sh            # the check container's helper script
scripts/tests/test_wizard_flow.sh                  # ①→②→③ is reachable (needs a built player)
<unity> -batchmode -runTests -testPlatform EditMode -projectPath unity/UniRoboLab   # the GUI's logic
```

## Tutorial

URDF to a deployable ROS 2 package through the GUI, with screenshots:
[docs/tutorial.md](docs/tutorial.md) (Japanese). It covers a servo (joint targets), an arm
pushing an object, and the settings for start-pose and physics randomization.

## Command line

Everything the GUI does is a `unirobolab` subcommand (`.venv/bin/unirobolab --help`): `robot-info`,
`task-preset`, `task-gen`, `train`, `train-status`, `live`, `eval`, `scenario-default`, `gen`,
`sim2sim`, `explain-report`, `deploy-guide`, `import-isaaclab` (build a contract from an Isaac Lab
run's `env.yaml`), `make-test-policy`.

## Status

Verified end to end on this machine (see [docs/architecture.md](docs/architecture.md), Japanese):

- joint-target policies (servo demo) and a differential-drive goal-reaching policy through ①〜⑥,
  including ros2_control deployment;
- link-target (end-effector) and object-pushing tasks on a planar arm: training with relative
  actions, domain randomization of start pose and physics, history-window policies, and the ⑤ check
  with the object pose republished the way a camera would on the real robot;
- training throughput around 1,800 env steps/s for 16 servos and 280 env steps/s for 8 arms with
  objects, on the CPU.

Known limits: one goal condition per task; no grasping (pushing only); ⑤ is Linux-only; the Windows
player runs its helpers through Git for Windows' `bash.exe` and has not been exercised on real
hardware yet; evaluation of a trained policy varies by about ±10 % over 40 episodes. Open points
from the first outside UX review are listed in [CHANGELOG.md](CHANGELOG.md) and
[docs/reviews/2026-09-20-ux-review-result.md](docs/reviews/2026-09-20-ux-review-result.md).

## Layout

| Path | Purpose |
|---|---|
| `unity/UniRoboLab/` | Unity project: the wizard GUI (`Assets/UniRoboLab/Scripts`), scene builder and player build (`Assets/UniRoboLab/Editor`). The simulator core comes from Unity_ROS2_Robot_Simulator as UPM git packages |
| `contract/` | Policy contract schema and examples: observations, actions, rates, safety limits, ROS topics |
| `python/` | Package `unirobolab`: task spec → contract + training config, PPO trainer, live runner, ROS 2 package generator, sim2sim evaluator, deployment guide |
| `scripts/` | Player build, GUI launcher, Python setup (bash / PowerShell), ROS 2 container, ⑤ runner, release assembly |
| `release/` | README files shipped inside the release zips |
| `docker/` | Self-contained ROS 2 image for the ⑤ check (ros-base + the ROS 2 packages the check needs, built from pinned sources) |
| `docs/` | Design notes and results (`architecture.md`), UX flow (`ux-flow.md`), tutorial and screenshots |

## License

Apache-2.0. UniRoboLab is an independent project and is not affiliated with
or endorsed by Unity Technologies.
