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
from unirobolab.obs_math import process_action_term, process_obs_term
from unirobolab.task import Task


class DirectVecEnv(VecEnv):
    def __init__(self, c: Contract, task_cfg: dict[str, Any], entities: list[str],
                 host: str = "127.0.0.1", port: int = 10100, seed: int = 0) -> None:
        self.c = c
        self.entities = list(entities)
        n = len(self.entities)
        self.cmd_term = c.obs_terms_by_source("command")[0]
        self.act_term = [t for t in c.actions if t.source == "joints"][0]
        self.act_joints = self.act_term.joints or c.joints
        self.task = Task(task_cfg, self.cmd_term.size)
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

        info = self.client.info(self.entities)
        self.index: list[list[int]] = []
        for name, st in zip(self.entities, info):
            missing = [j for j in c.joints if j not in st.names]
            if missing:
                raise RuntimeError(f"entity {name}: joints {missing} not found (has {st.names})")
            self.index.append([st.names.index(j) for j in c.joints])
        self.act_index = [c.joints.index(j) for j in self.act_joints]

        self.goal = np.zeros((n, self.cmd_term.size), np.float32)
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
            else:
                raise ValueError(f"observation source {s!r} not supported by DirectVecEnv")
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

    def _begin_episode(self, i: int) -> None:
        self.goal[i] = self.task.sample_goal(self.rng)
        self.last_action[i] = 0.0
        self.frames[i] = []
        self.t[i] = 0
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
            clipped, target = process_action_term(raw[a_off:a_end], self.act_term.spec)
            raw[a_off:a_end] = clipped
            self.last_action[i] = raw
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
            goal = self.goal[i][: len(self.act_joints)]
            r, contrib = self.task.reward(q, goal, qd, self.last_action[i], prev_actions[i])
            for k, v in contrib.items():
                self.episode_terms[i][k] += v
            rewards[i] = r
            self.t[i] += 1
            info = {"goal": goal.copy(), "q": q.copy(), "abs_err": float(np.mean(np.abs(q - goal)))}
            if self.t[i] >= self.task.episode_steps:
                dones[i] = True
                info["TimeLimit.truncated"] = True
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
