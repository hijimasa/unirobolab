"""Task definition shared by the training environments: goal sampling and reward terms.

Loaded from the ``task`` section of a ``*.train.json``. Kept apart from the contract on
purpose: the contract says how a policy observes and acts, the task says what it is
rewarded for.

Task types:
  joint_target  goal = target joint positions (the contract's ``command`` observation);
                terms tracking_l1 / tracking_l2 / action_rate / velocity
  base_target   goal = a point on the ground around the spawn pose (the contract's
                ``base_goal_xy`` observation gives it in the body frame);
                terms base_distance / base_progress / base_reached / action_rate / velocity.
                base_reached pays every step inside reach_radius; with terminate_on_reach the
                episode ends there instead (default: keep going and learn to hold the goal).
"""

from __future__ import annotations

from typing import Any

import numpy as np

JOINT_TERMS = ("tracking_l1", "tracking_l2", "action_rate", "velocity")
BASE_TERMS = ("base_distance", "base_progress", "base_reached", "action_rate", "velocity")


def make_task(cfg: dict[str, Any], n_goal: int, joints: list[str]):
    """task 節から Task (joint_target / base_target) か ConditionTask (conditions) を作る。"""
    if cfg.get("type") == "conditions":
        return ConditionTask(cfg, joints)
    return Task(cfg, n_goal)


class Task:
    def __init__(self, cfg: dict[str, Any], n_goal: int) -> None:
        self.cfg = cfg
        self.type = cfg.get("type", "joint_target")
        self.steps_per_action = int(cfg.get("physics_steps_per_action", 1))
        self.episode_steps = int(cfg.get("episode_steps", 100))
        self.weights: dict[str, float] = dict(cfg.get("reward", {"tracking_l1": -1.0}))
        if self.type == "joint_target":
            gr = cfg.get("goal_range", [-0.5, 0.5])
            self.goal_low = np.full(n_goal, gr[0], dtype=np.float32)
            self.goal_high = np.full(n_goal, gr[1], dtype=np.float32)
            allowed = JOINT_TERMS
        elif self.type == "base_target":
            self.goal_radius = tuple(cfg.get("goal_radius", [1.0, 2.5]))
            self.goal_angle = tuple(cfg.get("goal_angle_deg", [-180.0, 180.0]))
            self.reach_radius = float(cfg.get("reach_radius", 0.15))
            # terminate_on_reach=false keeps the episode running inside the goal radius, so the
            # policy also learns to stop there (a deployed controller does not get reset at the goal)
            self.terminate_on_reach = bool(cfg.get("terminate_on_reach", False))
            allowed = BASE_TERMS
        else:
            raise ValueError(f"unknown task type {self.type!r}")
        unknown = [k for k in self.weights if k not in allowed]
        if unknown:
            raise ValueError(f"unknown reward terms {unknown} for {self.type}; available: {allowed}")

    # ------------------------------------------------------------- goals
    def sample_joint_goal(self, rng: np.random.Generator) -> np.ndarray:
        return rng.uniform(self.goal_low, self.goal_high).astype(np.float32)

    def sample_base_goal(self, rng: np.random.Generator, spawn_xy: np.ndarray) -> np.ndarray:
        r = rng.uniform(*self.goal_radius)
        a = np.deg2rad(rng.uniform(*self.goal_angle))
        return (np.asarray(spawn_xy, np.float64) + r * np.array([np.cos(a), np.sin(a)])).astype(np.float32)

    # ------------------------------------------------------------ reward
    def reward_joint(self, q, goal, qd, action, prev_action) -> tuple[float, dict[str, float], bool]:
        terms = {
            "tracking_l1": float(np.sum(np.abs(q - goal))),
            "tracking_l2": float(np.sum((q - goal) ** 2)),
            "action_rate": float(np.sum(np.abs(action - prev_action))),
            "velocity": float(np.sum(np.abs(qd))),
        }
        contrib = {k: w * terms[k] for k, w in self.weights.items()}
        return float(sum(contrib.values())), contrib, False

    def reward_base(self, dist, prev_dist, qd, action, prev_action) -> tuple[float, dict[str, float], bool]:
        reached = dist <= self.reach_radius
        terms = {
            "base_distance": float(dist),
            "base_progress": float(prev_dist - dist),
            "base_reached": 1.0 if reached else 0.0,
            "action_rate": float(np.sum(np.abs(action - prev_action))),
            "velocity": float(np.sum(np.abs(qd))),
        }
        contrib = {k: w * terms[k] for k, w in self.weights.items()}
        return float(sum(contrib.values())), contrib, bool(reached and self.terminate_on_reach)


# ----------------------------------------------------------------------------- conditions
class Condition:
    """終了 (成功) 条件 1 つ。kind: joints_near | base_in_region | link_near。
    goal は毎エピソード抽選し、err は「今どれだけ満たしていないか」(rad / m)。"""

    def __init__(self, cfg: dict[str, Any], joints: list[str]) -> None:
        self.cfg = cfg
        self.kind = cfg["kind"]
        self.tolerance = float(cfg.get("tolerance", 0.05))
        self.joints = joints
        if self.kind == "joints_near":
            rng = cfg.get("range", [-0.5, 0.5])
            self.low = np.full(len(joints), float(rng[0]), np.float32)
            self.high = np.full(len(joints), float(rng[1]), np.float32)
        elif self.kind == "base_in_region":
            reg = cfg.get("region", {})
            self.radius = (float(reg.get("r_min", 1.0)), float(reg.get("r_max", 2.5)))
            self.angle = tuple(float(a) for a in reg.get("angle_deg", [-180.0, 180.0]))
        elif self.kind in ("link_near", "object_in_region"):
            if self.kind == "link_near":
                self.link = cfg["link"]
                self.chain = list(cfg["chain"])
                self.point = list(cfg.get("point") or [0.0, 0.0, 0.0])
            else:
                self.object = cfg["object"]
            reg = cfg.get("region", {"shape": "box", "center": [0.3, 0.0, 0.3], "size": [0.2, 0.2, 0.2]})
            self.center = np.asarray(reg.get("center", [0.0, 0.0, 0.0]), np.float32)
            self.half = 0.5 * np.asarray(reg.get("size", [0.2, 0.2, 0.2]), np.float32) if reg.get("shape", "box") == "box" \
                else np.full(3, float(reg.get("radius", 0.1)), np.float32)
            self.planar = bool(cfg.get("planar", self.kind == "object_in_region"))   # 物体は高さを問わない (地面の上を動かす)
        else:
            raise ValueError(f"unknown condition kind {self.kind!r}")

    def sample(self, rng: np.random.Generator, spawn_xy: np.ndarray) -> np.ndarray:
        if self.kind == "joints_near":
            return rng.uniform(self.low, self.high).astype(np.float32)
        if self.kind == "base_in_region":
            r = rng.uniform(*self.radius); a = np.deg2rad(rng.uniform(*self.angle))
            return (np.asarray(spawn_xy, np.float64) + r * np.array([np.cos(a), np.sin(a)])).astype(np.float32)
        return (self.center + rng.uniform(-self.half, self.half)).astype(np.float32)

    def error(self, goal: np.ndarray, ctx: dict[str, Any]) -> float:
        """ctx: q (行動関節の角度), q_by_name, base_pos (world), objects {name: 根リンク座標系の位置}"""
        if self.kind == "joints_near":
            q = ctx["q"]
            return float(np.mean(np.abs(q - goal[: len(q)])))
        if self.kind == "base_in_region":
            return float(np.linalg.norm(goal[:2] - np.asarray(ctx["base_pos"])[:2]))
        if self.kind == "object_in_region":
            p = np.asarray(ctx["objects"][self.object], np.float32)
            d = goal[:3] - p
            if self.planar:
                d = d[:2]
            return float(np.linalg.norm(d))
        from .fk import fk_point
        return float(np.linalg.norm(goal - fk_point(self.chain, ctx["q_by_name"], self.point)))


class ConditionTask:
    """task.type == "conditions": 条件の列から報酬・成功・終了を組む。
    報酬項 (reward の重み): progress (誤差の減り分)、distance (誤差)、reached (許容内で 1)、action_rate、velocity。"""

    TERMS = ("progress", "distance", "reached", "action_rate", "velocity", "reach_progress", "reach_distance")

    def __init__(self, cfg: dict[str, Any], joints: list[str]) -> None:
        self.cfg = cfg
        self.type = "conditions"
        self.steps_per_action = int(cfg.get("physics_steps_per_action", 1))
        self.episode_steps = int(cfg.get("episode_steps", 100))
        self.weights: dict[str, float] = dict(cfg.get("reward", {"progress": 5.0, "distance": -0.5, "reached": 1.0, "action_rate": -0.05}))
        unknown = [k for k in self.weights if k not in self.TERMS]
        if unknown:
            raise ValueError(f"unknown reward terms {unknown} for conditions; available: {self.TERMS}")
        self.conditions = [Condition(c, joints) for c in cfg.get("conditions", [])]
        if not self.conditions:
            raise ValueError("conditions task needs at least one condition")
        self.stop_at_goal = bool(cfg.get("stop_at_goal", False))
        self.hold_steps = int(cfg.get("hold_steps", 0))
        # 整形 (物体タスク): 手先 (link の点) が物体に近づく分を reach_* で報いる。疎な成功だけでは学習が進まないため
        self.reach = cfg.get("reach")   # {"link", "chain", "point", "object"} or None
        self.objects: list[dict[str, Any]] = list(cfg.get("objects", []))

    def reach_distance(self, ctx: dict[str, Any]) -> float | None:
        if not self.reach:
            return None
        from .fk import fk_point
        p = fk_point(self.reach["chain"], ctx["q_by_name"], self.reach.get("point"))
        return float(np.linalg.norm(np.asarray(ctx["objects"][self.reach["object"]], np.float32) - p))

    def by_kind(self, kind: str) -> Condition | None:
        for c in self.conditions:
            if c.kind == kind:
                return c
        return None

    def reward(self, errs: list[float], prev_errs: list[float], qd, action, prev_action,
               reach: float | None = None, prev_reach: float | None = None) -> tuple[float, dict[str, float], bool]:
        reached_all = all(e <= c.tolerance for e, c in zip(errs, self.conditions))
        terms = {
            "progress": float(sum(p - e for p, e in zip(prev_errs, errs))),
            "distance": float(sum(errs)),
            "reached": 1.0 if reached_all else 0.0,
            "action_rate": float(np.sum(np.abs(action - prev_action))),
            "velocity": float(np.sum(np.abs(qd))),
            "reach_progress": float(prev_reach - reach) if reach is not None and prev_reach is not None else 0.0,
            "reach_distance": float(reach) if reach is not None else 0.0,
        }
        contrib = {k: w * terms[k] for k, w in self.weights.items()}
        return float(sum(contrib.values())), contrib, bool(reached_all and self.stop_at_goal)
