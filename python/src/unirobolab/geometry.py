"""Small quaternion helpers (x, y, z, w convention, ROS axes)."""

from __future__ import annotations

import numpy as np


def quat_to_rot(q: np.ndarray) -> np.ndarray:
    x, y, z, w = (float(v) for v in q)
    n = x * x + y * y + z * z + w * w
    if n < 1e-12:
        return np.eye(3)
    s = 2.0 / n
    return np.array([
        [1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w)],
        [s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w)],
        [s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y)],
    ])


def world_to_body(v_world: np.ndarray, q: np.ndarray) -> np.ndarray:
    """Rotate a world-frame vector into the body frame of orientation q."""
    return quat_to_rot(q).T @ np.asarray(v_world, dtype=np.float64)


def projected_gravity(q: np.ndarray) -> np.ndarray:
    """Unit gravity vector (0, 0, -1 in world) expressed in the body frame."""
    return world_to_body(np.array([0.0, 0.0, -1.0]), q)


def yaw_of(q: np.ndarray) -> float:
    x, y, z, w = (float(v) for v in q)
    return float(np.arctan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z)))
