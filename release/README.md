# UniRoboLab

English | [日本語](README-ja.md)

UniRoboLab trains a robot policy in a Unity simulator and takes it to a ROS 2 robot in six
steps, all from one window: **① robot → ② task → ③ train → ④ try → ⑤ check → ⑥ deploy**.
The only file you supply is a URDF: from it the tool generates the training setup, trains a policy,
lets you try it in 3D, checks it against a ROS 2 simulation, and writes the ROS 2 package and the
deployment notes. The machine still needs what the table below lists, and steps ⑤ and ⑥ need the
robot's ROS 2 details (namespace, command topic or controller, e-stop topic), which you enter in ①.

The step-by-step guide with screenshots is [docs/tutorial.md](docs/tutorial.md).

## Requirements

| | Linux (x86_64) | Windows (x64) |
|---|---|---|
| Steps ① to ④ (train and try) | Ubuntu 22.04 / 24.04, a GPU with Vulkan or OpenGL | Windows 10 / 11, **Git for Windows** (the tool runs its helpers through `bash.exe`), PowerShell |
| Step ⑤ (check through ROS 2) | ROS 2 Jazzy on this machine, **or** Docker (the tool builds a 1.3 GB ROS 2 image on first use) | not available yet (use a Linux machine or WSL 2 with the Linux zip) |
| Step ⑥ (deploy) | a ROS 2 workspace on the robot side | same |

Python is installed by the setup script into this folder (`.venv`) through [uv](https://astral.sh/uv);
nothing else on the machine is touched. Training runs on the CPU; no NVIDIA card is needed.

## Setup (once)

Linux:

```bash
unzip UniRoboLab-*-linux-x86_64.zip && cd UniRoboLab-*-linux-x86_64
scripts/setup_python.sh --training        # creates .venv (a few minutes the first time)
./UniRoboLab.sh ~/unirobolab_projects/first    # the argument is the project folder (created if missing)
```

Windows (PowerShell):

```powershell
Expand-Archive UniRoboLab-*-windows-x86_64.zip; cd UniRoboLab-*-windows-x86_64
powershell -ExecutionPolicy Bypass -File scripts\setup_python.ps1 -Training
.\UniRoboLab.cmd $HOME\unirobolab_projects\first
```

Then follow [docs/tutorial.md](docs/tutorial.md) from step ①. The window shows the six steps at the
top; the current one is blue, finished ones green. The status line at the bottom tells you what to do
next and why a button is disabled.

For step ⑤ on Linux without ROS 2 installed: install Docker (your user must be able to run `docker`),
then press "Start container" on the ⑤ screen or run `scripts/sim2sim_container.sh start`. The first
start builds the ROS 2 image from `docker/Dockerfile` (about 10 minutes, network needed); later
starts take seconds.

## What is in this folder

| Path | Purpose |
|---|---|
| `UniRoboLab.sh` / `UniRoboLab.cmd` | starts the GUI (`player/`) with the default settings |
| `player/` | the Unity application (GUI, simulator, learning server on port 10100) |
| `python/` | the `unirobolab` package: task generation, training, live runs, ROS 2 package generation, checks |
| `scripts/` | `setup_python.sh` / `.ps1`, `sim2sim_container.sh` (ROS 2 container for ⑤), `check_runner.sh` |
| `contract/` | the policy contract schema (what the policy sees and outputs) and examples |
| `docs/` | the tutorial and its screenshots |
| `.venv/` | created by the setup script; delete it to start over |
| `CHANGELOG.md` | what is in this version, and the known limits |

Projects (task, contract, training runs, reports) live in the folder you pass at start-up, not here.

## Ports and files the tool uses

- `localhost:10100` — the simulator's learning server (training and ④ connect to it). Change it with
  the `SIM_LEARNING_PORT` environment variable or `settings.learning_port` in `scripts/gui_resources.json`.
- `localhost:10000` — the ROS-TCP endpoint during ⑤ (started inside the container or on the host).
- `~/.config/unirobolab/recent.txt` — the last project folder, so starting without an argument reopens it.

## Troubleshooting

| Symptom | What to do |
|---|---|
| "Python environment not found" in the status line | run `scripts/setup_python.sh --training` (Windows: `setup_python.ps1 -Training`); the GUI looks for `.venv` in this folder |
| The player does not start on Linux | `chmod +x player/UniRoboLab.x86_64`; the machine needs a working Vulkan or OpenGL driver |
| Windows shows "bash.exe not found" | install Git for Windows (default options) and start again |
| Windows SmartScreen blocks the app | the binary is not signed; choose "More info → Run anyway" |
| ⑤ says ROS 2 is not available | see the ⑤ notes under Setup; on Windows use a Linux machine for this step |
| Training is slower than the estimate | the estimate assumes an idle machine; the simulator uses most cores |

Logs: the GUI writes `train.log`, `check_runner.log` and the reports into the project folder;
the player's own log is `~/.config/unity3d/UniRoboLab/UniRoboLab/Player.log` on Linux and
`%USERPROFILE%\AppData\LocalLow\UniRoboLab\UniRoboLab\Player.log` on Windows.

## License

UniRoboLab is released under the Apache License 2.0 (see `LICENSE`). The Japanese text is rendered
with a font atlas baked from the system font found on the build machine (Noto Sans CJK, SIL Open
Font License 1.1). It bundles the Unity runtime
(Unity Technologies, under the Unity Terms of Service) and packages under the Apache License 2.0:
[Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator),
ROS-TCP-Connector and URDF-Importer (Unity Technologies, forks by hijimasa). UniRoboLab is an
independent project and is not affiliated with or endorsed by Unity Technologies.

Source: https://github.com/hijimasa/unirobolab
