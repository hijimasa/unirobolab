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

## Status

Skeleton. See [docs/architecture.md](docs/architecture.md) (Japanese) for
the design and milestones.

## Layout

| Path | Purpose |
|---|---|
| `unity/UniRoboLab/` | Unity project (GUI, simulator, sim2sim). Simulator core is pulled from [Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator) as UPM git packages |
| `contract/` | Policy contract schema: the single source of truth for observations, actions and rates |
| `trainer/` | Python training backend (uv-managed, bundled with the binary) |
| `templates/ros2_policy_node/` | Template of the generated ROS 2 package |
| `docs/` | Design notes |

## License

Apache-2.0. UniRoboLab is an independent project and is not affiliated with
or endorsed by Unity Technologies.
