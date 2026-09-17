"""Observation/action processing shared by the training side.

This is a verbatim copy of the two functions in ``ros2/policy_node.py``. The node
must stay free of ``unirobolab`` imports (it is shipped inside generated ROS 2
packages), so the copy lives here and ``tests/test_obs_math.py`` checks that
both copies are identical.
"""

from __future__ import annotations

import numpy as np


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


def integrate_relative(prev: np.ndarray, delta: np.ndarray, limits: list | None) -> np.ndarray:
    """relative position actions: previous target + increment, clamped to [lo, hi] per joint when given."""
    t = np.asarray(prev, np.float32) + np.asarray(delta, np.float32)
    if limits:
        lo = np.array([l[0] for l in limits], np.float32); hi = np.array([l[1] for l in limits], np.float32)
        t = np.clip(t, lo, hi)
    return t


def fk_point(chain: list, q: dict, point=None) -> np.ndarray:
    """chain の段を根から順に適用し、最後のリンク座標系の point (既定: 原点) を根の座標系で返す。"""
    def rpy_rot(r: float, p: float, y: float) -> np.ndarray:
        cr, sr, cp, sp, cy, sy = np.cos(r), np.sin(r), np.cos(p), np.sin(p), np.cos(y), np.sin(y)
        return np.array([[cy * cp, cy * sp * sr - sy * cr, cy * sp * cr + sy * sr],
                         [sy * cp, sy * sp * sr + cy * cr, sy * sp * cr - cy * sr],
                         [-sp, cp * sr, cp * cr]])

    def axis_rot(axis: np.ndarray, a: float) -> np.ndarray:
        k = axis / (np.linalg.norm(axis) or 1.0)
        K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
        return np.eye(3) + np.sin(a) * K + (1 - np.cos(a)) * (K @ K)

    R = np.eye(3); t = np.zeros(3)
    for s in chain:
        t = t + R @ np.asarray(s.get("xyz", [0, 0, 0]), float)
        R = R @ rpy_rot(*[float(v) for v in s.get("rpy", [0, 0, 0])])
        jn = s.get("joint")
        if jn is not None:
            a = float(q.get(jn, 0.0)); axis = np.asarray(s.get("axis", [1, 0, 0]), float)
            if s.get("type") == "prismatic":
                t = t + R @ (axis / (np.linalg.norm(axis) or 1.0) * a)
            else:
                R = R @ axis_rot(axis, a)
    return (t + R @ np.asarray(point if point is not None else [0.0, 0.0, 0.0], float)).astype(np.float32)
