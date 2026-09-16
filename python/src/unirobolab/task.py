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
                The episode ends with success when the base is within reach_radius.
"""

from __future__ import annotations

from typing import Any

import numpy as np

JOINT_TERMS = ("tracking_l1", "tracking_l2", "action_rate", "velocity")
BASE_TERMS = ("base_distance", "base_progress", "base_reached", "action_rate", "velocity")


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
        return float(sum(contrib.values())), contrib, bool(reached)
