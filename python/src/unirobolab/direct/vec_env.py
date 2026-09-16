"""Vectorised environment over the simulator's learning server.

K robots live in one scene; every ``step`` is one round trip that commands all of
them, advances the physics N steps and returns all joint states. Observations and
actions use the same functions as the deployed policy node (``obs_math``), so a
policy trained here matches the generated ROS 2 package at the wiring level.
"""

from __future__ import annotations

import time
from typing import Any

import numpy as np
from gymnasium import spaces
from stable_baselines3.common.vec_env.base_vec_env import VecEnv

from unirobolab.contract import Contract
from unirobolab.direct.client import LearningClient, LearningServerError
from unirobolab.geometry import projected_gravity, quat_to_rot, world_to_body
from unirobolab.obs_math import process_action_term, process_obs_term
from unirobolab.task import Task


class DirectVecEnv(VecEnv):
    def __init__(self, c: Contract, task_cfg: dict[str, Any], entities: list[str],
                 host: str = "127.0.0.1", port: int = 10100, seed: int = 0,
                 spawn_urdf: str | None = None, spawn_spacing: float = 1.0, spawn_yaw: float = 0.0,
                 spawn_layout: str = "line", spawn_origin: tuple[float, float] = (0.0, 0.0)) -> None:
        self.c = c
        self.entities = list(entities)
        n = len(self.entities)
        cmds = c.obs_terms_by_source("command")
        self.cmd_term = cmds[0] if cmds else None
        joint_terms = [t for t in c.actions if t.source == "joints"]
        twist_terms = [t for t in c.actions if t.source == "base_twist"]
        if joint_terms:
            self.act_term = joint_terms[0]
            self.act_joints = self.act_term.joints or c.joints
            self.twist_term = None
        elif twist_terms:
            # the simulator takes joint commands: a base_twist action needs the task's
            # diff_drive model {wheel_radius, wheel_separation, left_joint, right_joint}
            self.twist_term = twist_terms[0]
            dd = task_cfg.get("diff_drive")
            if not dd:
                raise ValueError("base_twist actions need task.diff_drive {wheel_radius, wheel_separation, left_joint, right_joint}")
            self.dd = dd
            self.act_term = self.twist_term
            self.act_joints = [dd["left_joint"], dd["right_joint"]]
        else:
            raise ValueError("contract needs a joints or base_twist action term")
        self.task = Task(task_cfg, self.cmd_term.size if self.cmd_term else 2)
        if self.task.type == "joint_target" and self.cmd_term is None:
            raise ValueError("joint_target task needs a 'command' observation term")
        if self.task.type == "base_target" and not c.obs_terms_by_source("base_goal_xy"):
            raise ValueError("base_target task needs a 'base_goal_xy' observation term")
        self.client = LearningClient(host, port)
        self.client.ping()
        try:
            self.client.pause()
        except LearningServerError as e:
            if "unknown op" not in str(e):
                raise
            # simulator built before the PAUSE op: it must already be paused (set_sim_state pause)
            print("warning: learning server has no PAUSE op; pause the simulation via ROS first", flush=True)
        self.rng = np.random.default_rng(seed)

        obs_space = spaces.Box(-np.inf, np.inf, (c.policy_input_dim,), np.float32)
        clip = self.act_term.clip or (-1.0, 1.0)
        act_space = spaces.Box(clip[0], clip[1], (c.action_dim,), np.float32)
        super().__init__(n, obs_space, act_space)

        if spawn_urdf:
            # spawn the entities that are not there yet, in a row along +y of the instance
            present = self.client.has_entities(self.entities)
            for i, (name, ok) in enumerate(zip(self.entities, present)):
                if not ok:
                    # line: ROS の y 方向に一列。grid: ceil(sqrt(n)) 列の格子 (並列の様子が見やすい)
                    if spawn_layout == "grid":
                        cols = max(1, int(np.ceil(np.sqrt(len(entities)))))
                        sx, sy = (i // cols) * spawn_spacing, (i % cols) * spawn_spacing
                    else:
                        sx, sy = 0.0, i * spawn_spacing
                    got = self.client.spawn(name, spawn_urdf, spawn_origin[0] + sx, spawn_origin[1] + sy, 0.0, spawn_yaw)
                    if got != name:
                        raise RuntimeError(f"spawned as {got!r}, expected {name!r}")
            self.client.pause()
        info = self.client.info(self.entities)
        self.index: list[list[int]] = []
        for name, st in zip(self.entities, info):
            missing = [j for j in c.joints if j not in st.names]
            if missing:
                raise RuntimeError(f"entity {name}: joints {missing} not found (has {st.names})")
            self.index.append([st.names.index(j) for j in c.joints])
        missing = [j for j in self.act_joints if j not in c.joints]
        if missing:
            raise ValueError(f"action joints {missing} are not in robot.joints")
        self.act_index = [c.joints.index(j) for j in self.act_joints]

        n_goal = self.cmd_term.size if self.cmd_term else 2
        self.goal = np.zeros((n, n_goal), np.float32)   # joint goal, or world xy goal
        # action latency: the contract's control.action_latency_steps control periods pass before a
        # command reaches the actuators (queue per env, initialised with zero actions on reset)
        self.latency = int(c.action_latency_steps)
        self.action_queue: list[list[np.ndarray]] = [[] for _ in range(n)]
        # observation noise: task.obs_noise = {term_name: std} on the raw (pre-scale) values
        self.obs_noise: dict[str, float] = {k: float(v) for k, v in (task_cfg.get("obs_noise") or {}).items()}
        unknown = [k for k in self.obs_noise if k not in {t.name for t in c.observations}]
        if unknown:
            raise ValueError(f"obs_noise names unknown terms {unknown}")
        self.spawn_xy = np.zeros((n, 2), np.float32)
        self.prev_dist = np.zeros(n, np.float32)
        self.last_action = np.zeros((n, c.action_dim), np.float32)
        self.frames: list[list[np.ndarray]] = [[] for _ in range(n)]
        self.t = np.zeros(n, int)
        self.episode_terms: list[dict[str, float]] = [{} for _ in range(n)]
        self.last_q = np.zeros((n, len(self.act_joints)), np.float32)
        self.states = None
        self._actions = None
        self.step_count = 0
        self.rpc_time = 0.0

    # ------------------------------------------------------------- helpers
    def _joint(self, i: int, field: str) -> np.ndarray:
        return np.asarray(getattr(self.states[i], field), np.float32)[self.index[i]]

    def _select(self, full: np.ndarray, spec: dict) -> np.ndarray:
        js = spec.get("joints")
        return full if not js else full[[self.c.joints.index(j) for j in js]]

    def _frame(self, i: int) -> np.ndarray:
        frame = np.zeros(self.c.obs_dim, np.float32)
        for t in self.c.observations:
            s = t.source
            if s == "joint_position":
                v = self._select(self._joint(i, "position"), t.spec)
            elif s == "joint_velocity":
                v = self._select(self._joint(i, "velocity"), t.spec)
            elif s == "joint_effort":
                v = self._select(self._joint(i, "effort"), t.spec)
            elif s == "command":
                v = self.goal[i]
            elif s == "last_action":
                v = self.last_action[i]
            elif s == "base_lin_vel":
                v = world_to_body(self.states[i].base_lin_vel, self.states[i].base_quat)
            elif s == "base_ang_vel":
                v = world_to_body(self.states[i].base_ang_vel, self.states[i].base_quat)
            elif s == "projected_gravity":
                v = projected_gravity(self.states[i].base_quat)
            elif s == "imu_orientation":
                v = self.states[i].base_quat
            elif s == "base_goal_xy":
                v = self._goal_body_xy(i)
            else:
                raise ValueError(f"observation source {s!r} not supported by DirectVecEnv")
            std = self.obs_noise.get(t.name, 0.0)
            if std > 0:
                v = np.asarray(v, np.float32) + self.rng.normal(0.0, std, size=np.shape(v)).astype(np.float32)
            frame[t.offset:t.end] = process_obs_term(v, t.spec)
        return frame

    def _obs(self, i: int) -> np.ndarray:
        frame = self._frame(i)
        if not self.frames[i]:
            self.frames[i] = [frame] * self.c.history_length
        else:
            self.frames[i] = (self.frames[i] + [frame])[-self.c.history_length:]
        return np.concatenate(self.frames[i]).astype(np.float32)

    def _q(self, i: int) -> np.ndarray:
        return self._joint(i, "position")[self.act_index]

    def _goal_body_xy(self, i: int) -> np.ndarray:
        st = self.states[i]
        d = np.array([self.goal[i][0] - st.base_pos[0], self.goal[i][1] - st.base_pos[1], 0.0])
        return (quat_to_rot(st.base_quat).T @ d)[:2]

    def _dist(self, i: int) -> float:
        return float(np.linalg.norm(self.goal[i][:2] - self.states[i].base_pos[:2]))

    def _begin_episode(self, i: int) -> None:
        if self.task.type == "joint_target":
            self.goal[i] = self.task.sample_joint_goal(self.rng)
        else:
            self.spawn_xy[i] = self.states[i].base_pos[:2]
            self.goal[i] = self.task.sample_base_goal(self.rng, self.spawn_xy[i])
            self.prev_dist[i] = self._dist(i)
        self.last_action[i] = 0.0
        self.frames[i] = []
        self.t[i] = 0
        self.action_queue[i] = [np.zeros(self.c.action_dim, np.float32) for _ in range(self.latency)]
        self.episode_terms[i] = {k: 0.0 for k in self.task.weights}
        self.last_q[i] = self._q(i)

    # -------------------------------------------------------------- VecEnv
    def reset(self) -> np.ndarray:
        t0 = time.perf_counter()
        self.states, _ = self.client.reset(self.entities)
        self.rpc_time += time.perf_counter() - t0
        for i in range(self.num_envs):
            self._begin_episode(i)
        return np.stack([self._obs(i) for i in range(self.num_envs)])

    def step_async(self, actions: np.ndarray) -> None:
        self._actions = np.asarray(actions, np.float32)

    def step_wait(self):
        n = self.num_envs
        a_off, a_end = self.act_term.offset, self.act_term.end
        commands = []
        prev_actions = self.last_action.copy()
        for i in range(n):
            raw = self._actions[i].copy()
            if self.latency > 0:
                # the policy's newest action enters the queue; the one applied now is `latency` steps old
                self.action_queue[i].append(raw.copy())
                raw = self.action_queue[i].pop(0)
            clipped, target = process_action_term(raw[a_off:a_end], self.act_term.spec)
            raw[a_off:a_end] = clipped
            self.last_action[i] = raw  # what the actuators received (also the last_action observation)
            if self.twist_term is not None:
                vx = float(target[0]); wz = float(target[-1])
                r, b = float(self.dd["wheel_radius"]), float(self.dd["wheel_separation"])
                target = np.array([(vx - wz * b / 2) / r, (vx + wz * b / 2) / r], dtype=np.float32)
                mode = "velocity"
            else:
                mode = self.act_term.spec.get("mode", "position")
            commands.append({"name": list(self.act_joints), mode: target})
        t0 = time.perf_counter()
        self.states, _ = self.client.step(self.entities, self.task.steps_per_action, commands)
        self.rpc_time += time.perf_counter() - t0
        self.step_count += 1

        dt = self.task.steps_per_action * (self.c.sim_dt_s or 0.02)
        obs = np.zeros((n, self.c.policy_input_dim), np.float32)
        rewards = np.zeros(n, np.float32)
        dones = np.zeros(n, bool)
        infos: list[dict] = []
        to_reset = []
        for i in range(n):
            q = self._q(i)
            qd = (q - self.last_q[i]) / dt
            self.last_q[i] = q
            if self.task.type == "joint_target":
                goal = self.goal[i][: len(self.act_joints)]
                r, contrib, reached = self.task.reward_joint(q, goal, qd, self.last_action[i], prev_actions[i])
                info = {"goal": goal.copy(), "q": q.copy(), "abs_err": float(np.mean(np.abs(q - goal)))}
            else:
                dist = self._dist(i)
                r, contrib, reached = self.task.reward_base(dist, self.prev_dist[i], qd,
                                                            self.last_action[i], prev_actions[i])
                self.prev_dist[i] = dist
                info = {"goal": self.goal[i].copy(), "q": q.copy(), "abs_err": dist,
                        "base_pos": self.states[i].base_pos.copy()}
            for k, v in contrib.items():
                self.episode_terms[i][k] += v
            rewards[i] = r
            self.t[i] += 1
            if reached or self.t[i] >= self.task.episode_steps:
                dones[i] = True
                if not reached:
                    info["TimeLimit.truncated"] = True
                # success: terminated at the goal, or (without termination) ended inside the radius
                info["success"] = bool(reached) or (self.task.type == "base_target"
                                                    and info["abs_err"] <= self.task.reach_radius)
                info["episode_terms"] = dict(self.episode_terms[i])
                info["terminal_observation"] = self._obs(i)
                to_reset.append(i)
            else:
                obs[i] = self._obs(i)
            infos.append(info)
        if to_reset:
            names = [self.entities[i] for i in to_reset]
            t0 = time.perf_counter()
            new_states, _ = self.client.reset(names)
            self.rpc_time += time.perf_counter() - t0
            for i, st in zip(to_reset, new_states):
                self.states[i] = st
                self._begin_episode(i)
                obs[i] = self._obs(i)
        return obs, rewards, dones, infos

    def close(self) -> None:
        self.client.close()

    # SB3 plumbing
    def get_attr(self, attr_name, indices=None):
        return [getattr(self, attr_name)] * self.num_envs

    def set_attr(self, attr_name, value, indices=None):
        setattr(self, attr_name, value)

    def env_method(self, method_name, *args, indices=None, **kwargs):
        return [getattr(self, method_name)(*args, **kwargs)] * self.num_envs

    def env_is_wrapped(self, wrapper_class, indices=None):
        return [False] * self.num_envs

    def seed(self, seed=None):
        self.rng = np.random.default_rng(seed)
        return [seed] * self.num_envs
