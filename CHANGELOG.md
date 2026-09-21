# Changelog

Dates are the day the work landed in this repository. Versions follow the Python package
(`python/pyproject.toml`), which is also the version of the release zips.

## 0.1.0 — 2026-09-21

First public release: the six-step wizard, from a URDF to a ROS 2 package that has been checked in
simulation. Linux (x86_64) and Windows (x64) zips contain the player, the Python package, the
scripts and the tutorial.

### The wizard

- **① robot**: load a URDF, see joints, limits and links; the ROS 2 connection (namespace, command
  topic or ros2_control controller, e-stop topic) is set once here and reused by ⑤ and ⑥.
- **② task**: define start and goal conditions in 3D instead of hunting for a matching example
  config. Four kinds: joint targets, a link into a region, an object pushed into a region, and a
  mobile base reaching a place. The contract and the training configuration are generated from it.
- **③ train**: PPO against the simulator's learning server (no ROS 2 involved), several robots in
  one scene, with success rate, error, remaining time and a curve. Stops early once the success
  target holds; keeps the best checkpoint rather than the last one.
- **④ try**: run the trained policy live in 3D, move the goal with sliders, put objects back.
- **⑤ check**: run the policy through ROS 2 exactly as the real robot would, judge PASS/FAIL per
  goal, and test that the e-stop suspends commanding and resumes.
- **⑥ deploy**: generate the ROS 2 package plus `DEPLOY.md`, the plain-language procedure with the
  topics to connect, the safety limits and the start-up commands.

### Training

- Relative (incremental) joint actions (`action_term.relative`), needed for manipulation: with
  absolute targets the pushing task stayed under 1 % success, with increments it reached 71 %.
- Domain randomization: start pose (with a curriculum that widens the spread as success holds) and
  physics (object mass, friction, drive gain), plus history-window policies that infer the
  situation from recent observations. On the pushing task, physics randomization with a window of 8
  gave the best policy (70.8 % nominal, 48.3 % under randomization).
- `unirobolab eval` scores a policy under conditions other than the ones it trained in.
- Checkpoints every 50k steps, so a simulator crash does not lose the run.

### Checking and deployment

- Self-contained ROS 2 container (`docker/Dockerfile`, about 1.3 GB): ros-base plus the ROS-TCP
  endpoint, `simulation_interfaces`, `simulation_ros2_utils` and `topic_based_ros2_control` built
  from pinned sources. Built on first use; no other repository needs to be cloned.
- The container shares only the current project directory, mounted at its host path, so projects
  can live anywhere.
- Object tasks are checked with the object pose republished the way a camera or mocap bridge would
  on the real robot.
- ⑥ refuses to generate while the latest ⑤ result is missing, failed, or stale, so a package cannot
  quietly be built from an unverified policy.

### Failure handling

- Training, the live run and the check summarise a failed child process into a cause and a recovery
  step in the status line (communication loss, port conflict, missing Python environment, missing
  inputs) instead of only pointing at the log.

### Notes for people who built from source before

- The simulator core is now a pinned UPM git dependency
  (`REACT-ROBOT/Unity_ROS2_Robot_Simulator` at `fcd2985`), not a local `file:` path.
- **Breaking change in the simulator**: a robot whose URDF does not name its topics now listens for
  commands on `/joint_command`, not `/joint_states`. The two used to collide, and the robot fed its
  own state back as a command, so position commands were ignored.
- The check container no longer derives from the `Unity_ROS2_sample` image and no longer mounts its
  workspace.

### Known limits

- One goal condition per task; grasping is not supported (pushing only).
- ⑤ and the container are Linux-only. The Windows player is built and starts, but its helper
  commands run through Git for Windows' `bash.exe` and have not been exercised on real hardware.
- Evaluating a trained policy varies by roughly ±10 % over 40 episodes.
- Open points from the first outside UX review (`docs/reviews/2026-09-20-ux-review-result.md`):
  the 3D view in ② does not always frame the robot together with the goal, the tutorial does not
  explain the coordinate frames or how to pick the real robot's ROS 2 settings, and some units and
  labels in ③ and ⑤ are still ambiguous.
