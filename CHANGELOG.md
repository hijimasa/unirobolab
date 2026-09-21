# Changelog

Dates are the day the work landed in this repository. Versions follow the Python package
(`python/pyproject.toml`), which is also the version of the release zips.

## 0.2.0 — 2026-09-22

Two additions on top of 0.1.0, both about understanding and using the tool rather than new robot
capability.

### Seeing what training is doing

Success rate and error say whether it works, not why. Step ③ gained two views.

- **Reward breakdown**: the per-attempt contribution of every reward and penalty term, drawn over
  time with a legend in plain words ("distance from hand to object", "jerky commands") and the
  trend against the previous window. The data was already in `progress.csv` and was not shown.
- **Inside the learner** (under Details): the policy's spread, how well the value function fits, and
  how far each update moves the policy, each with a line saying what it means. They are also
  recorded per update in `runs/<run>/updates.csv`.
- New advice is derived from both: penalties outweighing the reward, exploration collapsing early,
  the value function not fitting, and updates that are too large.

### English or Japanese

- The header has a language button. It rebuilds the screen in the other language and remembers the
  choice in `~/.config/unirobolab/language.txt`, so the next run starts in it. Training and live
  runs keep going across a switch.
- The text the Python side produces follows the same choice, including the training-time estimate
  in step ②, which used to stay Japanese.

## 0.1.0 — 2026-09-22

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
- Robots in a parallel run are spaced by what the task needs, so they no longer drive into or push
  each other's workspace (a wheeled robot with 2.5 m goals needs 6 m, not the old fixed 0.6 m).

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

### From the first outside UX review

An outside agent ran the three robots through the wizard with only the tutorial to go on
(`docs/reviews/2026-09-20-ux-review-result.md`). What its 28 findings changed:

- **Step ⑤ runs wherever the project is.** The container used to reach only projects inside the
  repository, which stopped the review after ①〜④. It now mounts the selected project at its own
  path, and ⑥ refuses to generate while the latest ⑤ result is missing, failed or stale.
- **A failed child process says why.** Training, the live run and the check summarise the cause and
  the next step in the status line (communication loss, port conflict, missing Python environment,
  missing inputs) instead of only pointing at a log.
- **Units everywhere.** The check result of an object task said "rad" for a distance in metres; the
  training status line gave an error and a target with no unit at all. Both now say `m` or `rad`.
- **The training graph fits the run.** Its y axis was capped at five times the tolerance, so a run
  whose error was still far above it drew every point stuck to the top edge.
- **The estimate matches the measurement.** Step ② assumed one training speed for every robot, so a
  wheeled robot was told "about 2 minutes" for a run that takes 40. Speeds are now per task kind.
- **Step ② shows the robot.** The screen could open on an empty floor (⑤ removes the entities), and
  the goal was framed without the robot, so reachability could not be judged. It now spawns a
  preview robot and frames both, from an angle that shows the structure.
- **Text no longer disappears.** On a crowded screen the labels of buttons and sliders were blank:
  a fixed-height wrapped label squeezed the column, and a truncating text with too little room
  draws nothing. Wrapped labels size themselves, single-line text overflows instead of vanishing,
  and the Japanese font atlas is baked at build time rather than rasterised at run time.
- **A URDF without materials is grey, not magenta.** Meshes with no usable material get a neutral
  one, so a missing material no longer looks like a failed import.
- **Smaller things**: continuous joints say so instead of showing a 0.00 .. 0.00 range; the check
  reports progress in the interface language; "Next" is greyed with the missing prerequisite next
  to it; step ⑥ restores what it generated when you come back; step ② says when a change has not
  reached the generated files yet; the tutorial explains how to pick the robot's ROS 2 settings and
  how to verify them, what the coordinate frames and the three "size" fields mean, and how to get
  the generated package onto the robot.

### Known limits

- One goal condition per task; grasping is not supported (pushing only).
- ⑤ and the container are Linux-only. The Windows player is built and starts, but its helper
  commands run through Git for Windows' `bash.exe` and have not been exercised on real hardware.
- Evaluating a trained policy varies by roughly ±10 % over 40 episodes.
- Parallel training spaces the robots from the task (reach, travel, object placement), so a
  wheeled robot with distant goals needs a wide grid and looks small in the 3D view.
