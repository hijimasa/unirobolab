"""Run a policy in real time against the simulator through the learning server.

No ROS 2 involved: the simulator is put into PLAYING, and every policy period the
runner reads the entity state (observe-only STEP), assembles the observation with
the same functions as the deployed node (``obs_math``), runs the ONNX policy with
onnxruntime and sends the command back. This is what the simulator's GUI launches
to "run a policy" without a ROS graph, and it doubles as a quick check of a
contract + ONNX pair.

Goals: ``--goal`` sets the initial goal (command / base_goal_xy term); later goals
can be fed on stdin as lines ``goal v1 v2 ...``. Status lines (JSON) go to stdout
once a second; ``--log`` writes every observation/action for cross-checks.
"""

from __future__ import annotations

import json
import select
import sys
import threading
import time

import numpy as np

from unirobolab.contract import Contract
from unirobolab.direct.client import LearningClient
from unirobolab.geometry import projected_gravity, quat_to_rot, world_to_body
from unirobolab.obs_math import process_action_term, process_obs_term


class LivePolicy:
    def __init__(self, c: Contract, entity: str, onnx_path: str, host: str = "127.0.0.1", port: int = 10100,
                 goal: list[float] | None = None) -> None:
        import onnxruntime as ort
        self.c = c
        self.entity = entity
        self.client = LearningClient(host, port)
        self.sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        st = self.client.info([entity])[0]
        missing = [j for j in c.joints if j not in st.names]
        if missing:
            raise RuntimeError(f"entity {entity}: joints {missing} not found (has {st.names})")
        self.index = [st.names.index(j) for j in c.joints]
        self.act_term = [t for t in c.actions if t.source == "joints"][0]
        self.act_joints = self.act_term.joints or c.joints
        cmds = c.obs_terms_by_source("command") + c.obs_terms_by_source("base_goal_xy")
        self.goal_size = cmds[0].size if cmds else 0
        self.goal = np.zeros(self.goal_size, np.float32)
        if goal is not None:
            self.set_goal(goal)
        self.last_action = np.zeros(c.action_dim, np.float32)
        self.frames: list[np.ndarray] = []
        self.state = st
        self.steps = 0
        self.errors: list[str] = []

    def set_goal(self, values: list[float]) -> None:
        v = np.asarray(values, np.float32)
        if v.shape[0] != self.goal_size:
            raise ValueError(f"goal has {v.shape[0]} values, contract needs {self.goal_size}")
        self.goal = v

    def _joint(self, field: str) -> np.ndarray:
        return np.asarray(getattr(self.state, field), np.float32)[self.index]

    def _select(self, full: np.ndarray, spec: dict) -> np.ndarray:
        js = spec.get("joints")
        return full if not js else full[[self.c.joints.index(j) for j in js]]

    def _frame(self) -> np.ndarray:
        st = self.state
        frame = np.zeros(self.c.obs_dim, np.float32)
        for t in self.c.observations:
            s = t.source
            if s == "joint_position":
                v = self._select(self._joint("position"), t.spec)
            elif s == "joint_velocity":
                v = self._select(self._joint("velocity"), t.spec)
            elif s == "joint_effort":
                v = self._select(self._joint("effort"), t.spec)
            elif s == "command":
                v = self.goal
            elif s == "last_action":
                v = self.last_action
            elif s == "base_lin_vel":
                v = world_to_body(st.base_lin_vel, st.base_quat)
            elif s == "base_ang_vel":
                v = world_to_body(st.base_ang_vel, st.base_quat)
            elif s == "projected_gravity":
                v = projected_gravity(st.base_quat)
            elif s == "imu_orientation":
                v = st.base_quat
            elif s == "base_goal_xy":
                d = np.array([self.goal[0] - st.base_pos[0], self.goal[1] - st.base_pos[1], 0.0])
                v = (quat_to_rot(st.base_quat).T @ d)[:2]
            else:
                raise ValueError(f"observation source {s!r} not supported")
            frame[t.offset:t.end] = process_obs_term(v, t.spec)
        return frame

    def tick(self) -> tuple[np.ndarray, np.ndarray]:
        frame = self._frame()
        if not self.frames:
            self.frames = [frame] * self.c.history_length
        else:
            self.frames = (self.frames + [frame])[-self.c.history_length:]
        x = np.concatenate(self.frames)[None, :].astype(np.float32)
        y = self.sess.run([self.c.output_name], {self.c.input_name: x})[0].reshape(-1).astype(np.float32)
        raw = y.copy()
        a_off, a_end = self.act_term.offset, self.act_term.end
        clipped, target = process_action_term(raw[a_off:a_end], self.act_term.spec)
        raw[a_off:a_end] = clipped
        self.last_action = raw
        mode = self.act_term.spec.get("mode", "position")
        states, _ = self.client.observe([self.entity], [{"name": list(self.act_joints), mode: target}])
        self.state = states[0]
        self.steps += 1
        return x[0], raw

    def error_to_goal(self) -> float | None:
        if self.goal_size == 0:
            return None
        if self.c.obs_terms_by_source("base_goal_xy"):
            return float(np.linalg.norm(self.goal[:2] - self.state.base_pos[:2]))
        q = self._joint("position")[[self.c.joints.index(j) for j in self.act_joints]]
        return float(np.mean(np.abs(q - self.goal[: len(self.act_joints)])))


def run(c: Contract, entity: str, onnx_path: str, host: str, port: int, goal: list[float] | None,
        duration_s: float | None, log_path: str | None, play: bool = True) -> int:
    lp = LivePolicy(c, entity, onnx_path, host, port, goal)
    if play:
        lp.client.play()
    period = c.period_s
    log = open(log_path, "w", encoding="utf-8") if log_path else None
    stop = threading.Event()

    def reader():  # goal updates on stdin: "goal v1 v2 ..."
        while not stop.is_set():
            r, _, _ = select.select([sys.stdin], [], [], 0.2)
            if not r:
                continue
            line = sys.stdin.readline()
            if not line:
                break
            parts = line.split()
            if parts and parts[0] == "goal":
                try:
                    lp.set_goal([float(v) for v in parts[1:]])
                except ValueError as e:
                    print(json.dumps({"error": str(e)}), flush=True)
            elif parts and parts[0] == "stop":
                stop.set()

    threading.Thread(target=reader, daemon=True).start()
    t0 = time.monotonic(); next_t = t0; last_status = t0; ticks = 0
    try:
        while not stop.is_set() and (duration_s is None or time.monotonic() - t0 < duration_s):
            now = time.monotonic()
            if now < next_t:
                time.sleep(min(next_t - now, 0.002)); continue
            next_t += period
            if now - next_t > 5 * period:  # fell far behind: resync instead of bursting
                next_t = now
            obs, act = lp.tick(); ticks += 1
            if log:
                log.write(json.dumps({"t": round(now - t0, 4), "obs": obs.tolist(), "action": act.tolist()}) + "\n")
            if now - last_status >= 1.0:
                print(json.dumps({"t": round(now - t0, 1), "rate_hz": ticks / (now - last_status),
                                  "err": lp.error_to_goal(), "goal": lp.goal.tolist()}), flush=True)
                ticks = 0; last_status = now
    finally:
        stop.set()
        if log:
            log.close()
        lp.client.close()
    err = lp.error_to_goal()
    print(json.dumps({"done": True, "steps": lp.steps, "final_err": err}), flush=True)
    return 0
