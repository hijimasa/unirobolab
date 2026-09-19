"""Run a policy in real time against the simulator through the learning server.

No ROS 2 involved: the simulator is put into PLAYING, and every policy period the
runner reads the entity state (observe-only STEP), assembles the observation with
the same functions as the deployed node (``obs_math``), runs the ONNX policy with
onnxruntime and sends the command back. This is what the simulator's GUI launches
to "run a policy" without a ROS graph, and it doubles as a quick check of a
contract + ONNX pair.

Goals: ``--goal`` sets the initial goal (command / base_goal_xy / link_goal / object_goal term);
later goals can be fed on stdin as lines ``goal v1 v2 ...``. Object tasks need the object
definitions (``--objects-from task.json``): the objects are spawned as ``<entity>__<name>``,
placed at their start centre, observed with the robot, and ``object <name> [x y [yaw]]`` on
stdin puts one back (root-link frame). Status lines (JSON) go to stdout
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
from unirobolab.geometry import projected_gravity, quat_to_rot, world_to_body, yaw_of
from unirobolab.obs_math import integrate_relative, process_action_term, process_obs_term


class LivePolicy:
    def __init__(self, c: Contract, entity: str, onnx_path: str, host: str = "127.0.0.1", port: int = 10100,
                 goal: list[float] | None = None, objects: list[dict] | None = None, objects_dir: str | None = None) -> None:
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
        cmds = (c.obs_terms_by_source("command") + c.obs_terms_by_source("base_goal_xy")
                + c.obs_terms_by_source("link_goal") + c.obs_terms_by_source("object_goal"))
        self.goal_size = cmds[0].size if cmds else 0
        self.goal = np.zeros(self.goal_size, np.float32)
        if goal is not None:
            self.set_goal(goal)
        self.last_action = np.zeros(c.action_dim, np.float32)
        self.frames: list[np.ndarray] = []
        self.state = st
        self.relative = bool(self.act_term.spec.get("relative")) and self.act_term.spec.get("mode", "position") == "position"
        self.rel_target = self._joint("position")[[c.joints.index(j) for j in self.act_joints]] if self.relative else None
        lim = c.safety.get("joint_limits") or {}
        self.rel_limits = [lim[j] for j in self.act_joints] if all(j in lim for j in self.act_joints) else None
        self.steps = 0
        self.errors: list[str] = []
        # 物体 (押す・運ぶ対象): 契約の object_* 項が参照する物体を <entity>__<name> として用意し、
        # ロボットと一緒に観測する。位置は学習時と同じく根リンク座標系。
        self._objects_def = objects or []
        self.obj_states: dict[str, "EntityState"] = {}
        self.objects = self._setup_objects(self._objects_def, objects_dir)
        # 学習で変えた質量・摩擦・ゲインが残っていれば公称値に戻す (古いプレイヤーでは非対応)
        try:
            for ent in [entity] + [self._obj_entity(n) for n in self.objects]:
                self.client.set_dynamics(ent, 1.0, -1.0, 1.0)
        except Exception as err:  # noqa: BLE001
            if "unknown op" not in str(err):
                raise

    def _obj_entity(self, name: str) -> str:
        return f"{self.entity}__{name}"

    def _setup_objects(self, objects: list[dict], objects_dir: str | None) -> list[str]:
        needed = sorted({t.spec["object"] for t in self.c.observations if t.source in ("object_position", "object_goal")})
        if not needed:
            return []
        by_name = {o["name"]: o for o in objects}
        unknown = [n for n in needed if n not in by_name]
        if unknown:
            raise RuntimeError(f"contract observes objects {unknown} but no object definition was given (--objects-from task.json)")
        from unirobolab.objects import write_object_urdfs
        import os, tempfile
        paths = write_object_urdfs([by_name[n] for n in needed], objects_dir or os.path.join(tempfile.gettempdir(), "unirobolab_objects"))
        for n in needed:
            ent = self._obj_entity(n)
            try:
                self.client.info([ent])
            except Exception:
                self.client.spawn(ent, os.path.abspath(paths[n]), 10.0, 10.0, 0.0, 0.0)
            self.place_object(n)   # 開始条件の中心へ (学習時のばらつき無し)
        return needed

    def place_object(self, name: str, xy: list[float] | None = None, yaw: float | None = None) -> None:
        """物体を根リンク座標系の (x, y) と向きへ置く。省略時は開始条件の中心。"""
        o = next((o for o in self._objects_def if o["name"] == name), None)
        if o is None:
            raise ValueError(f"unknown object {name!r} (have {self.objects})")
        st = o.get("start", {})
        c = np.asarray(st.get("center", [0.3, 0.0, 0.0]), np.float64)
        p_root = np.array([xy[0], xy[1], c[2]], np.float64) if xy is not None else c
        base = self.state
        p_world = quat_to_rot(base.base_quat) @ p_root + np.asarray(base.base_pos, np.float64)
        yaw_w = (yaw if yaw is not None else 0.0) + yaw_of(base.base_quat)
        self.obj_states[name] = self.client.set_pose(self._obj_entity(name), float(p_world[0]), float(p_world[1]), float(p_world[2]), float(yaw_w))

    def _object_position(self, name: str) -> np.ndarray:
        st = self.obj_states.get(name)
        if st is None:
            return np.zeros(3, np.float32)
        base = self.state
        return (quat_to_rot(base.base_quat).T @ (np.asarray(st.base_pos, np.float64) - np.asarray(base.base_pos, np.float64))).astype(np.float32)

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
            elif s in ("link_position", "link_goal"):
                from unirobolab.fk import fk_point
                p = fk_point(t.spec["chain"], {n: float(q) for n, q in zip(st.names, st.position)}, t.spec.get("point"))
                v = p if s == "link_position" else self.goal[:3] - p
            elif s in ("object_position", "object_goal"):
                p = self._object_position(t.spec["object"])
                v = p if s == "object_position" else self.goal[:3] - p
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
        if self.relative:
            target = integrate_relative(self.rel_target, target, self.rel_limits)
            self.rel_target = target
        names = [self.entity] + [self._obj_entity(n) for n in self.objects]
        states, _ = self.client.observe(names, [{"name": list(self.act_joints), mode: target}] + [{"name": []} for _ in self.objects])
        self.state = states[0]
        for n, st in zip(self.objects, states[1:]):
            self.obj_states[n] = st
        self.steps += 1
        return x[0], raw

    def error_to_goal(self) -> float | None:
        if self.goal_size == 0:
            return None
        if self.c.obs_terms_by_source("base_goal_xy"):
            return float(np.linalg.norm(self.goal[:2] - self.state.base_pos[:2]))
        og = self.c.obs_terms_by_source("object_goal")
        if og:
            return float(np.linalg.norm((self.goal[:3] - self._object_position(og[0].spec["object"]))[:2]))   # 平面
        lg = self.c.obs_terms_by_source("link_goal")
        if lg:
            from unirobolab.fk import fk_point
            st = self.state
            p = fk_point(lg[0].spec["chain"], {n: float(q) for n, q in zip(st.names, st.position)}, lg[0].spec.get("point"))
            return float(np.linalg.norm(self.goal[:3] - p))
        q = self._joint("position")[[self.c.joints.index(j) for j in self.act_joints]]
        return float(np.mean(np.abs(q - self.goal[: len(self.act_joints)])))


def run(c: Contract, entity: str, onnx_path: str, host: str, port: int, goal: list[float] | None,
        duration_s: float | None, log_path: str | None, play: bool = True,
        objects: list[dict] | None = None, objects_dir: str | None = None) -> int:
    lp = LivePolicy(c, entity, onnx_path, host, port, goal, objects=objects, objects_dir=objects_dir)
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
            elif parts and parts[0] == "object" and len(parts) >= 2:   # "object <name> [x y [yaw]]": 物体を置き直す
                try:
                    xy = [float(parts[2]), float(parts[3])] if len(parts) >= 4 else None
                    lp.place_object(parts[1], xy, float(parts[4]) if len(parts) >= 5 else None)
                except (ValueError, StopIteration, Exception) as e:
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
                                  "err": lp.error_to_goal(), "goal": lp.goal.tolist(),
                                  "base": bool(lp.c.obs_terms_by_source("base_goal_xy")),
                                  "objects": {n: lp._object_position(n).round(4).tolist() for n in lp.objects},
                                  "kind": "base" if lp.c.obs_terms_by_source("base_goal_xy") else ("object" if lp.c.obs_terms_by_source("object_goal") else ("link" if lp.c.obs_terms_by_source("link_goal") else "joint"))}), flush=True)
                ticks = 0; last_status = now
    finally:
        stop.set()
        if log:
            log.close()
        lp.client.close()
    err = lp.error_to_goal()
    print(json.dumps({"done": True, "steps": lp.steps, "final_err": err}), flush=True)
    return 0
