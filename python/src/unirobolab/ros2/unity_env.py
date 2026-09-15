"""Gymnasium environment over the Unity simulator, driven by the policy contract.

The simulator is paused and advanced with the ``step_simulation`` service, so
each ``step()`` is exactly ``physics_steps_per_action`` physics steps regardless
of wall clock. Observations and actions go through the very same functions as
the deployed node (``policy_node.process_obs_term`` / ``process_action_term``),
which is what makes a policy trained here match the generated package bit for
bit at the wiring level.

Task definition (goal sampling, reward terms, episode length) lives in a small
JSON next to the contract; see ``contract/examples/servo_demo.train.json``.
"""

from __future__ import annotations

import time
from typing import Any

import gymnasium as gym
import numpy as np
import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState
from simulation_interfaces.msg import Result, SimulationState
from simulation_interfaces.srv import ResetSimulation, SetSimulationState, StepSimulation

try:  # simulator >= step-and-observe branch: one round trip per control step
    from simulation_extra_interfaces.srv import StepAndObserve
except ImportError:  # older simulation_extra_interfaces
    StepAndObserve = None

from unirobolab.contract import Contract
from unirobolab.ros2.policy_node import process_action_term, process_obs_term


class UnityContractEnv(gym.Env):
    metadata = {"render_modes": []}

    def __init__(self, c: Contract, task: dict[str, Any], node: Node | None = None,
                 service_timeout_s: float = 20.0) -> None:
        super().__init__()
        if c.ros is None:
            raise ValueError("contract needs a 'ros' section")
        self.c = c
        self.task = task
        self.owns_node = node is None
        if not rclpy.ok():
            rclpy.init()
        self.node = node or rclpy.create_node("unirobolab_env")
        self.joints = c.joints
        self.cmd_term = c.obs_terms_by_source("command")[0]
        self.act_term = [t for t in c.actions if t.source == "joints"][0]
        self.act_joints = self.act_term.joints or self.joints
        self.steps_per_action = int(task.get("physics_steps_per_action", 1))
        self.episode_steps = int(task.get("episode_steps", 100))
        self.command_settle_s = float(task.get("command_settle_s", 0.005))
        gr = task.get("goal_range", [-0.5, 0.5])
        self.goal_low = np.array([gr[0]] * self.cmd_term.size, dtype=np.float32)
        self.goal_high = np.array([gr[1]] * self.cmd_term.size, dtype=np.float32)
        self.reward_weights: dict[str, float] = dict(task.get("reward", {"tracking_l1": -1.0}))

        self.observation_space = gym.spaces.Box(-np.inf, np.inf, (c.policy_input_dim,), np.float32)
        clip = self.act_term.clip or (-1.0, 1.0)
        self.action_space = gym.spaces.Box(clip[0], clip[1], (c.action_dim,), np.float32)

        # ROS plumbing
        self.sub = self.node.create_subscription(JointState, c.ros.joint_states_topic, self._on_js, 50)
        self.pub = self.node.create_publisher(JointState, c.ros.command_topic, 10)
        self.cli_state = self.node.create_client(SetSimulationState, "/set_simulation_state")
        self.cli_step = self.node.create_client(StepSimulation, "/step_simulation")
        self.cli_reset = self.node.create_client(ResetSimulation, "/reset_simulation")
        for cli in (self.cli_state, self.cli_step, self.cli_reset):
            if not cli.wait_for_service(timeout_sec=service_timeout_s):
                raise RuntimeError(f"service {cli.srv_name} not available")
        # step_and_observe: command + N steps + joint state in one service call. Falls back
        # to joint_command topic + step_simulation + joint_states topic when the simulator
        # (or the interface package) does not have it.
        self.entity = task.get("entity", c.ros.namespace)
        self.cli_sao = None
        if StepAndObserve is not None and task.get("use_step_and_observe", True):
            cli = self.node.create_client(StepAndObserve, "/step_and_observe")
            if cli.wait_for_service(timeout_sec=float(task.get("step_and_observe_timeout_s", 3.0))):
                self.cli_sao = cli
            else:
                self.node.get_logger().warn("/step_and_observe not available; using topics + step_simulation")
        self.node.get_logger().info(
            f"env transport: {'step_and_observe' if self.cli_sao else 'topics + step_simulation'}")
        self.js: JointState | None = None
        self.js_seq = 0
        self.js_index: list[int] | None = None
        self.stale_steps = 0  # steps that saw no fresh joint_states (diagnostic)
        self._pause()

        self.goal = np.zeros(self.cmd_term.size, dtype=np.float32)
        self.last_action = np.zeros(c.action_dim, dtype=np.float32)
        self.frames: list[np.ndarray] = []
        self.t = 0
        self.episode_terms: dict[str, float] = {}
        self.last_q = np.zeros(len(self.act_joints), dtype=np.float32)

    # ------------------------------------------------------------ ROS helpers
    def _call(self, cli, req, timeout_s: float = 30.0):
        fut = cli.call_async(req)
        end = time.monotonic() + timeout_s
        while rclpy.ok() and not fut.done():
            # short timeout: with 0.01 every service response could wait up to 10 ms here
            rclpy.spin_once(self.node, timeout_sec=0.001)
            if time.monotonic() > end:
                raise TimeoutError(f"{cli.srv_name} timed out")
        return fut.result()

    def _pause(self) -> None:
        req = SetSimulationState.Request()
        req.state = SimulationState(state=SimulationState.STATE_PAUSED)
        res = self._call(self.cli_state, req)
        if res.result.result not in (Result.RESULT_OK, SetSimulationState.Response.ALREADY_IN_TARGET_STATE):
            raise RuntimeError(f"pause failed: {res.result.error_message}")

    def _step_sim(self, n: int) -> None:
        req = StepSimulation.Request(steps=n)
        res = self._call(self.cli_step, req)
        if res.result.result != Result.RESULT_OK:
            raise RuntimeError(f"step_simulation failed: {res.result.error_message}")

    def _reset_sim(self) -> None:
        req = ResetSimulation.Request(scope=ResetSimulation.Request.SCOPE_STATE)
        res = self._call(self.cli_reset, req)
        if res.result.result != Result.RESULT_OK:
            raise RuntimeError(f"reset_simulation failed: {res.result.error_message}")

    def _index_from(self, names: list[str]) -> list[int] | None:
        if any(j not in names for j in self.joints):
            return None
        return [names.index(j) for j in self.joints]

    def _on_js(self, msg: JointState) -> None:
        if self.cli_sao is not None:
            return  # observations come from the service response in this mode
        if self.js_index is None:
            self.js_index = self._index_from(list(msg.name))
            if self.js_index is None:
                return
        self.js = msg
        self.js_seq += 1

    def _step_and_observe(self, targets: np.ndarray | None, steps: int) -> None:
        """One round trip: apply targets (None = no command), run steps, take the state."""
        req = StepAndObserve.Request()
        req.entity = self.entity
        req.steps = int(steps)
        if targets is not None:
            req.command = self._command_msg(targets)
        res = self._call(self.cli_sao, req)
        if res.result != StepAndObserve.Response.RESULT_OK:
            raise RuntimeError(f"step_and_observe failed ({res.result}): {res.error_message}")
        idx = self._index_from(list(res.joint_states.name))
        if idx is None:
            raise RuntimeError(f"step_and_observe returned joints {list(res.joint_states.name)}, "
                               f"contract needs {self.joints}")
        self.js_index = idx
        self.js = res.joint_states
        self.js_seq += 1

    def _wait_new_js(self, seq_before: int, timeout_s: float = 0.2) -> JointState:
        """Return the newest joint_states published since ``seq_before``.

        joint_states travel Unity -> endpoint -> DDS while the step response comes back
        through the service path, so a state published during the step may land just
        after the response: wait a little for it, then take whatever is newest.
        """
        end = time.monotonic() + timeout_s
        while self.js_seq == seq_before:
            rclpy.spin_once(self.node, timeout_sec=0.005)
            if time.monotonic() > end:
                if self.js is None:
                    raise TimeoutError("no joint_states from the simulator")
                self.stale_steps += 1
                return self.js
        # drain anything already queued (the second of two steps may have published too)
        for _ in range(4):
            rclpy.spin_once(self.node, timeout_sec=0.0)
        return self.js

    # ----------------------------------------------------------- observation
    def _joint_array(self, field: str) -> np.ndarray:
        arr = getattr(self.js, field)
        if len(arr) < len(self.js.name):
            return np.zeros(len(self.joints), dtype=np.float32)
        return np.asarray(arr, dtype=np.float32)[self.js_index]

    def _select(self, full: np.ndarray, spec: dict) -> np.ndarray:
        js = spec.get("joints")
        return full if not js else full[[self.joints.index(j) for j in js]]

    def _frame(self) -> np.ndarray:
        frame = np.zeros(self.c.obs_dim, dtype=np.float32)
        for t in self.c.observations:
            s = t.source
            if s == "joint_position":
                v = self._select(self._joint_array("position"), t.spec)
            elif s == "joint_velocity":
                v = self._select(self._joint_array("velocity"), t.spec)
            elif s == "joint_effort":
                v = self._select(self._joint_array("effort"), t.spec)
            elif s == "command":
                v = self.goal
            elif s == "last_action":
                v = self.last_action
            else:
                raise ValueError(f"observation source {s!r} not supported by the Unity env")
            frame[t.offset:t.end] = process_obs_term(v, t.spec)
        return frame

    def _obs(self) -> np.ndarray:
        frame = self._frame()
        if not self.frames:
            self.frames = [frame] * self.c.history_length
        else:
            self.frames = (self.frames + [frame])[-self.c.history_length:]
        return np.concatenate(self.frames).astype(np.float32)

    def _q(self) -> np.ndarray:
        full = self._joint_array("position")
        return full[[self.joints.index(j) for j in self.act_joints]]

    # ---------------------------------------------------------------- gym API
    def reset(self, *, seed: int | None = None, options: dict | None = None):
        super().reset(seed=seed)
        self._reset_sim()
        self.goal = self.np_random.uniform(self.goal_low, self.goal_high).astype(np.float32)
        self.last_action = np.zeros(self.c.action_dim, dtype=np.float32)
        self.frames = []
        self.t = 0
        self.episode_terms = {k: 0.0 for k in self.reward_weights}
        zeros = np.zeros(len(self.act_joints), dtype=np.float32)
        if self.cli_sao is not None:
            self._step_and_observe(zeros, self.steps_per_action)
        else:
            self._publish_targets(zeros)
            seq = self.js_seq
            self._step_sim(self.steps_per_action)
            self._wait_new_js(seq)
        self.last_q = self._q()
        return self._obs(), {"goal": self.goal.copy()}

    def _command_msg(self, targets: np.ndarray) -> JointState:
        msg = JointState()
        msg.header.stamp = self.node.get_clock().now().to_msg()
        msg.name = list(self.act_joints)
        mode = self.act_term.spec.get("mode", "position")
        vals = [float(v) for v in targets]
        if mode == "position":
            msg.position = vals
        elif mode == "velocity":
            msg.velocity = vals
        else:
            msg.effort = vals
        return msg

    def _publish_targets(self, targets: np.ndarray) -> None:
        self.pub.publish(self._command_msg(targets))

    def step(self, action: np.ndarray):
        raw = np.asarray(action, dtype=np.float32).reshape(-1)
        clipped, target = process_action_term(raw[self.act_term.offset:self.act_term.end], self.act_term.spec)
        prev_action = self.last_action.copy()
        self.last_action = raw.copy()
        self.last_action[self.act_term.offset:self.act_term.end] = clipped

        if self.cli_sao is not None:
            self._step_and_observe(target, self.steps_per_action)
        else:
            self._publish_targets(target)
            if self.command_settle_s > 0:
                # let the command reach Unity before the physics steps consume it
                end = time.monotonic() + self.command_settle_s
                while time.monotonic() < end:
                    rclpy.spin_once(self.node, timeout_sec=0.001)
            seq = self.js_seq
            self._step_sim(self.steps_per_action)
            self._wait_new_js(seq)

        q = self._q()
        qd = (q - self.last_q) / (self.steps_per_action * (self.c.sim_dt_s or 0.02))
        self.last_q = q
        goal = self.goal[: len(self.act_joints)]
        terms = {
            "tracking_l1": float(np.sum(np.abs(q - goal))),
            "tracking_l2": float(np.sum((q - goal) ** 2)),
            "action_rate": float(np.sum(np.abs(self.last_action - prev_action))),
            "velocity": float(np.sum(np.abs(qd))),
        }
        reward = 0.0
        for k, w in self.reward_weights.items():
            if k not in terms:
                raise ValueError(f"unknown reward term {k!r}; available: {sorted(terms)}")
            contrib = w * terms[k]
            reward += contrib
            self.episode_terms[k] += contrib
        self.t += 1
        truncated = self.t >= self.episode_steps
        info = {"goal": goal.copy(), "q": q.copy(), "abs_err": float(np.mean(np.abs(q - goal))),
                "terms": terms}
        if truncated:
            info["episode_terms"] = dict(self.episode_terms)
        return self._obs(), float(reward), False, truncated, info

    def close(self) -> None:
        if self.owns_node:
            self.node.destroy_node()
