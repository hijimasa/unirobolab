# UniRoboLab
English | [日本語](README-ja.md)

UniRoboLab is a GUI-first toolchain for training a robot policy in Unity and
getting it onto a ROS 2 robot: **train → generate a ROS 2 package → sim2sim
check → deploy**.

It is not another massively-parallel physics simulator. Isaac Lab, mjlab and
Genesis already do that well. UniRoboLab focuses on the part those tools
leave to the user: turning a trained policy into a ROS 2 node whose
observation/action wiring is guaranteed to match the environment it was
trained in, and proving it in simulation before touching hardware.

## What you get

- A single Unity binary that hosts the GUI, the simulator and sim2sim checks
  (ONNX inference runs inside Unity via Inference Engine; no Python needed).
- A training backend that is bundled with the binary and unpacked on first
  use. NVIDIA is not required; the torch wheel is chosen per GPU.
- A generated ROS 2 package (C++ node + ONNX Runtime) whose joint order,
  units, scaling and control rate come from one *policy contract* shared
  with the training environment.

## Install (Python side)

```bash
scripts/setup_python.sh              # uv venv at .venv with the runtime extras (gen, import, live)
scripts/setup_python.sh --training   # + stable-baselines3 and CPU torch for `unirobolab train`
```

The script installs uv if needed and prints the interpreter path for the simulator's
`unirobolab.policy_runner_command`. Training and sim2sim against the simulator use the
container from `scripts/sim2sim_container.sh` (ROS 2 Jazzy). Linux only so far.

## Quick start (servo demo, wiring-check policy)

```bash
cd python && uv venv .venv && uv pip install -e ".[dev]" && cd ..
python/.venv/bin/unirobolab make-test-policy contract/examples/servo_demo.json
python/.venv/bin/unirobolab gen contract/examples/servo_demo.json --out generated --overwrite
scripts/sim2sim_container.sh start          # ROS 2 Jazzy + simulator container (needs ../Unity_ROS2_sample)
scripts/sim2sim_container.sh build          # derived image: + onnxruntime, torch-cpu, stable-baselines3
```

Inside the container (`scripts/sim2sim_container.sh shell`): see
[docs/architecture.md](docs/architecture.md) §7 for the bring-up, build and
`unirobolab sim2sim` commands. The run ends with a PASS/FAIL table and
`generated/sim2sim_out/report.json`. Training (`unirobolab train`) is in §8.

## Status

M1 done, M2 first slice done: a policy trained with PPO inside the simulator is exported
to ONNX, packaged for ROS 2 and passes sim2sim on the servo demo (ideal joint error under
1 centiradian). Training does not go through ROS 2: the simulator's learning server (its
`learning-server` branch) steps K robots in one scene per round trip, 1,850 env steps per
second on the servo demo with 16 robots (400k PPO steps in under 4 minutes, settled error
0.006 rad, sim2sim PASS). ROS 2 stays the deployment and sim2sim path, both
as direct joint-command topics and through ros2_control (the generator also emits the
controller configuration). Base-state observations (velocity, gravity, goal in the body frame) work the same
way: a differential-drive robot trained to reach goals in 16-robot batches passes
sim2sim over ROS 2 with its pose coming from the ground-truth topic. Training stops early once a rolling success criterion holds (the diffbot run went
from 43 minutes to under 3), robots are spawned through the learning server so no ROS
graph is needed during training, and several simulator processes can be pooled (about 2x
on this machine: one process already fills most cores). See
[docs/unity6-gpu-physics-survey.md](docs/unity6-gpu-physics-survey.md) for why GPU
physics is not an option inside Unity 6. Policies trained elsewhere can be imported: `unirobolab import-isaaclab` builds a contract
from an Isaac Lab run's `env.yaml` (per-element scale/offset, Twist commands, IMU and
odometry inputs, cmd_vel outputs are all in the contract now), and sim2sim scenarios can
inject disturbances and random goals. A first GUI piece exists: the simulator's Policy panel runs a contract + ONNX pair on a
spawned robot without ROS (it launches `unirobolab live`, which talks to the learning
server). A tabbed UniRoboLab panel adds a contract editor with validation, training start/stop with a live
learning curve, and a sim2sim report viewer. Visual polish is still to come. See [docs/architecture.md](docs/architecture.md) (Japanese) for
the design, milestones and results.

## Layout

| Path | Purpose |
|---|---|
| `unity/UniRoboLab/` | Unity project (GUI, simulator, sim2sim). Simulator core is pulled from [Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator) as UPM git packages |
| `contract/` | Policy contract schema: the single source of truth for observations, actions and rates |
| `python/` | Python package `unirobolab`: `gen` (ROS 2 package generator), `make-test-policy`, `sim2sim` evaluator, `train` (placeholder) |
| `scripts/` | Container and simulator bring-up helpers for sim2sim |
| `docs/` | Design notes |

## License

Apache-2.0. UniRoboLab is an independent project and is not affiliated with
or endorsed by Unity Technologies.

## Tutorial

URDF to a deployable ROS 2 package through the GUI, with screenshots: [docs/tutorial.md](docs/tutorial.md) (Japanese)
