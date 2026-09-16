#!/usr/bin/env python3
"""Contract-driven policy node.

This file is copied verbatim into every generated ROS 2 package. It has no
generated code in it: everything robot-specific (joint order, topics, scaling,
rate, ONNX file) comes from the policy contract it loads at start-up. The same
file therefore runs a policy in sim2sim and on the real robot, which is the
point: there is exactly one implementation of the observation/action wiring.

It deliberately avoids importing the ``unirobolab`` package so the generated
package depends only on rclpy, numpy and onnxruntime.

Topics (relative to the contract's ``ros`` section):
  sub  joint_states_topic         sensor_msgs/JointState
  sub  goal_topic                 std_msgs/Float64MultiArray   (the 'command' observation, or world x,y for base_goal_xy;
                                  geometry_msgs/Twist when the command term has ros_type: twist)
  sub  imu_topic / odom_topic     sensor_msgs/Imu / nav_msgs/Odometry (base_* observations on a real robot)
  pub  cmd_vel_topic              geometry_msgs/Twist          (base_twist actions)
  sub  safety.estop_topic         std_msgs/Bool                (true = suspend commanding)

Safety (contract "safety"): stale observations or e-stop suspend commanding (stop_action hold|zero);
resuming ramps in from the measured state over ramp_in_s; position targets are rate-limited
by max_joint_speed and clamped to joint_limits; velocity/effort/base speeds are clamped.
  sub  ground_truth_topic         geometry_msgs/PoseStamped    (base_* observations; velocity by finite difference)
  pub  command_topic              sensor_msgs/JointState       (command_mode=joint_state_topic)
       /<ns>/<controller>/commands std_msgs/Float64MultiArray  (command_mode=ros2_control_commands)
  pub  <goal_topic dir>/status    std_msgs/String (JSON, 1 Hz)
  pub  <goal_topic dir>/observation, .../action  std_msgs/Float64MultiArray (debug)
"""

from __future__ import annotations

import collections
import json
import os
import time

import numpy as np
import rclpy
from rclpy.node import Node
from rclpy.qos import QoSProfile, ReliabilityPolicy, HistoryPolicy
from geometry_msgs.msg import PoseStamped, Twist
from nav_msgs.msg import Odometry
from sensor_msgs.msg import Imu
from sensor_msgs.msg import JointState
from std_msgs.msg import Bool, Float64MultiArray, String

SUPPORTED_OBS = {"joint_position", "joint_velocity", "joint_effort", "command", "last_action",
                 "base_lin_vel", "base_ang_vel", "projected_gravity", "imu_orientation", "base_goal_xy"}


def quat_to_rot(q) -> np.ndarray:
    """Rotation matrix of quaternion (x, y, z, w)."""
    x, y, z, w = (float(v) for v in q)
    n = x * x + y * y + z * z + w * w
    if n < 1e-12:
        return np.eye(3)
    s = 2.0 / n
    return np.array([
        [1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w)],
        [s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w)],
        [s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y)],
    ])


def process_obs_term(v: np.ndarray, spec: dict) -> np.ndarray:
    """Contract order for observations: deadband -> *scale + offset -> clip."""
    v = np.asarray(v, dtype=np.float32).copy()
    db = float(spec.get("deadband", 0.0))
    if db > 0:
        v[np.abs(v) < db] = 0.0
    v = v * np.asarray(spec.get("scale", 1.0), dtype=np.float32) + np.asarray(spec.get("offset", 0.0), dtype=np.float32)
    clip = spec.get("clip")
    if clip:
        v = np.clip(v, clip[0], clip[1])
    return v


def process_action_term(raw: np.ndarray, spec: dict) -> tuple[np.ndarray, np.ndarray]:
    """Contract order for actions: clip -> *scale + offset. Returns (clipped_raw, target)."""
    a = np.asarray(raw, dtype=np.float32).copy()
    clip = spec.get("clip")
    if clip:
        a = np.clip(a, clip[0], clip[1])
    target = a * np.asarray(spec.get("scale", 1.0), dtype=np.float32) + np.asarray(spec.get("offset", 0.0), dtype=np.float32)
    return a, target


class PolicyNode(Node):
    def __init__(self) -> None:
        super().__init__("policy_node")
        self.declare_parameter("contract_path", "")
        self.declare_parameter("onnx_path", "")
        self.declare_parameter("namespace_override", "")
        self.declare_parameter("publish_debug", True)

        contract_path = self.get_parameter("contract_path").value
        if not contract_path:
            raise RuntimeError("contract_path parameter is required")
        with open(contract_path, encoding="utf-8") as f:
            self.c = json.load(f)
        self.contract_dir = os.path.dirname(os.path.abspath(contract_path))
        self.errors: list[str] = []

        self.joints: list[str] = list(self.c["robot"]["joints"])
        self.rate_hz = float(self.c["control"]["policy_rate_hz"])
        pol = self.c["policy"]
        self.history = int(pol.get("history_length", 1))
        self.input_name = pol.get("input_name", "obs")
        self.output_name = pol.get("output_name", "actions")

        # --- layouts -----------------------------------------------------
        self.actions = self._layout(self.c["actions"], self._action_size)
        self.action_dim = sum(n for _, n, _ in self.actions)
        self.observations = self._layout(self.c["observations"], self._obs_size)
        self.obs_dim = sum(n for _, n, _ in self.observations)
        for spec, _, _ in self.observations:
            if spec["source"] not in SUPPORTED_OBS:
                self._fail(f"observation source {spec['source']!r} not supported by this node")

        # --- topics ------------------------------------------------------
        ros = self.c.get("ros") or {}
        ns = self.get_parameter("namespace_override").value or ros.get("namespace", "")
        prefix = f"/{ns}" if ns else ""
        self.joint_states_topic = ros.get("joint_states_topic", f"{prefix}/joint_states")
        self.command_topic = ros.get("command_topic", f"{prefix}/joint_command")
        self.command_mode = ros.get("command_mode", "joint_state_topic")
        self.goal_topic = ros.get("goal_topic", f"{prefix}/policy/command")
        self.controller_name = ros.get("controller_name")
        self.ground_truth_topic = ros.get("ground_truth_topic", f"{prefix}/ground_truth")
        self.imu_topic = ros.get("imu_topic")
        self.odom_topic = ros.get("odom_topic")
        self.cmd_vel_topic = ros.get("cmd_vel_topic", f"{prefix}/cmd_vel")
        self.cmd_term = next((spec for spec, _, _ in self.observations if spec["source"] == "command"), None)
        self.cmd_is_twist = bool(self.cmd_term and self.cmd_term.get("ros_type") == "twist")
        self.twist_action = next((spec for spec, _, _ in self.actions if spec["target"] == "base_twist"), None)

        # --- safety (contract "safety" section) ------------------------------
        sf = self.c.get("safety") or {}
        self.max_obs_age = float(sf.get("max_obs_age_s", 3.0 / self.rate_hz))
        self.joint_limits = {j: (float(v[0]), float(v[1])) for j, v in (sf.get("joint_limits") or {}).items()}
        self.max_speed = self._per_joint(sf.get("max_joint_speed"))
        self.max_effort = self._per_joint(sf.get("max_joint_effort"))
        self.max_base_speed = sf.get("max_base_speed")
        self.ramp_in_s = float(sf.get("ramp_in_s", 1.0))
        self.stop_action = sf.get("stop_action", "hold")
        self.estop_topic = sf.get("estop_topic", f"{prefix}/policy/estop")
        self.estop = False
        self.safe_stopped = False      # commanding suspended (stale observations or e-stop)
        self.safe_stop_reason = ""
        self.ramp_start: float | None = None   # monotonic time the current ramp-in started
        self.last_target: dict[str, float] = {}
        self.safety_events = 0
        self.ramp_alpha = 1.0
        self.base_wall_time = 0.0
        self.needs_base = any(spec["source"] in ("base_lin_vel", "base_ang_vel", "projected_gravity",
                                                  "imu_orientation", "base_goal_xy")
                              for spec, _, _ in self.observations)
        status_base = self.goal_topic.rsplit("/", 1)[0] if "/" in self.goal_topic else ""

        # --- ONNX --------------------------------------------------------
        onnx_path = self.get_parameter("onnx_path").value or pol["onnx"]
        if not os.path.isabs(onnx_path):
            onnx_path = os.path.join(self.contract_dir, onnx_path)
        import onnxruntime as ort  # imported late so a missing install is a clear error
        self.sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        in_shape = self.sess.get_inputs()[0].shape
        out_shape = self.sess.get_outputs()[0].shape
        want_in = self.obs_dim * self.history
        if isinstance(in_shape[-1], int) and in_shape[-1] != want_in:
            self._fail(f"ONNX input dim {in_shape[-1]} != contract {want_in}")
        if isinstance(out_shape[-1], int) and out_shape[-1] != self.action_dim:
            self._fail(f"ONNX output dim {out_shape[-1]} != contract {self.action_dim}")

        # --- state -------------------------------------------------------
        self.js: JointState | None = None
        self.js_time: float = 0.0
        self.js_index: list[int] | None = None
        self.goal = np.zeros(self._goal_size(), dtype=np.float32)
        self.goal_received = False
        # base state from the pose topic: velocity by finite difference of consecutive poses
        self.base_pos = np.zeros(3); self.base_quat = np.array([0.0, 0.0, 0.0, 1.0])
        self.base_lin_vel = np.zeros(3); self.base_ang_vel = np.zeros(3)
        self.base_time: float | None = None
        self.base_received = False
        # velocities already in the body frame when they come from IMU/odometry
        self.body_lin_vel: np.ndarray | None = None
        self.body_ang_vel: np.ndarray | None = None
        self.last_action = np.zeros(self.action_dim, dtype=np.float32)
        self.frames: collections.deque = collections.deque(maxlen=self.history)
        self.steps = 0
        self.tick_times: collections.deque = collections.deque(maxlen=int(self.rate_hz * 2) + 2)
        self.infer_ms = 0.0
        self.obs_ages: list[float] = []  # joint_states age at each tick, cleared every status

        qos = QoSProfile(reliability=ReliabilityPolicy.RELIABLE,
                         history=HistoryPolicy.KEEP_LAST, depth=10)
        self.create_subscription(JointState, self.joint_states_topic, self._on_js, qos)
        goal_topic = (self.cmd_term or {}).get("ros_topic", self.goal_topic)
        if self.cmd_is_twist:
            self.create_subscription(Twist, goal_topic, self._on_goal_twist, qos)
        else:
            self.create_subscription(Float64MultiArray, goal_topic, self._on_goal, qos)
        if self.needs_base:
            # sources by priority: IMU (angular velocity, orientation), odometry (linear velocity,
            # pose), ground truth (everything, simulation only)
            if self.imu_topic:
                self.create_subscription(Imu, self.imu_topic, self._on_imu, qos)
            if self.odom_topic:
                self.create_subscription(Odometry, self.odom_topic, self._on_odom, qos)
            if not (self.imu_topic and self.odom_topic):
                self.create_subscription(PoseStamped, self.ground_truth_topic, self._on_pose, qos)
        if self.twist_action is not None:
            self.pub_twist = self.create_publisher(Twist, self.cmd_vel_topic, qos)
        self.create_subscription(Bool, self.estop_topic, self._on_estop, qos)
        if self.command_mode == "joint_state_topic":
            self.pub_cmd = self.create_publisher(JointState, self.command_topic, qos)
        elif self.command_mode == "ros2_control_commands":
            topic = f"{prefix}/{self.controller_name}/commands"
            self.pub_cmd = self.create_publisher(Float64MultiArray, topic, qos)
        else:
            self._fail(f"unknown command_mode {self.command_mode!r}")
        self.pub_status = self.create_publisher(String, f"{status_base}/status", qos)
        self.debug = bool(self.get_parameter("publish_debug").value)
        if self.debug:
            self.pub_obs = self.create_publisher(Float64MultiArray, f"{status_base}/observation", qos)
            self.pub_act = self.create_publisher(Float64MultiArray, f"{status_base}/action", qos)

        self.create_timer(1.0 / self.rate_hz, self._tick)
        self.create_timer(1.0, self._publish_status)
        self.get_logger().info(
            f"policy {self.c.get('name')}: {self.rate_hz:g} Hz, joints={self.joints}, "
            f"obs {self.obs_dim}x{self.history} -> act {self.action_dim}, "
            f"states={self.joint_states_topic} cmd={self.command_topic} ({self.command_mode})")
        if self.errors:
            self.get_logger().error("contract errors: " + "; ".join(self.errors))

    # ------------------------------------------------------------------ layout
    def _obs_size(self, spec: dict) -> int:
        s = spec["source"]
        if s in ("joint_position", "joint_velocity", "joint_effort"):
            return len(spec.get("joints") or self.joints)
        if s == "last_action":
            return self.action_dim
        if s in ("command", "custom"):
            return int(spec["size"])
        return {"base_lin_vel": 3, "base_ang_vel": 3, "projected_gravity": 3,
                "imu_orientation": 4, "base_goal_xy": 2}.get(s, 0)

    def _action_size(self, spec: dict) -> int:
        t = spec["target"]
        if t == "joints":
            return len(spec.get("joints") or self.joints)
        return int(spec.get("size", 2 if t == "base_twist" else 0))

    @staticmethod
    def _layout(specs, size_fn):
        out, off = [], 0
        for spec in specs:
            n = size_fn(spec)
            out.append((spec, n, off))
            off += n
        return out

    def _per_joint(self, spec) -> dict[str, float] | None:
        """Scalar or {joint: value} -> {joint: value} over the contract joints; None if unset."""
        if spec is None:
            return None
        if isinstance(spec, (int, float)):
            return {j: float(spec) for j in self.joints}
        return {j: float(v) for j, v in spec.items()}

    def _on_estop(self, msg: Bool) -> None:
        if msg.data and not self.estop:
            self.get_logger().warn("e-stop asserted: commanding suspended")
        elif not msg.data and self.estop:
            self.get_logger().info("e-stop released: ramping in")
        self.estop = bool(msg.data)

    def _enter_safe_stop(self, reason: str) -> None:
        if self.safe_stopped:
            return
        self.safe_stopped = True
        self.safe_stop_reason = reason
        self.safety_events += 1
        if self.stop_action == "zero":
            zeros, mode = {}, None
            for spec, n, off in self.actions:
                if spec["target"] == "joints" and spec["mode"] in ("velocity", "effort"):
                    mode = spec["mode"]
                    for j in spec.get("joints") or self.joints:
                        zeros[j] = 0.0
            if zeros:
                self._publish_command(zeros, mode)
            if self.twist_action is not None:
                self.pub_twist.publish(Twist())

    def _leave_safe_stop(self) -> None:
        self.safe_stopped = False
        self.safe_stop_reason = ""
        self.ramp_start = time.monotonic()  # ramp in again from the measured state

    def _apply_safety(self, targets: dict[str, float], mode: str, now: float) -> dict[str, float]:
        """Clamp targets to the contract's safety limits and blend during ramp-in."""
        if self.ramp_start is None:
            self.ramp_start = now
        alpha = 1.0 if self.ramp_in_s <= 0 else min(1.0, (now - self.ramp_start) / self.ramp_in_s)
        q_meas: dict[str, float] = {}
        if mode == "position":
            arr = self._joint_array("position")
            if arr is not None:
                q_meas = {j: float(v) for j, v in zip(self.joints, arr)}
        out = {}
        for j, v in targets.items():
            if mode == "position":
                if alpha < 1.0 and j in q_meas:
                    v = q_meas[j] + alpha * (v - q_meas[j])
                if self.max_speed and j in self.max_speed:
                    ref = self.last_target.get(j, q_meas.get(j, v))
                    step = self.max_speed[j] / self.rate_hz
                    v = min(max(v, ref - step), ref + step)
                if j in self.joint_limits:
                    lo, hi = self.joint_limits[j]
                    v = min(max(v, lo), hi)
            elif mode == "velocity":
                v *= alpha
                if self.max_speed and j in self.max_speed:
                    v = min(max(v, -self.max_speed[j]), self.max_speed[j])
            else:  # effort
                v *= alpha
                if self.max_effort and j in self.max_effort:
                    v = min(max(v, -self.max_effort[j]), self.max_effort[j])
            out[j] = float(v)
        self.last_target.update(out)
        self.ramp_alpha = alpha
        return out

    def _goal_size(self) -> int:
        for spec, n, _ in self.observations:
            if spec["source"] == "command":
                return n
        for spec, n, _ in self.observations:
            if spec["source"] == "base_goal_xy":
                return 2  # world x, y on the goal topic
        return 0

    def _fail(self, msg: str) -> None:
        self.errors.append(msg)

    # --------------------------------------------------------------- callbacks
    def _on_js(self, msg: JointState) -> None:
        self.js = msg
        self.js_time = time.monotonic()
        if self.js_index is None:
            names = list(msg.name)
            missing = [j for j in self.joints if j not in names]
            if missing:
                if not any(e.startswith("joints missing") for e in self.errors):
                    self._fail(f"joints missing from {self.joint_states_topic}: {missing} "
                               f"(got {names})")
                    self.get_logger().error(self.errors[-1])
                return
            self.js_index = [names.index(j) for j in self.joints]
            self.get_logger().info(f"joint index map resolved: {dict(zip(self.joints, self.js_index))}")

    def _on_pose(self, msg: PoseStamped) -> None:
        p = msg.pose.position; o = msg.pose.orientation
        pos = np.array([p.x, p.y, p.z]); quat = np.array([o.x, o.y, o.z, o.w])
        t = msg.header.stamp.sec + msg.header.stamp.nanosec * 1e-9
        if self.base_time is not None and t > self.base_time + 1e-6:
            dt = t - self.base_time
            self.base_lin_vel = (pos - self.base_pos) / dt
            # angular velocity from the relative rotation R_prev^T R_now (small-angle, world frame)
            r_rel = quat_to_rot(self.base_quat).T @ quat_to_rot(quat)
            w_body = np.array([r_rel[2, 1] - r_rel[1, 2], r_rel[0, 2] - r_rel[2, 0], r_rel[1, 0] - r_rel[0, 1]]) / (2 * dt)
            self.base_ang_vel = quat_to_rot(self.base_quat) @ w_body
        self.base_pos, self.base_quat, self.base_time = pos, quat, t
        self.base_wall_time = time.monotonic()
        self.base_received = True

    def _on_imu(self, msg: Imu) -> None:
        o = msg.orientation
        self.base_quat = np.array([o.x, o.y, o.z, o.w])
        w = msg.angular_velocity
        self.body_ang_vel = np.array([w.x, w.y, w.z])
        self.base_wall_time = time.monotonic()
        self.base_received = True

    def _on_odom(self, msg: Odometry) -> None:
        p = msg.pose.pose.position
        self.base_pos = np.array([p.x, p.y, p.z])
        if not self.imu_topic:
            o = msg.pose.pose.orientation
            self.base_quat = np.array([o.x, o.y, o.z, o.w])
        v = msg.twist.twist.linear; w = msg.twist.twist.angular
        self.body_lin_vel = np.array([v.x, v.y, v.z])  # odometry twist is in the child (body) frame
        if not self.imu_topic:
            self.body_ang_vel = np.array([w.x, w.y, w.z])
        self.base_wall_time = time.monotonic()
        self.base_received = True

    def _on_goal_twist(self, msg: Twist) -> None:
        self.goal = np.array([msg.linear.x, msg.linear.y, msg.angular.z], dtype=np.float32)
        self.goal_received = True

    def _on_goal(self, msg: Float64MultiArray) -> None:
        data = np.asarray(msg.data, dtype=np.float32)
        if data.shape[0] != self.goal.shape[0]:
            self.get_logger().warn(
                f"goal has {data.shape[0]} values, contract expects {self.goal.shape[0]}; ignored",
                throttle_duration_sec=2.0)
            return
        self.goal = data
        self.goal_received = True

    # --------------------------------------------------------------- main loop
    def _joint_array(self, field: str) -> np.ndarray | None:
        arr = getattr(self.js, field)
        if len(arr) < len(self.js.name):
            return None
        return np.asarray(arr, dtype=np.float32)[self.js_index]

    def _select(self, full: np.ndarray, spec: dict) -> np.ndarray:
        js = spec.get("joints")
        if not js:
            return full
        return full[[self.joints.index(j) for j in js]]

    def _assemble(self) -> np.ndarray | None:
        frame = np.zeros(self.obs_dim, dtype=np.float32)
        for spec, n, off in self.observations:
            s = spec["source"]
            if s == "joint_position":
                v = self._joint_array("position")
            elif s == "joint_velocity":
                v = self._joint_array("velocity")
            elif s == "joint_effort":
                v = self._joint_array("effort")
            elif s == "command":
                v = self.goal
            elif s == "last_action":
                v = self.last_action
            elif s == "base_lin_vel":
                v = self.body_lin_vel if self.body_lin_vel is not None else quat_to_rot(self.base_quat).T @ self.base_lin_vel
            elif s == "base_ang_vel":
                v = self.body_ang_vel if self.body_ang_vel is not None else quat_to_rot(self.base_quat).T @ self.base_ang_vel
            elif s == "projected_gravity":
                v = quat_to_rot(self.base_quat).T @ np.array([0.0, 0.0, -1.0])
            elif s == "imu_orientation":
                v = self.base_quat
            elif s == "base_goal_xy":
                d = np.array([self.goal[0] - self.base_pos[0], self.goal[1] - self.base_pos[1], 0.0])
                v = (quat_to_rot(self.base_quat).T @ d)[:2]
            else:
                v = np.zeros(n, dtype=np.float32)
            if v is None:
                self.get_logger().warn(f"{s}: field missing in joint_states", throttle_duration_sec=2.0)
                return None
            if s.startswith("joint_"):
                v = self._select(v, spec)
            if v.shape[0] != n:
                self.get_logger().error(f"{spec['name']}: got {v.shape[0]} values, expected {n}")
                return None
            frame[off:off + n] = process_obs_term(v, spec)
        return frame

    def _tick(self) -> None:
        now = time.monotonic()
        self.tick_times.append(now)
        if self.js is None or self.js_index is None:
            return
        if self.needs_base and not self.base_received:
            return
        self.obs_ages.append(now - self.js_time)
        # watchdog + e-stop: suspend commanding, resume with a ramp-in
        age = now - self.js_time
        if self.needs_base and self.base_wall_time > 0:
            age = max(age, now - self.base_wall_time)
        if age > self.max_obs_age or self.estop:
            reason = "e-stop" if self.estop else "stale_observations"
            if not self.safe_stopped:
                self.get_logger().warn(f"safe stop: {reason} (observation age {age*1e3:.0f} ms)")
            self._enter_safe_stop(reason)
            return
        if self.safe_stopped:
            self.get_logger().info("observations fresh and e-stop clear: resuming with ramp-in")
            self._leave_safe_stop()
        frame = self._assemble()
        if frame is None:
            return
        if not self.frames:
            for _ in range(self.history):
                self.frames.append(frame)
        else:
            self.frames.append(frame)
        x = np.concatenate(list(self.frames))[None, :]  # oldest .. newest

        t0 = time.perf_counter()
        y = self.sess.run([self.output_name], {self.input_name: x})[0]
        self.infer_ms = (time.perf_counter() - t0) * 1e3
        raw = np.asarray(y, dtype=np.float32).reshape(-1)
        if raw.shape[0] != self.action_dim or not np.all(np.isfinite(raw)):
            self.get_logger().error(f"policy output invalid: shape {raw.shape}, finite={np.all(np.isfinite(raw))}")
            return

        targets_by_joint: dict[str, float] = {}
        mode = None
        for spec, n, off in self.actions:
            clipped, a = process_action_term(raw[off:off + n], spec)
            raw[off:off + n] = clipped  # last_action sees the clipped raw output
            if spec["target"] == "joints":
                mode = spec["mode"]
                for j, v in zip(spec.get("joints") or self.joints, a):
                    targets_by_joint[j] = float(v)
            elif spec["target"] == "base_twist":
                if self.ramp_start is None:
                    self.ramp_start = now
                self.ramp_alpha = 1.0 if self.ramp_in_s <= 0 else min(1.0, (now - self.ramp_start) / self.ramp_in_s)
                a = a * self.ramp_alpha
                if self.max_base_speed:
                    vmax, wmax = float(self.max_base_speed[0]), float(self.max_base_speed[1])
                    a = a.copy()
                    a[0] = min(max(a[0], -vmax), vmax)
                    a[-1] = min(max(a[-1], -wmax), wmax)
                tw = Twist()
                tw.linear.x = float(a[0])
                if n == 3:
                    tw.linear.y = float(a[1]); tw.angular.z = float(a[2])
                else:
                    tw.angular.z = float(a[1])
                self.pub_twist.publish(tw)
        self.last_action = raw

        if targets_by_joint:
            self._publish_command(self._apply_safety(targets_by_joint, mode, now), mode)
        if self.debug:
            self.pub_obs.publish(Float64MultiArray(data=[float(v) for v in x[0]]))
            self.pub_act.publish(Float64MultiArray(data=[float(v) for v in raw]))
        self.steps += 1

    def _publish_command(self, targets: dict[str, float], mode: str) -> None:
        names = [j for j in self.joints if j in targets]
        vals = [targets[j] for j in names]
        if self.command_mode == "joint_state_topic":
            msg = JointState()
            msg.header.stamp = self.get_clock().now().to_msg()
            msg.name = names
            if mode == "position":
                msg.position = vals
            elif mode == "velocity":
                msg.velocity = vals
            else:
                msg.effort = vals
            self.pub_cmd.publish(msg)
        else:
            self.pub_cmd.publish(Float64MultiArray(data=vals))

    def _publish_status(self) -> None:
        ticks = [t for t in self.tick_times if t > time.monotonic() - 1.0]
        st = {
            "name": self.c.get("name"),
            "rate_target_hz": self.rate_hz,
            "rate_measured_hz": float(len(ticks)),
            "steps": self.steps,
            "joints_ok": self.js_index is not None,
            "goal_received": self.goal_received,
            "base_received": self.base_received if self.needs_base else None,
            # joint_states age as seen by the policy loop, over the last status window
            "obs_age_max_s": round(max(self.obs_ages), 4) if self.obs_ages else None,
            "obs_age_mean_s": round(float(np.mean(self.obs_ages)), 4) if self.obs_ages else None,
            "inference_ms": round(self.infer_ms, 3),
            "errors": list(self.errors),
            "safety": {"stopped": self.safe_stopped, "reason": self.safe_stop_reason, "estop": self.estop,
                       "events": self.safety_events, "ramp": round(float(self.ramp_alpha), 3),
                       "max_obs_age_s": self.max_obs_age},
        }
        self.pub_status.publish(String(data=json.dumps(st)))
        self.obs_ages.clear()


def main(args=None) -> None:
    rclpy.init(args=args)
    node = PolicyNode()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        node.destroy_node()
        rclpy.try_shutdown()


if __name__ == "__main__":
    main()
