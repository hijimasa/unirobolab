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
from unirobolab.geometry import projected_gravity, quat_to_rot, world_to_body, yaw_of
from unirobolab.obs_math import integrate_relative, process_action_term, process_obs_term
from unirobolab.task import make_task


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
        self.task = make_task(task_cfg, self.cmd_term.size if self.cmd_term else 2, list(self.act_joints))
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
        # conditions タスク: 条件ごとの目標 (kind → 配列) と直前の誤差
        self.cgoals: list[dict[str, np.ndarray]] = [{} for _ in range(n)]
        self.prev_errs: list[list[float]] = [[] for _ in range(n)]
        self.prev_reach: list[float | None] = [None for _ in range(n)]
        # 物体: env ごとに複製 (エンティティ名 <robot>__<object>)。状態はロボットと同じ STEP/RESET で返る
        self.objects: list[dict] = list(task_cfg.get("objects", [])) if self.task.type == "conditions" else []
        self.obj_entities: list[list[str]] = [[f"{e}__{o['name']}" for o in self.objects] for e in entities]
        self.obj_states: list[dict[str, "EntityState"]] = [{} for _ in range(n)]
        if self.objects and spawn_urdf:
            from unirobolab.objects import write_object_urdfs
            import os
            paths = write_object_urdfs(self.objects, os.path.join(os.path.dirname(os.path.abspath(spawn_urdf)), "objects"))
            for i, e in enumerate(entities):
                for o in self.objects:
                    ent = f"{e}__{o['name']}"
                    try:
                        self.client.info([ent])   # 既にある (前回の学習の残り) ならそのまま使う
                        continue
                    except LearningServerError:
                        pass
                    got = self.client.spawn(ent, paths[o["name"]], 10.0 + i, 10.0, 0.0, 0.0)   # 置き場所は最初のリセットで決める
                    if got != ent:
                        raise RuntimeError(f"object entity name clash: wanted {ent}, got {got}")
        self.last_action = np.zeros((n, c.action_dim), np.float32)
        self.start_joints = task_cfg.get("start_joints") if self.twist_term is None else None
        # 物理の domain randomization (エピソードごと): {"object_mass": [lo, hi] 倍率, "object_friction": [lo, hi] 係数,
        # "drive_gain": [lo, hi] 倍率}。抽選した値は info["dynamics"] に出す (特権情報として後で使える)
        self.randomize: dict = task_cfg.get("randomize") or {}
        self.dynamics: list[dict] = [{} for _ in range(n)]
        # relative position actions: the integrated target per env (reset to the measured q at episode start)
        self.relative = self.twist_term is None and bool(self.act_term.spec.get("relative")) and self.act_term.spec.get("mode", "position") == "position"
        self.rel_target = np.zeros((n, len(self.act_joints)), np.float32)
        self.rel_limits = [c.safety.get("joint_limits", {}).get(j) for j in self.act_joints] if c.safety.get("joint_limits") else None
        if self.rel_limits is not None and any(l is None for l in self.rel_limits):
            self.rel_limits = None
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

    def _q_by_name(self, i: int) -> dict[str, float]:
        st = self.states[i]
        return {n: float(p) for n, p in zip(st.names, st.position)}

    def _root_from_world(self, i: int, p_world: np.ndarray) -> np.ndarray:
        st = self.states[i]
        return (quat_to_rot(st.base_quat).T @ (np.asarray(p_world, np.float64) - np.asarray(st.base_pos, np.float64))).astype(np.float32)

    def _world_from_root(self, i: int, p_root: np.ndarray) -> np.ndarray:
        st = self.states[i]
        return (quat_to_rot(st.base_quat) @ np.asarray(p_root, np.float64) + np.asarray(st.base_pos, np.float64))

    def _object_positions(self, i: int) -> dict[str, np.ndarray]:
        return {o["name"]: self._root_from_world(i, self.obj_states[i][o["name"]].base_pos) for o in self.objects if o["name"] in self.obj_states[i]}

    def _ctx(self, i: int) -> dict:
        return {"q": self._q(i), "q_by_name": self._q_by_name(i), "base_pos": self.states[i].base_pos, "objects": self._object_positions(i)}

    def _cond_goal(self, i: int, kind: str, size: int) -> np.ndarray:
        g = self.cgoals[i].get(kind)
        return g if g is not None else np.zeros(size, np.float32)

    def _frame(self, i: int) -> np.ndarray:
        from unirobolab.fk import fk_point
        frame = np.zeros(self.c.obs_dim, np.float32)
        conds = self.task.type == "conditions"
        for t in self.c.observations:
            s = t.source
            if s == "joint_position":
                v = self._select(self._joint(i, "position"), t.spec)
            elif s == "joint_velocity":
                v = self._select(self._joint(i, "velocity"), t.spec)
            elif s == "joint_effort":
                v = self._select(self._joint(i, "effort"), t.spec)
            elif s == "command":
                v = self._cond_goal(i, "joints_near", t.size) if conds else self.goal[i]
            elif s == "link_position":
                v = fk_point(t.spec["chain"], self._q_by_name(i), t.spec.get("point"))
            elif s == "link_goal":
                v = self._cond_goal(i, "link_near", 3) - fk_point(t.spec["chain"], self._q_by_name(i), t.spec.get("point"))
            elif s == "object_position":
                v = self._object_positions(i).get(t.spec["object"], np.zeros(3, np.float32))
            elif s == "object_goal":
                v = self._cond_goal(i, "object_in_region", 3) - self._object_positions(i).get(t.spec["object"], np.zeros(3, np.float32))
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
        g = self._cond_goal(i, "base_in_region", 2) if self.task.type == "conditions" else self.goal[i]
        d = np.array([g[0] - st.base_pos[0], g[1] - st.base_pos[1], 0.0])
        return (quat_to_rot(st.base_quat).T @ d)[:2]

    def _cond_errs(self, i: int) -> list[float]:
        ctx = self._ctx(i)
        return [c.error(self.cgoals[i][c.kind], ctx) for c in self.task.conditions]

    def _randomize_dynamics(self, i: int) -> None:
        """エピソードごとに物体の質量・摩擦とロボットの駆動ゲインを抽選して SET_DYNAMICS で反映する。"""
        rz = self.randomize
        u = lambda key: float(self.rng.uniform(float(rz[key][0]), float(rz[key][1]))) if rz.get(key) else None
        d = {}
        g = u("drive_gain")
        if g is not None:
            self.states[i] = self.client.set_dynamics(self.entities[i], 1.0, -1.0, g); d["drive_gain"] = g
        m, f = u("object_mass"), u("object_friction")
        if (m is not None or f is not None) and self.objects:
            for o in self.objects:
                self.obj_states[i][o["name"]] = self.client.set_dynamics(f"{self.entities[i]}__{o['name']}", m if m is not None else 1.0, f if f is not None else -1.0, 1.0)
            if m is not None: d["object_mass"] = m
            if f is not None: d["object_friction"] = f
        self.dynamics[i] = d

    def _place_objects(self, i: int) -> None:
        """エピソード開始: 各物体を開始条件の範囲 (根リンク座標系) から抽選した位置・向きへ置く。"""
        for o in self.objects:
            st = o.get("start", {})
            c = np.asarray(st.get("center", [0.3, 0.0, 0.0]), np.float64); half = 0.5 * np.asarray(st.get("size", [0.1, 0.1, 0.0]), np.float64)
            p_root = c + self.rng.uniform(-half, half)
            yaw_rng = st.get("yaw_deg", [0.0, 0.0])
            yaw = np.deg2rad(self.rng.uniform(float(yaw_rng[0]), float(yaw_rng[1]))) + yaw_of(self.states[i].base_quat)
            p_world = self._world_from_root(i, p_root)
            self.obj_states[i][o["name"]] = self.client.set_pose(f"{self.entities[i]}__{o['name']}", float(p_world[0]), float(p_world[1]), float(p_world[2]), float(yaw))

    def _dist(self, i: int) -> float:
        return float(np.linalg.norm(self.goal[i][:2] - self.states[i].base_pos[:2]))

    def _begin_episode(self, i: int) -> None:
        if self.start_joints is not None and self.start_joints.get("mode") == "random":
            # 開始姿勢のばらつき: 関節を可動範囲 (safety.joint_limits) の fraction 倍の中から抽選して直接置く
            fr = float(self.start_joints.get("fraction", 0.5))
            lim = self.c.safety.get("joint_limits") or {}
            reach = getattr(self.task, "reach", None) if self.task.type == "conditions" else None
            for _ in range(20):
                q0 = np.array([self.rng.uniform(fr * lim[j][0], fr * lim[j][1]) if j in lim else self.rng.uniform(-fr, fr) for j in self.act_joints], np.float32)
                if not (reach and self.objects):
                    break
                # 物体タスク: 手先が物体の開始位置の真上に来る姿勢は避ける (テレポートで物体に食い込む)
                from unirobolab.fk import fk_point
                hand = fk_point(reach["chain"], dict(zip(self.act_joints, q0.tolist())), reach.get("point"))
                clear = all(np.linalg.norm(hand[:2] - np.asarray(o.get("start", {}).get("center", [0.3, 0.0, 0.0])[:2])) > 0.15 for o in self.objects)
                if clear:
                    break
            self.states[i] = self.client.set_joints(self.entities[i], list(self.act_joints), q0)
        if self.randomize:
            self._randomize_dynamics(i)
        if self.task.type == "conditions":
            self.spawn_xy[i] = self.states[i].base_pos[:2]
            if self.objects:
                self._place_objects(i)
            self.cgoals[i] = {c.kind: c.sample(self.rng, self.spawn_xy[i]) for c in self.task.conditions}
            self.prev_errs[i] = self._cond_errs(i)
            self.prev_reach[i] = self.task.reach_distance(self._ctx(i))
        elif self.task.type == "joint_target":
            self.goal[i] = self.task.sample_joint_goal(self.rng)
        else:
            self.spawn_xy[i] = self.states[i].base_pos[:2]
            self.goal[i] = self.task.sample_base_goal(self.rng, self.spawn_xy[i])
            self.prev_dist[i] = self._dist(i)
        self.last_action[i] = 0.0
        if self.relative:
            self.rel_target[i] = self._q(i)[[self.c.joints.index(j) for j in self.act_joints]]
        self.frames[i] = []
        self.t[i] = 0
        self.action_queue[i] = [np.zeros(self.c.action_dim, np.float32) for _ in range(self.latency)]
        self.episode_terms[i] = {k: 0.0 for k in self.task.weights}
        self.last_q[i] = self._q(i)

    # -------------------------------------------------------------- VecEnv
    def _all_names(self) -> list[str]:
        return list(self.entities) + [o for objs in self.obj_entities for o in objs]

    def _split(self, states: list) -> list:
        """STEP/RESET の応答 (ロボット + 物体) をロボット分と物体分に分ける。"""
        n = self.num_envs
        robots = states[:n]
        k = n
        for i in range(n):
            for o in self.objects:
                self.obj_states[i][o["name"]] = states[k]; k += 1
        return robots

    def _step_all(self, steps: int, commands: list) -> tuple[list, float]:
        if not self.objects:
            return self.client.step(self.entities, steps, commands)
        states, t = self.client.step(self._all_names(), steps, commands + [{"name": []} for _ in range(len(self.objects) * self.num_envs)])
        return self._split(states), t

    def _reset_all(self, indices: list[int] | None = None) -> tuple[list, float]:
        if indices is None:
            indices = list(range(self.num_envs))
        names = [self.entities[i] for i in indices] + [o for i in indices for o in self.obj_entities[i]]
        states, t = self.client.reset(names)
        robots = states[:len(indices)]
        k = len(indices)
        for i in indices:
            for o in self.objects:
                self.obj_states[i][o["name"]] = states[k]; k += 1
        return robots, t

    def reset(self) -> np.ndarray:
        t0 = time.perf_counter()
        self.states, _ = self._reset_all()
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
                if self.relative:
                    target = integrate_relative(self.rel_target[i], target, self.rel_limits)
                    self.rel_target[i] = target
            commands.append({"name": list(self.act_joints), mode: target})
        t0 = time.perf_counter()
        self.states, _ = self._step_all(self.task.steps_per_action, commands)
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
            if self.task.type == "conditions":
                errs = self._cond_errs(i)
                reach = self.task.reach_distance(self._ctx(i))
                r, contrib, reached = self.task.reward(errs, self.prev_errs[i], qd, self.last_action[i], prev_actions[i], reach, self.prev_reach[i])
                self.prev_errs[i] = errs; self.prev_reach[i] = reach
                info = {"goal": np.concatenate([np.asarray(self.cgoals[i][c.kind]).ravel() for c in self.task.conditions]),
                        "q": q.copy(), "abs_err": float(max(errs)), "errs": list(errs),
                        "cond_ok": all(e <= c.tolerance for e, c in zip(errs, self.task.conditions))}
            elif self.task.type == "joint_target":
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
                                                    and info["abs_err"] <= self.task.reach_radius) \
                    or (self.task.type == "conditions" and bool(info.get("cond_ok")))
                info["episode_terms"] = dict(self.episode_terms[i])
                if self.dynamics[i]:
                    info["dynamics"] = dict(self.dynamics[i])
                info["terminal_observation"] = self._obs(i)
                to_reset.append(i)
            else:
                obs[i] = self._obs(i)
            infos.append(info)
        if to_reset:
            t0 = time.perf_counter()
            new_states, _ = self._reset_all(to_reset)
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
