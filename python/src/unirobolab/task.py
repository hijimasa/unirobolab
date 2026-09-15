"""Task definition shared by the training environments: goal sampling and reward terms.

Loaded from the ``task`` section of a ``*.train.json``. Kept apart from the contract on
purpose: the contract says how a policy observes and acts, the task says what it is
rewarded for.
"""

from __future__ import annotations

from typing import Any

import numpy as np

REWARD_TERMS = ("tracking_l1", "tracking_l2", "action_rate", "velocity")


class Task:
    def __init__(self, cfg: dict[str, Any], n_goal: int) -> None:
        self.cfg = cfg
        self.steps_per_action = int(cfg.get("physics_steps_per_action", 1))
        self.episode_steps = int(cfg.get("episode_steps", 100))
        gr = cfg.get("goal_range", [-0.5, 0.5])
        self.goal_low = np.full(n_goal, gr[0], dtype=np.float32)
        self.goal_high = np.full(n_goal, gr[1], dtype=np.float32)
        self.weights: dict[str, float] = dict(cfg.get("reward", {"tracking_l1": -1.0}))
        unknown = [k for k in self.weights if k not in REWARD_TERMS]
        if unknown:
            raise ValueError(f"unknown reward terms {unknown}; available: {REWARD_TERMS}")

    def sample_goal(self, rng: np.random.Generator) -> np.ndarray:
        return rng.uniform(self.goal_low, self.goal_high).astype(np.float32)

    def reward(self, q: np.ndarray, goal: np.ndarray, qd: np.ndarray,
               action: np.ndarray, prev_action: np.ndarray) -> tuple[float, dict[str, float]]:
        terms = {
            "tracking_l1": float(np.sum(np.abs(q - goal))),
            "tracking_l2": float(np.sum((q - goal) ** 2)),
            "action_rate": float(np.sum(np.abs(action - prev_action))),
            "velocity": float(np.sum(np.abs(qd))),
        }
        contrib = {k: w * terms[k] for k, w in self.weights.items()}
        return float(sum(contrib.values())), contrib
